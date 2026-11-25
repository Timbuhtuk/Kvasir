using System.IO;
using System.Windows.Threading;
using Kvasir.Control;
using Kvasir.Desktop.Localization;
using Kvasir.Desktop.Services;

namespace Kvasir.Desktop.ViewModels;

public sealed partial class ControlViewModel
{
    private readonly BotControlClient _mediaClient = new();
    private readonly DispatcherTimer _mediaTimer;
    private bool _readingMedia;

    public string MediaText { get; private set; } = "";
    public string MediaDetails { get; private set; } = "";
    public bool HasMediaStatus => MediaText.Length > 0;
    public bool MediaWorking { get; private set; }
    public bool MediaIndeterminate { get; private set; }
    public double MediaPercent { get; private set; }

    private async Task ReadMediaStatusAsync()
    {
        if (_readingMedia || !Running || _session.IsCancellationRequested || _lifecycleBusy) return;
        _readingMedia = true;
        var token = _session.Token;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var response = await _mediaClient.SendAsync(_process.PipeName + "-media",
                new(_process.Session, "media-status"), timeout.Token);
            if (!token.IsCancellationRequested && !_disposed)
                ShowMediaStatus(response.MediaWork ?? [], response.PendingTracks);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or ObjectDisposedException or UnauthorizedAccessException)
        {
            if ((_connected || HasMediaStatus) && !token.IsCancellationRequested && !_disposed)
                ShowMediaStatus([], 0, unavailable: true);
        }
        finally { _readingMedia = false; }
    }

    internal void ShowMediaStatus(MediaWorkItem[] items, int pending, bool unavailable = false)
    {
        static bool Active(MediaWorkItem item) => item.Stage is "Search" or "Connecting" or "Downloading" or "Converting";

        var now = DateTimeOffset.UtcNow;
        var visible = items.Where(s => Active(s) || s.Stage == "Failed" || now - s.Updated < TimeSpan.FromSeconds(5))
            .OrderByDescending(Active).ThenByDescending(s => s.Stage == "Failed").ThenByDescending(s => s.Updated).ToArray();
        var first = visible.FirstOrDefault();
        var lines = visible.Select(s => Describe(s, now)).ToList();
        string queued = pending > 0 ? UiText.Format("MediaPending", pending) : "";
        MediaText = unavailable ? UiText.Get("MediaChannelUnavailable") : lines.FirstOrDefault() ?? "";
        int active = visible.Count(Active);
        if (active > 1) MediaText += " · " + UiText.Format("MediaOther", active - 1);
        if (pending > 0)
        {
            MediaText = MediaText.Length == 0 ? queued : MediaText + " · " + queued;
            lines.Add(queued);
        }
        MediaDetails = string.Join(Environment.NewLine, lines);
        MediaWorking = first != null && Active(first);
        MediaIndeterminate = MediaWorking && first!.Percent == null;
        MediaPercent = first?.Percent ?? 0;
        Raise(nameof(MediaText)); Raise(nameof(MediaDetails)); Raise(nameof(HasMediaStatus));
        Raise(nameof(MediaWorking)); Raise(nameof(MediaIndeterminate)); Raise(nameof(MediaPercent));
    }

    private static string Describe(MediaWorkItem item, DateTimeOffset now)
    {
        string text = UiText.Get("Media" + item.Stage);
        if (item.Provider != null) text += " · " + item.Provider;
        if (item.Stage == "Failed") return text + " · " + item.Detail + " · " + item.Subject;
        if (item.Stage is "Ready" or "Cancelled") return text + " · " + item.Subject;
        if (item.Percent is double percent) text += $" · {percent:0.#}%";
        if (item.Matches is int matches) text += " · " + UiText.Format("MediaMatches", matches);
        if (item.Bytes is long bytes) text += " · " + UiText.Format("MediaBytes", bytes / 1048576d);
        if (item.Processed is TimeSpan processed) text += " · " + UiText.Format("MediaProcessed", processed.ToString(@"hh\:mm\:ss"));
        var silent = now - item.Updated;
        if (silent.TotalSeconds >= 15) text += " · " + UiText.Format("MediaSilent", (int)silent.TotalSeconds);
        return text + " · " + item.Subject;
    }
}
