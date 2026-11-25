using Kvasir.Media;
using Application.Interfaces;
using Application.Models;
using Logging;

namespace Application.Services;

public sealed class PlaylistImportService(IMusicLibrary library) : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<(ulong Guild, ulong User), Task> _imports = new();
    private readonly Dictionary<(ulong Guild, ulong User), ImportProgress> _importProgress = new();
    private bool _disposed;

    public ImportProgress? GetImport(ulong guildId, ulong userId) { lock (_sync) return _importProgress.GetValueOrDefault((guildId, userId)); }
    private bool HasActiveImport(ulong guildId, ulong userId) => _imports.TryGetValue((guildId, userId), out Task? job) && !job.IsCompleted;
    public bool IsImportRunning(ulong guildId, ulong userId) { lock (_sync) return HasActiveImport(guildId, userId); }
    public OperationResult StartImport(ulong guildId, MusicActor actor, int playlistId, string query)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (HasActiveImport(guildId, actor.UserId)) return OperationResult.Fail("Предыдущий импорт ещё выполняется.");
            _importProgress[(guildId, actor.UserId)] = new(0, 0, Array.Empty<string>(), false) { PlaylistId = playlistId };
            _imports[(guildId, actor.UserId)] = Task.Run(async () =>
            {
                try { await library.ImportAsync(guildId, actor, playlistId, query, p => { lock (_sync) _importProgress[(guildId, actor.UserId)] = p with { PlaylistId = playlistId }; }, _lifetime.Token); }
                catch (OperationCanceledException) { lock (_sync) _importProgress[(guildId, actor.UserId)] = new(0, 0, new[] { "Импорт отменён." }, true) { PlaylistId = playlistId }; }
                catch (Exception ex)
                {
                    await Logger.AddLog("Import failed: " + ex.Message, exception: ex);
                    lock (_sync) _importProgress[(guildId, actor.UserId)] = new(0, 0, new[] { "Источник недоступен. Повторите импорт позднее." }, true,
                        ex is MediaSourceException source ? source.Code : null, ex.GetType().Name + ": " + ex.Message) { PlaylistId = playlistId };
                }
            });
        }
        return OperationResult.Ok("Импорт запущен. Кнопка «Прогресс импорта» покажет результат.");
    }
    public async ValueTask DisposeAsync()
    {
        Task[] imports;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            imports = _imports.Values.ToArray();
        }
        await _lifetime.CancelAsync();
        await Task.WhenAll(imports);
        _lifetime.Dispose();
    }
}
