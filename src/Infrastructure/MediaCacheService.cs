using Kvasir.Media;
using Application.Interfaces;
using Application.Models;
using Entities.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Kvasir.Configuration;

namespace Infrastructure;

public sealed class MediaCacheService : IMediaCache
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IAudioDownloaderService _downloader;
    private readonly IAudioNormalizer _normalizer;
    private readonly IConfiguration _config;
    private readonly string _root;
    private readonly long _limit;
    private readonly int _maxAgeDays;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, int> _leases = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _prepare = new(1, 1);
    private int _preparingSongId;
    public int? PreparingSongId => Volatile.Read(ref _preparingSongId) is int id and > 0 ? id : null;
    public MediaCacheService(IServiceScopeFactory scopes, IAudioDownloaderService downloader, IAudioNormalizer normalizer, IConfiguration config)
    {
        _scopes = scopes; _downloader = downloader; _normalizer = normalizer;
        _config = config;
        _root = Path.GetFullPath(config["music_client:music_folder"] ?? "music");
        _limit = long.TryParse(config["music_client:cache_limit_mb"], out long mb) ? Math.Clamp(mb, 64, 1048576) * 1024 * 1024 : 2L * 1024 * 1024 * 1024;
        _maxAgeDays = int.TryParse(config["music_client:cache_max_age_days"], out int days) ? Math.Clamp(days, 1, 3650) : 30;
        Directory.CreateDirectory(_root);
        if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Папка кэша не должна быть ссылкой.");
    }
    public bool IsManaged(string path)
    {
        string full = Path.GetFullPath(path);
        return string.Equals(Path.GetDirectoryName(full), _root, StringComparison.OrdinalIgnoreCase)
            && (!File.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0);
    }
    private static bool Valid(string? path, double? seconds = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        long length = new FileInfo(path).Length;
        if (length < 4 || length % 4 != 0) return false;
        return seconds is not > 0 || Math.Abs(length / 192000d - seconds.Value) <= Math.Max(5, seconds.Value * .1);
    }
    private bool DecodeOnPlayback =>
        bool.TryParse(_config[$"music_client:{BotConfiguration.DecodeOnPlaybackKey}"], out bool enabled) && enabled;

    private static bool IsCompressed(string path) => path.EndsWith(".audio", StringComparison.OrdinalIgnoreCase);

    private static bool HasContent(string path) => new FileInfo(path) is { Exists: true, Length: > 0 };

    private static bool ValidCachedFile(string path, double? seconds = null) =>
        IsCompressed(path) ? HasContent(path) : Valid(path, seconds);

    public Task<Stream?> TryOpenReadyAsync(Song song, CancellationToken cancellationToken) =>
        TryOpenReadyAsync(song, DecodeOnPlayback, cancellationToken);

    private async Task<Stream?> TryOpenReadyAsync(Song song, bool decodeOnPlayback, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DiscordMusicDBContext>();
            Song entity = await db.Songs.FirstAsync(s => s.Id == song.Id, cancellationToken);
            string destination = Destination(entity.Id, decodeOnPlayback);
            if (!ValidCachedFile(destination, entity.Duration))
            {
                if (!decodeOnPlayback) return null;
                destination = Destination(entity.Id, false);
                if (!Valid(destination, entity.Duration))
                {
                    if (entity.FilePath is not string legacy || !IsManaged(legacy) ||
                        !legacy.EndsWith(".pcm", StringComparison.OrdinalIgnoreCase) || !Valid(legacy, entity.Duration))
                        return null;
                    destination = Path.GetFullPath(legacy);
                }
            }

            if (!entity.IsDownloaded || entity.FilePath != destination)
            {
                entity.FilePath = destination;
                entity.IsDownloaded = true;
                await db.SaveChangesAsync(cancellationToken);
            }
            lock (_leases)
                if (!_leases.ContainsKey(destination)) File.SetLastWriteTimeUtc(destination, DateTime.UtcNow);
            return Lease(destination);
        }
        finally { _gate.Release(); }
    }

    private string Destination(int id, bool compressed)
    {
        string path = Path.Combine(_root, id + (compressed ? ".audio" : ".norm.pcm"));
        if (!IsManaged(path)) throw new IOException("Небезопасный путь кэша.");
        return path;
    }

    private FileStream Lease(string path)
    {
        void Release()
        {
            lock (_leases) if (--_leases[path] == 0) _leases.Remove(path);
        }
        lock (_leases) _leases[path] = _leases.GetValueOrDefault(path) + 1;
        try { return new LeasedStream(path, Release); }
        catch { Release(); throw; }
    }

    public async Task<Stream> OpenAsync(Song song, CancellationToken cancellationToken = default)
    {
        bool decodeOnPlayback = DecodeOnPlayback;
        Stream? ready = await TryOpenReadyAsync(song, decodeOnPlayback, cancellationToken);
        if (ready != null) return ready;

        await _prepare.WaitAsync(cancellationToken);
        try
        {
            ready = await TryOpenReadyAsync(song, decodeOnPlayback, cancellationToken);
            if (ready != null) return ready;

            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DiscordMusicDBContext>();
            Song entity = await db.Songs.FirstAsync(s => s.Id == song.Id, cancellationToken);
            string destination = Destination(entity.Id, decodeOnPlayback);
            string temporary = destination + ".part";
            if (!IsManaged(temporary)) throw new IOException("Небезопасный временный путь кэша.");
            Volatile.Write(ref _preparingSongId, entity.Id);
            try
            {
                await PrepareFileAsync(entity, temporary, decodeOnPlayback, cancellationToken);
                if (decodeOnPlayback ? !HasContent(temporary) : !Valid(temporary, entity.Duration))
                    throw new IOException("Загружен неполный аудиофайл.");
                await _gate.WaitAsync(cancellationToken);
                try
                {
                    lock (_leases) if (_leases.ContainsKey(destination)) throw new IOException("Файл кэша занят.");
                    File.Move(temporary, destination, true);
                    entity.FilePath = destination;
                    entity.IsDownloaded = true;
                    db.Entry(entity).Property(s => s.IsDownloaded).IsModified = true;
                    await db.SaveChangesAsync(cancellationToken);
                    return Lease(destination);
                }
                finally { _gate.Release(); }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { Volatile.Write(ref _preparingSongId, 0); _prepare.Release(); }
    }
    private async Task PrepareFileAsync(Song song, string temporary, bool decodeOnPlayback, CancellationToken token)
    {
        FileStream? original = null;
        if (!decodeOnPlayback)
        {
            await _gate.WaitAsync(token);
            try
            {
                string compressed = Destination(song.Id, true);
                if (HasContent(compressed))
                    original = Lease(compressed);
                else if (song.FilePath is string path && IsManaged(path) && !IsCompressed(path) && Valid(path, song.Duration) &&
                    !string.Equals(Path.GetFullPath(path), Destination(song.Id, false), StringComparison.OrdinalIgnoreCase))
                    original = Lease(Path.GetFullPath(path));
            }
            finally { _gate.Release(); }
        }

        if (original != null)
        {
            await using (original)
            {
                if (IsCompressed(original.Name))
                    await _normalizer.NormalizeCompressedAsync(original.Name, temporary, token);
                else
                    await _normalizer.NormalizeAsync(original.Name, temporary, token);
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(song.Link)) throw new IOException("Источник аудио недоступен.");
        bool downloaded = decodeOnPlayback
            ? await _downloader.DownloadCompressedAsync(song.Link, temporary, song.Name, token)
            : await _downloader.DownloadAsync(song.Link, temporary, song.Name, token);
        if (!downloaded) throw new IOException("Источник аудио недоступен.");
    }

    public async Task<Stream> OpenPlaybackStreamAsync(Stream source, CancellationToken token)
    {
        if (source is not FileStream file || !IsCompressed(file.Name)) return source;
        return await BufferedPcmStream.OpenAsync(file, token);
    }

    private List<FileInfo> Files() => new DirectoryInfo(_root).EnumerateFiles()
        .Where(f => (IsCompressed(f.Name) || f.Extension.Equals(".pcm", StringComparison.OrdinalIgnoreCase)) && IsManaged(f.FullName))
        .ToList();
    public async Task<CacheReport> InspectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var files = Files();
            using var scope = _scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DiscordMusicDBContext>();
            var songs = await db.Songs.AsNoTracking().Where(s => s.IsDownloaded).ToListAsync();
            int invalid = files.Count(f => !ValidCachedFile(f.FullName));
            invalid += songs.Count(s => s.FilePath == null || !File.Exists(s.FilePath) ||
                ValidCachedFile(s.FilePath) && !ValidCachedFile(s.FilePath, s.Duration));
            return new(files.Sum(f => f.Length), _limit, files.Count, invalid);
        }
        finally { _gate.Release(); }
    }
    public async Task TrimAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var files = Files().OrderBy(f => f.LastWriteTimeUtc).ToList(); long bytes = files.Sum(f => f.Length);
            using var scope = _scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DiscordMusicDBContext>();
            List<Song> songs = await db.Songs.ToListAsync(cancellationToken);
            foreach (FileInfo file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_leases) if (_leases.GetValueOrDefault(file.FullName) > 0) continue;
                if (bytes <= _limit && file.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-_maxAgeDays) && ValidCachedFile(file.FullName)) continue;
                long length = file.Length;
                try { file.Delete(); } catch (IOException) { continue; }
                bytes -= length;
                foreach (Song song in songs.Where(s => s.FilePath != null && string.Equals(Path.GetFullPath(s.FilePath), file.FullName, StringComparison.OrdinalIgnoreCase))) song.IsDownloaded = false;
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }
    public async Task<CacheReport> MaintainAsync(bool repair, CancellationToken cancellationToken = default)
    {
        int repaired = 0, failed = 0;
        if (repair)
        {
            using var scope = _scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DiscordMusicDBContext>();
            List<Song> songs = await db.Songs.AsNoTracking().Where(s => s.IsDownloaded).ToListAsync(cancellationToken);
            foreach (Song song in songs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (song.FilePath != null && IsManaged(song.FilePath) && ValidCachedFile(song.FilePath, song.Duration)) continue;
                try { await using (await OpenAsync(song, cancellationToken)) { } repaired++; }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { failed++; }
            }
        }
        CacheReport before = await InspectAsync(); await TrimAsync(cancellationToken); CacheReport after = await InspectAsync();
        return after with { Removed = Math.Max(0, before.Files - after.Files), Repaired = repaired, FailedRepairs = failed };
    }
    private sealed class LeasedStream(string path, Action release) : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true)
    {
        private int _released;
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (Interlocked.Exchange(ref _released, 1) == 0) release(); }
        public override async ValueTask DisposeAsync() { await base.DisposeAsync(); if (Interlocked.Exchange(ref _released, 1) == 0) release(); }
    }
}
