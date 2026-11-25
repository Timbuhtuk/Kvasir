using Kvasir.Media;
using Application.Interfaces;
using Application.Models;
using Application.Services;
using Entities.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure;

public sealed class MusicLibraryService(IServiceScopeFactory scopes, IMusicPreferences preferences, IVideoFinderService finder, TrackService tracks) : IMusicLibrary
{
    private readonly SemaphoreSlim _edit = new(1, 1);
    private static bool Visible(Playlist p, ulong guildId, MusicActor actor) => p.Guilds.Any(g => g.DiscordId == guildId) && (p.IsPublic || p.AuthorId == actor.UserId || actor.IsManager);
    private static bool Editable(Playlist p, ulong guildId, MusicActor actor) => Visible(p, guildId, actor) && (p.AuthorId == actor.UserId || actor.IsManager);
    public async Task<IReadOnlyList<Playlist>> ListAsync(ulong guildId, MusicActor actor, string search = "")
    {
        using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DiscordMusicDBContext>();
        List<Playlist> lists = await db.Playlists.AsNoTracking().Include(p => p.Songs).Where(p => p.Guilds.Any(g => g.DiscordId == guildId) && (p.IsPublic || p.AuthorId == actor.UserId || actor.IsManager)).ToListAsync();
        return lists.Where(p => p.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Name).ToList();
    }
    public async Task<Playlist?> GetAsync(ulong guildId, MusicActor actor, int playlistId)
    {
        using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DiscordMusicDBContext>();
        Playlist? playlist = await db.Playlists.AsNoTracking().Include(p => p.Songs).Include(p => p.Guilds).FirstOrDefaultAsync(p => p.Id == playlistId);
        if (playlist == null || !Visible(playlist, guildId, actor)) return null;
        IReadOnlyList<int> order = preferences.GetOrder(playlistId);
        var positions = new Dictionary<int, int>(order.Count);
        for (int q = 0; q < order.Count; q++) positions.TryAdd(order[q], q);
        playlist.Songs = playlist.Songs.OrderBy(s => positions.GetValueOrDefault(s.Id, int.MaxValue)).ThenBy(s => s.Id).ToList();
        return playlist;
    }
    public async Task<Playlist> CreateAsync(ulong guildId, MusicActor actor, string name, bool shared)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128) throw new ArgumentException("Название должно содержать от 1 до 128 символов.");
        using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DiscordMusicDBContext>();
        Guild guild = await db.Guilds.FirstAsync(g => g.DiscordId == guildId);
        Playlist playlist = new() { Name = name.Trim(), AuthorId = actor.UserId, CreationDate = DateTime.UtcNow, IsPublic = shared, Guilds = new List<Guild> { guild } };
        db.Playlists.Add(playlist); await db.SaveChangesAsync(); return playlist;
    }
    public async Task<OperationResult> EditAsync(ulong guildId, MusicActor actor, int playlistId, string action, string? value = null, int? songId = null)
    {
        await _edit.WaitAsync();
        try
        {
            using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DiscordMusicDBContext>();
            Playlist? p = await LoadPlaylistAsync(db, playlistId);
            if (p == null || !Editable(p, guildId, actor)) return OperationResult.Fail("Плейлист недоступен. Изменять его может автор или администратор.");
            List<int> order = GetOrder(p);
            switch (action)
            {
                case "rename":
                    if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 128) return OperationResult.Fail("Название: от 1 до 128 символов.");
                    p.Name = value.Trim(); break;
                case "share": p.IsPublic = !p.IsPublic; break;
                case "delete": db.Playlists.Remove(p); order.Clear(); break;
                case "add":
                    Song? song = await db.Songs.FindAsync(songId);
                    if (song == null) return OperationResult.Fail("Трек не найден.");
                    if (p.Songs.All(s => s.Id != song.Id)) { p.Songs.Add(song); order.Add(song.Id); }
                    break;
                case "remove":
                    Song? removed = p.Songs.FirstOrDefault(s => s.Id == songId);
                    if (removed == null) return OperationResult.Fail("Сначала выберите трек.");
                    p.Songs.Remove(removed); order.Remove(removed.Id); break;
                case "up": case "down":
                    int q = order.IndexOf(songId ?? -1); if (q < 0) return OperationResult.Fail("Сначала выберите трек.");
                    int e = Math.Clamp(q + (action == "up" ? -1 : 1), 0, order.Count - 1);
                    (order[q], order[e]) = (order[e], order[q]); break;
                default: return OperationResult.Fail("Неизвестное действие.");
            }
            await db.SaveChangesAsync(); await preferences.SaveOrderAsync(p.Id, order);
            return OperationResult.Ok("Плейлист обновлён.", "PlaylistUpdated");
        }
        finally { _edit.Release(); }
    }

    public async Task<OperationResult> MoveTrackAsync(ulong guildId, MusicActor actor, int playlistId, int songId, int anchorSongId, bool after)
    {
        await _edit.WaitAsync();
        try
        {
            using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DiscordMusicDBContext>();
            Playlist? playlist = await LoadPlaylistAsync(db, playlistId);
            if (playlist == null || !Editable(playlist, guildId, actor))
                return OperationResult.Fail("Плейлист недоступен для изменения.") with { MessageKey = "PlaylistDropUnavailable" };

            var order = GetOrder(playlist);
            int from = order.IndexOf(songId);
            int anchor = order.IndexOf(anchorSongId);
            if (from < 0 || anchor < 0)
                return OperationResult.Fail("Один из треков больше не в плейлисте.") with { MessageKey = "PlaylistDropTrackMissing" };

            if (from != anchor)
            {
                int destination = anchor + (after ? 1 : 0) - (from < anchor ? 1 : 0);
                if (from != destination)
                {
                    order.RemoveAt(from);
                    order.Insert(destination, songId);
                    await preferences.SaveOrderAsync(playlistId, order);
                }
            }
            return OperationResult.Ok("Порядок треков сохранён.", "PlaylistTrackOrderSaved");
        }
        finally { _edit.Release(); }
    }

    public async Task<OperationResult> CopyTrackAsync(ulong guildId, MusicActor actor, int sourcePlaylistId, int destinationPlaylistId, int songId)
    {
        await _edit.WaitAsync();
        try
        {
            using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DiscordMusicDBContext>();
            Playlist? destination = await LoadPlaylistAsync(db, destinationPlaylistId);
            if (destination == null || !Editable(destination, guildId, actor))
                return OperationResult.Fail("Плейлист недоступен для изменения.") with { MessageKey = "PlaylistDropUnavailable" };

            Playlist? source = await LoadPlaylistAsync(db, sourcePlaylistId);
            Song? song = source != null && Visible(source, guildId, actor) ? source.Songs.FirstOrDefault(s => s.Id == songId) : null;
            if (song == null)
                return OperationResult.Fail("Трек больше недоступен в исходном плейлисте.") with { MessageKey = "PlaylistDropSourceMissing" };
            if (destination.Songs.Any(s => s.Id == songId))
                return OperationResult.Ok("Трек уже есть в плейлисте.", "PlaylistTrackAlreadyPresent", destination.Name);

            var order = GetOrder(destination);
            order.Add(songId);
            // If the database write fails, readers ignore the order entry until the song is added.
            await preferences.SaveOrderAsync(destinationPlaylistId, order);
            destination.Songs.Add(song);
            await db.SaveChangesAsync();
            return OperationResult.Ok("Трек скопирован в плейлист «" + destination.Name + "».", "PlaylistTrackCopied", destination.Name);
        }
        finally { _edit.Release(); }
    }

    public async Task ImportAsync(ulong guildId, MusicActor actor, int playlistId, string query, Action<ImportProgress> progress, CancellationToken cancellationToken)
    {
        Playlist? p = await GetAsync(guildId, actor, playlistId);
        if (p == null || !Editable(p, guildId, actor)) throw new InvalidOperationException("Плейлист недоступен для изменения.");
        List<Song> found = new();
        await foreach (var batch in finder.FindImportBatchesAsync(query, cancellationToken))
            found.AddRange(await AddImportTracksAsync(guildId, actor, playlistId, batch, cancellationToken));

        List<string> failures = new(); int completed = 0;
        string? errorCode = null, detail = null;
        if (found.Count == 0) failures.Add("Источник не вернул треков.");
        progress(new(0, found.Count, failures.ToArray(), false));
        foreach (Song song in found)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await tracks.PrepareAsync(song, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failures.Add(song.Name + ": загрузка не удалась");
                if (detail == null) { detail = ex.GetType().Name + ": " + ex.Message; errorCode = ex is MediaSourceException source ? source.Code : null; }
                await Logging.Logger.AddLog("Import track failed: " + ex.Message, exception: ex);
            }
            completed++; progress(new(completed, found.Count, failures.ToArray(), false, errorCode, detail));
        }
        progress(new(completed, found.Count, failures.ToArray(), true, errorCode, detail));
    }

    private List<int> GetOrder(Playlist playlist)
    {
        var ids = playlist.Songs.Select(s => s.Id).ToHashSet();
        var order = preferences.GetOrder(playlist.Id).Where(ids.Contains).Distinct().ToList();
        order.AddRange(ids.Except(order).OrderBy(id => id));
        return order;
    }

    private static Task<Playlist?> LoadPlaylistAsync(DiscordMusicDBContext db, int id, CancellationToken cancellationToken = default) =>
        db.Playlists.Include(p => p.Songs).Include(p => p.Guilds).FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    private async Task<List<Song>> AddImportTracksAsync(ulong guildId, MusicActor actor, int playlistId,
        IReadOnlyList<FinderSongDTO> tracks, CancellationToken cancellationToken)
    {
        await _edit.WaitAsync(cancellationToken);
        try
        {
            using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DiscordMusicDBContext>();
            Playlist? playlist = await LoadPlaylistAsync(db, playlistId, cancellationToken);
            if (playlist == null || !Editable(playlist, guildId, actor)) throw new InvalidOperationException("Плейлист недоступен для изменения.");

            string[] links = tracks.Select(track => track.Link).OfType<string>().Distinct().ToArray();
            var knownSongs = await db.Songs.Where(s => s.Link != null && links.Contains(s.Link)).OrderBy(s => s.Id).ToListAsync(cancellationToken);
            var byLink = new Dictionary<string, Song>();
            foreach (var song in knownSongs) byLink.TryAdd(song.Link!, song);

            var order = GetOrder(playlist);
            var members = playlist.Songs.ToHashSet();
            var added = new List<Song>();
            var found = new List<Song>(tracks.Count);
            foreach (var track in tracks)
            {
                if (track.Link == null || !byLink.TryGetValue(track.Link, out Song? song))
                {
                    song = new Song { Name = track.Name, AuthorName = track.AuthorName, Link = track.Link, Duration = track.Duration };
                    db.Songs.Add(song);
                    if (track.Link != null) byLink.Add(track.Link, song);
                }
                if (members.Add(song))
                {
                    playlist.Songs.Add(song);
                    added.Add(song);
                }
                found.Add(song);
            }
            if (added.Count > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
                order.AddRange(added.Select(s => s.Id));
                await preferences.SaveOrderAsync(playlistId, order);
            }
            return found;
        }
        finally { _edit.Release(); }
    }
}
