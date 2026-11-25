using Kvasir.Control;

namespace Kvasir.Desktop.ViewModels;

// UI drafts and the last received snapshot. Playback remains owned by the bot.
public sealed class ServerWorkspace(GuildItem guild)
{
    public GuildItem Guild { get; } = guild;
    public string Name => Guild.Name;
    public override string ToString() => Name;
    public ControlState? Snapshot { get; set; }
    public ulong? ChannelId { get; set; }
    public int? PlaylistId { get; set; }
    public Guid? EntryId { get; set; }
    public int? SongId { get; set; }
    public int? HistoryId { get; set; }
    public string Query { get; set; } = "";
    public string Search { get; set; } = "";
    public string SeekText { get; set; } = "0:00";
    public int ViewIndex { get; set; }
    public bool SettingsDirty { get; set; }
    public CompanySettings Draft { get; set; } = new(null, true, false, 10, 75);
    public string DjRole { get; set; } = "";
    public string Limit { get; set; } = "10";
    public string? ReportedError { get; set; }
    public string? ReportedImport { get; set; }
}
