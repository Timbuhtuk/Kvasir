using System.Buffers.Binary;
using System.Text.Json;

namespace Kvasir.Control;

// Only transport data lives here; the bot owns all music and storage operations.
public sealed record ControlRequest(string Session, string Action = "snapshot", ulong GuildId = 0,
    ulong? VoiceChannelId = null, Guid? EntryId = null, int? PlaylistId = null,
    int? SongId = null, string? Value = null, double Number = 0,
    CompanySettings? Settings = null, Guid? EffectId = null)
{
    public string? ImportQuery { get; init; }
    public bool CreateLocalPlaylist { get; init; }
    public int? AnchorSongId { get; init; }
    public bool InsertAfter { get; init; }
    public int? DestinationPlaylistId { get; init; }
}
public sealed record CompanySettings(ulong? DjRoleId, bool VoteSkip, bool FairQueue, int PerUserLimit, int Volume);
public sealed record GuildItem(ulong Id, string Name)
{
    public override string ToString() => Name;
}
public sealed record ChannelItem(ulong Id, string Name)
{
    public override string ToString() => Name;
}
public enum TrackDownloadState { Unknown, Pending, Downloading, Downloaded }
public sealed record TrackItem(int Id, string Name, string Author, double Duration, string? Link)
{
    public TrackDownloadState DownloadState { get; init; }
    public override string ToString() => Name;
}
public sealed record QueueItem(Guid Id, TrackItem Track, ulong RequestedBy)
{
    public override string ToString() => Track.Name;
}
public sealed record PlaylistItem(int Id, string Name, ulong AuthorId, bool Shared, int Count)
{
    public bool IsLocal { get; init; }
    public override string ToString() => Name;
}
public sealed record CheckItem(string Name, bool Healthy, string Detail);
public sealed record CacheItem(long Bytes, long LimitBytes, int Files, int InvalidFiles, int Removed, int Repaired, int FailedRepairs);
public sealed record ImportItem(int Completed, int Total, string[] Failures, bool Finished, string? ErrorCode = null, string? Detail = null)
{
    public int? PlaylistId { get; init; }
}
public sealed record SoundEffectItem(Guid Id, string Name, double Duration, bool Ready, string? Problem)
{
    public override string ToString() => Name;
}
public sealed record AudioMixItem(int MusicVolume, int EffectsVolume, bool DuckMusic, int ActiveEffects);
public sealed record ControlState
{
    public bool Ready { get; init; }
    public ulong GuildId { get; init; }
    public GuildItem[] Guilds { get; init; } = [];
    public ChannelItem[] Channels { get; init; } = [];
    public ulong? VoiceChannelId { get; init; }
    public TrackItem? Current { get; init; }
    public string Status { get; init; } = "None";
    public double Position { get; init; }
    public string Repeat { get; init; } = "off";
    public string? LastError { get; init; }
    public string? LastErrorDetail { get; init; }
    public QueueItem[] Queue { get; init; } = [];
    public TrackItem[] History { get; init; } = [];
    public PlaylistItem[] Playlists { get; init; } = [];
    public int? PlaylistId { get; init; }
    public TrackItem[] Songs { get; init; } = [];
    public CompanySettings? Settings { get; init; }
    public ImportItem? Import { get; init; }
    public ImportItem? LocalImport { get; init; }
    public CheckItem[] Checks { get; init; } = [];
    public CacheItem? Cache { get; init; }
    public SoundEffectItem[] Effects { get; init; } = [];
    public AudioMixItem? Mix { get; init; }
}
public sealed record MediaWorkItem(string Subject, string Stage, DateTimeOffset Started, DateTimeOffset Updated,
    string? Provider, long? Bytes, double? Percent, string? Detail, TimeSpan? Processed, int? Matches = null);

public sealed record ControlResponse(bool Success, string Message, ControlState? State = null, string? ErrorCode = null, string? Detail = null, AudioMixItem? Mix = null)
{
    public MediaWorkItem[]? MediaWork { get; init; }
    public int PendingTracks { get; init; }
    public string? MessageKey { get; init; }
    public string[]? MessageArguments { get; init; }
}

public static class ControlProtocol
{
    public const int MaximumFrameBytes = 8 * 1024 * 1024;

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length > MaximumFrameBytes) throw new InvalidDataException("Control message is too large.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumFrameBytes) throw new InvalidDataException("Invalid control message length.");
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidDataException("Empty control message.");
    }
}
