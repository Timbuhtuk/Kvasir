using System.Diagnostics;
using System.IO;
using System.Windows;
using Kvasir.Configuration;
using Kvasir.Desktop.Localization;
using Kvasir.Desktop.Services;
using Tessera.WpfStyles.Controls;

namespace Kvasir.Desktop;

public partial class SettingsWindow
{
    private readonly CancellationTokenSource _maintenanceLifetime = new();
    private GitHubUpdater.Release? _release;
    private bool _handingOffMaintenance;
    internal event Action<bool>? AutoCheckUpdatesChanged;

    private void InitializeMaintenance()
    {
        InstalledVersion.Text = GitHubUpdater.IsReleaseBuild
            ? UiText.Format("InstalledVersionFormat", GitHubUpdater.CurrentVersion)
            : UiText.Get("DevelopmentBuild");
        AutoCheckUpdatesBox.IsChecked = _preferences.AutoCheckUpdates;
        AutoCheckUpdatesBox.IsEnabled = GitHubUpdater.IsReleaseBuild;
        StandaloneHint.Visibility = DesktopMaintenance.CanReplaceExecutable ? Visibility.Collapsed : Visibility.Visible;
        UninstallButton.IsEnabled = DesktopMaintenance.CanReplaceExecutable;
        Closing += (_, e) => e.Cancel |= _handingOffMaintenance;
        Closed += (_, _) =>
        {
            _maintenanceLifetime.Cancel();
            _maintenanceLifetime.Dispose();
        };
    }

    private void AutoCheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = AutoCheckUpdatesBox.IsChecked == true;
        if (TrySave(_preferences with { AutoCheckUpdates = enabled })) AutoCheckUpdatesChanged?.Invoke(enabled);
        else AutoCheckUpdatesBox.IsChecked = _preferences.AutoCheckUpdates;
    }

    internal void ShowAvailableRelease(GitHubUpdater.Release? release)
    {
        _release = release;
        UpdateButton.Content = release is null ? UiText.Get("CheckForUpdates")
            : DesktopMaintenance.CanReplaceExecutable && release.Installer is not null
                ? UiText.Format("UpdateToVersionFormat", release.Version)
                : UiText.Get("OpenGitHubRelease");
        MaintenanceStatus.Text = release is null ? "" : UiText.Format("UpdateAvailableFormat", release.Version);
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        var token = _maintenanceLifetime.Token;
        string? downloaded = null;
        bool checking = _release is null;
        try
        {
            if (checking)
            {
                SetMaintenanceProgress("CheckingGitHubUpdates");
                ShowAvailableRelease(await GitHubUpdater.CheckAsync(token));
                if (_release is null) MaintenanceStatus.Text = UiText.Get("NoNewerRelease");
                return;
            }

            var release = _release!;
            if (!DesktopMaintenance.CanReplaceExecutable || release.Installer is not { } package)
            {
                Process.Start(new ProcessStartInfo(release.Page.AbsoluteUri) { UseShellExecute = true });
                return;
            }

            if (!ConfirmMaintenance(UiText.Format("ConfirmKvasirUpdateFormat", release.Version), "KvasirUpdate", "InstallUpdate"))
                return;
            SetMaintenanceProgress("DownloadingKvasirUpdate", measurable: true);
            var progress = new Progress<int>(value => MaintenanceProgress.Value = value);
            downloaded = await GitHubUpdater.DownloadAsync(package, progress, token);
            token.ThrowIfCancellationRequested();
            SetMaintenanceProgress("ClosingForMaintenance");
            if (await CloseForMaintenanceAsync(() => DesktopMaintenance.StartUpdate(downloaded, package)))
                downloaded = null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                MaintenanceStatus.Text = UiText.Format(checking ? "CheckUpdatesFailedFormat" : "UpdateFailedFormat", ex.Message);
        }
        finally
        {
            GitHubUpdater.DeleteDownload(downloaded);
            if (!token.IsCancellationRequested) ResetMaintenanceProgress();
        }
    }

    private async void MoveData_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = UiText.Get("ChooseDataParent") };
        if (picker.ShowDialog(this) != true) return;
        string destination = Path.Combine(picker.FolderName, "Kvasir");
        var token = _maintenanceLifetime.Token;
        try
        {
            string source = ApplicationPaths.DataDirectory;
            DataDirectoryMigration.Validate(source, destination);
            if (!ConfirmMaintenance(UiText.Format("ConfirmMoveData", destination), "MoveData", "MoveData")) return;
            SetMaintenanceProgress("ClosingForMaintenance");
            await CloseForMaintenanceAsync(() => DataDirectoryMigration.Restart(source, destination));
        }
        catch (Exception ex) { SettingsMessage.Text = UiText.Format("MoveDataFailed", ex.Message); }
        finally { if (!token.IsCancellationRequested) ResetMaintenanceProgress(); }
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var token = _maintenanceLifetime.Token;
        if (!ConfirmMaintenance(UiText.Format("ConfirmKvasirUninstallFormat", Environment.ProcessPath, ApplicationPaths.DataDirectory),
                "UninstallKvasirTitle", "Delete")) return;
        try
        {
            SetMaintenanceProgress("ClosingForMaintenance");
            await CloseForMaintenanceAsync(DesktopMaintenance.StartUninstall);
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) MaintenanceStatus.Text = UiText.Format("UninstallFailedFormat", ex.Message);
        }
        finally
        {
            if (!token.IsCancellationRequested) ResetMaintenanceProgress();
        }
    }

    private async Task<bool> CloseForMaintenanceAsync(Action startHelper)
    {
        _handingOffMaintenance = true;
        try
        {
            if (Owner is MainWindow main && await main.CloseAfterStoppingAsync(startHelper)) return true;
            MaintenanceStatus.Text = UiText.Get("MaintenanceCloseFailed");
            return false;
        }
        finally { _handingOffMaintenance = false; }
    }

    private bool ConfirmMaintenance(string message, string title, string action)
    {
        var labels = new TesseraDialogLabels(Yes: UiText.Get(action), No: UiText.Get("Cancel"), Close: UiText.Get("Close"));
        return TesseraDialog.Show(this, message, UiText.Get(title), MessageBoxButton.YesNo, MessageBoxImage.Warning,
            labels, confirmByDefault: false) == MessageBoxResult.Yes;
    }

    private void SetMaintenanceProgress(string status, bool measurable = false)
    {
        MoveDataButton.IsEnabled = UpdateButton.IsEnabled = UninstallButton.IsEnabled = false;
        MaintenanceStatus.Text = UiText.Get(status);
        MaintenanceProgress.Value = 0;
        MaintenanceProgress.IsIndeterminate = !measurable;
        MaintenanceProgress.Visibility = Visibility.Visible;
    }

    private void ResetMaintenanceProgress()
    {
        MaintenanceProgress.Visibility = Visibility.Collapsed;
        MaintenanceProgress.IsIndeterminate = false;
        MoveDataButton.IsEnabled = UpdateButton.IsEnabled = true;
        UninstallButton.IsEnabled = DesktopMaintenance.CanReplaceExecutable;
    }
}
