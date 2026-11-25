namespace Kvasir.Media;

public enum MediaStage { Search, Connecting, Downloading, Converting, Ready, Failed, Cancelled }

public sealed record MediaStatus(string Subject, MediaStage Stage, DateTimeOffset Started, DateTimeOffset Updated,
    string? Provider = null, long? Bytes = null, double? Percent = null, string? Detail = null, TimeSpan? Processed = null, int? Matches = null);

public sealed class MediaActivity
{
    private readonly object _sync = new();
    private readonly Dictionary<MediaOperation, MediaStatus> _active = new();
    private readonly Queue<MediaStatus> _recent = new();

    public MediaStatus[] Snapshot()
    {
        lock (_sync)
            return _active.Values.Concat(_recent.Reverse().Where(s => DateTimeOffset.UtcNow - s.Updated < TimeSpan.FromMinutes(1))).ToArray();
    }

    public async Task<T> RunAsync<T>(string subject, MediaStage stage, Func<MediaOperation, Task<T>> work)
    {
        var operation = new MediaOperation(this);
        var now = DateTimeOffset.UtcNow;
        lock (_sync) _active.Add(operation, new(subject, stage, now, now));
        try
        {
            T result = await work(operation).ConfigureAwait(false);
            Finish(operation, MediaStage.Ready);
            return result;
        }
        catch (OperationCanceledException)
        {
            Finish(operation, MediaStage.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            Finish(operation, MediaStage.Failed, ex.Message);
            throw;
        }
    }

    internal void Report(MediaOperation operation, MediaStage stage, string? provider, long? bytes, double? percent, TimeSpan? processed, int? matches)
    {
        lock (_sync)
        {
            if (!_active.TryGetValue(operation, out var previous)) return;
            var next = previous with { Stage = stage, Provider = provider, Bytes = bytes, Percent = percent, Processed = processed, Matches = matches };
            if (next != previous) _active[operation] = next with { Updated = DateTimeOffset.UtcNow };
        }
    }

    private void Finish(MediaOperation operation, MediaStage stage, string? detail = null)
    {
        lock (_sync)
        {
            if (!_active.Remove(operation, out var previous)) return;
            _recent.Enqueue(previous with { Stage = stage, Detail = detail, Updated = DateTimeOffset.UtcNow });
            while (_recent.Count > 12) _recent.Dequeue();
        }
    }
}

public sealed class MediaOperation
{
    private readonly MediaActivity _activity;
    internal MediaOperation(MediaActivity activity) => _activity = activity;
    public void Report(MediaStage stage, string? provider = null, long? bytes = null, double? percent = null, TimeSpan? processed = null, int? matches = null) =>
        _activity.Report(this, stage, provider, bytes, percent.HasValue ? Math.Clamp(percent.Value, 0, 100) : null, processed, matches);
}
