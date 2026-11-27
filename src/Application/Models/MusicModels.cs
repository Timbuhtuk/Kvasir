using Entities.Models;

namespace Application.Models;

public sealed record MusicActor(ulong UserId, ulong? VoiceChannelId, bool IsManager = false, IReadOnlyCollection<ulong>? RoleIds = null)
{
    public bool CanAccessLocalPlaylists { get; init; }
}

public sealed record QueueEntry(Guid Id, Guid BatchId, BotVoiceChannel Channel, Song Song, ulong RequestedBy, DateTime AddedUtc);

public sealed class GuildMusicSettings
{
    public ulong? DjRoleId { get; set; }
    public bool VoteSkip { get; set; } = true;
    public bool FairQueue { get; set; }
    public int PerUserLimit { get; set; } = 10;
    public int Volume { get; set; } = 75;
    public int EffectsVolume { get; set; } = 75;
    public bool DuckMusic { get; set; }
}

public sealed record OperationResult(bool Success, string Message)
{
    public static OperationResult Ok(string message) => new(true, message);
    public static OperationResult Ok(string message, string messageKey, params string[] arguments) =>
        new(true, message) { MessageKey = messageKey, MessageArguments = arguments };
    public static OperationResult Fail(string message) => new(false, message);
    public string? MessageKey { get; init; }
    public string[]? MessageArguments { get; init; }
}

public sealed record CacheReport(long Bytes, long LimitBytes, int Files, int InvalidFiles, int Removed = 0, int Repaired = 0, int FailedRepairs = 0);
public sealed record DiagnosticCheck(string Name, bool Healthy, string Detail);
public sealed record ImportProgress(int Completed, int Total, IReadOnlyList<string> Failures, bool Finished, string? ErrorCode = null, string? Detail = null)
{
    public int? PlaylistId { get; init; }
}

public sealed class MusicPanelSession
{
    public string Screen { get; set; } = "library";
    public int Page { get; set; }
    public int? PlaylistId { get; set; }
    public Guid? QueueEntryId { get; set; }
    public int? SongId { get; set; }
    public string Search { get; set; } = "";
    public string LogFilter { get; set; } = "All";
    public DateTime TouchedUtc { get; set; } = DateTime.UtcNow;
}
