using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Kvasir.Desktop.Localization;
using Kvasir.Desktop.Services;

namespace Kvasir.Desktop;

public partial class MainWindow
{
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private readonly CancellationTokenSource _updateLifetime = new();
    private GitHubUpdater.Release? _availableRelease;
    private Version? _notifiedVersion;
    private bool _checkingForUpdates;

    private void InitializeUpdateChecks()
    {
        Loaded += (_, _) => SetAutomaticUpdateChecks(DesktopPreferences.Load().AutoCheckUpdates);
        Activated += (_, _) => NotifyAboutUpdate();
        _updateTimer.Tick += async (_, _) => await CheckAutomaticallyAsync();
        Closed += (_, _) =>
        {
            _updateTimer.Stop();
            _updateLifetime.Cancel();
            _updateLifetime.Dispose();
        };
    }

    private void SetAutomaticUpdateChecks(bool enabled)
    {
        if (!GitHubUpdater.IsReleaseBuild) return;
        if (!enabled)
        {
            _updateTimer.Stop();
            return;
        }
        if (_updateTimer.IsEnabled) return;
        _updateTimer.Start();
        _ = CheckAutomaticallyAsync();
    }

    private async Task CheckAutomaticallyAsync()
    {
        if (_checkingForUpdates || !_updateTimer.IsEnabled) return;
        _checkingForUpdates = true;
        var token = _updateLifetime.Token;
        try
        {
            _availableRelease = await GitHubUpdater.CheckAsync(token);
            NotifyAboutUpdate();
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) Trace.WriteLine(ex);
        }
        finally { _checkingForUpdates = false; }
    }

    private void NotifyAboutUpdate()
    {
        if (!_updateTimer.IsEnabled || _closing || !IsActive || WindowState == WindowState.Minimized ||
            _availableRelease is not { } release || release.Version == _notifiedVersion) return;
        _notifiedVersion = release.Version;
        Notifications.Show(UiText.Format("UpdateAvailableNoticeFormat", release.Version), heading: UiText.Get("KvasirUpdate"));
    }
}
