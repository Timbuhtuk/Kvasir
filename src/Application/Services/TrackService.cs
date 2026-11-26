using Application.Interfaces;
using Entities.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Application.Services;

public sealed class TrackService(IServiceScopeFactory scopes, IMediaCache cache) : IAsyncDisposable
{
    private sealed class Request(Song song)
    {
        public Song Song { get; } = song;
        public TaskCompletionSource<Exception?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _sync = new();
    private readonly LinkedList<Request> _queue = new();
    private readonly Dictionary<int, LinkedListNode<Request>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task _worker = Task.CompletedTask;
    private bool _disposed;

    public async Task<List<Song>?> FindAsync(string query, int capacity)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISongRepository>().GetByQueryAsync(query, capacity);
    }

    public async Task RecordPlayAsync(Song song)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISongRepository>().IncrementViewsAsync(song);
    }

    public int QueuedCount { get { lock (_sync) return _queue.Count; } }

    public void Schedule(IEnumerable<Song> songs)
    {
        lock (_sync)
            foreach (Song song in songs) ScheduleLocked(song, false);
    }

    private Request ScheduleLocked(Song song, bool next)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pending.TryGetValue(song.Id, out var node))
        {
            if (next && node.List != null)
            {
                _queue.Remove(node);
                _queue.AddFirst(node);
            }
            return node.Value;
        }

        var request = new Request(song);
        _pending.Add(song.Id, next ? _queue.AddFirst(request) : _queue.AddLast(request));
        if (_worker.IsCompleted) _worker = Task.Run(ProcessAsync);
        return request;
    }

    public async Task<Stream> OpenAsync(Song song, CancellationToken token)
    {
        Stream? ready = await cache.TryOpenReadyAsync(song, token);
        if (ready == null)
        {
            await PrepareAsync(song, token, next: true);
            ready = await cache.OpenAsync(song, token);
        }
        return await cache.OpenPlaybackStreamAsync(ready, token);
    }

    public async Task PrepareAsync(Song song, CancellationToken token, bool next = false)
    {
        token.ThrowIfCancellationRequested();
        Request request;
        lock (_sync) request = ScheduleLocked(song, next);
        Exception? failure = await request.Completion.Task.WaitAsync(token);
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
    }

    private async Task ProcessAsync()
    {
        while (true)
        {
            Request request;
            lock (_sync)
            {
                if (_queue.First == null || _disposed)
                {
                    _worker = Task.CompletedTask;
                    return;
                }
                request = _queue.First.Value;
                _queue.RemoveFirst();
            }

            Exception? failure = null;
            try
            {
                await using var stream = await cache.OpenAsync(request.Song, _lifetime.Token);
                await cache.TrimAsync(_lifetime.Token);
            }
            catch (Exception ex)
            {
                failure = ex;
                if (ex is not OperationCanceledException)
                    await Logging.Logger.AddLog($"Track {request.Song.Id} preparation failed", exception: ex);
            }
            finally
            {
                lock (_sync)
                {
                    _pending.Remove(request.Song.Id);
                    request.Completion.TrySetResult(failure);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task worker;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var request in _queue) request.Completion.TrySetCanceled();
            _queue.Clear();
            _pending.Clear();
            worker = _worker;
        }
        await _lifetime.CancelAsync();
        await worker;
        _lifetime.Dispose();
    }
}
