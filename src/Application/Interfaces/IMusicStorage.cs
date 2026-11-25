using Application.Models;
using Entities.Models;

namespace Application.Interfaces;

public interface IMusicPreferences
{
    GuildMusicSettings GetSettings(ulong guildId);
    Task SaveSettingsAsync(ulong guildId, GuildMusicSettings settings);
    IReadOnlyList<int> GetOrder(int playlistId);
    Task SaveOrderAsync(int playlistId, IReadOnlyList<int> songIds);
}

public interface IMusicLibrary
{
    Task<IReadOnlyList<Playlist>> ListAsync(ulong guildId, MusicActor actor, string search = "");
    Task<Playlist?> GetAsync(ulong guildId, MusicActor actor, int playlistId);
    Task<Playlist> CreateAsync(ulong guildId, MusicActor actor, string name, bool shared);
    Task<OperationResult> EditAsync(ulong guildId, MusicActor actor, int playlistId, string action, string? value = null, int? songId = null);
    Task<OperationResult> MoveTrackAsync(ulong guildId, MusicActor actor, int playlistId, int songId, int anchorSongId, bool after);
    Task<OperationResult> CopyTrackAsync(ulong guildId, MusicActor actor, int sourcePlaylistId, int destinationPlaylistId, int songId);
    Task ImportAsync(ulong guildId, MusicActor actor, int playlistId, string query, Action<ImportProgress> progress, CancellationToken cancellationToken);
}

public interface IMediaCache
{
    int? PreparingSongId => null;
    Task<Stream?> TryOpenReadyAsync(Song song, CancellationToken cancellationToken);
    Task<Stream> OpenAsync(Song song, CancellationToken cancellationToken = default);
    Task<CacheReport> InspectAsync();
    Task<CacheReport> MaintainAsync(bool repair, CancellationToken cancellationToken = default);
    Task TrimAsync(CancellationToken cancellationToken = default);
}

public interface IMusicDiagnostics
{
    Task<IReadOnlyList<DiagnosticCheck>> CheckAsync();
}
