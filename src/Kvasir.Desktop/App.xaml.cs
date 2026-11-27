using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Kvasir.Configuration;
using Kvasir.Desktop.Localization;
using Kvasir.Desktop.Services;

namespace Kvasir.Desktop;

public partial class App : System.Windows.Application
{
    private IDisposable? _dataSession;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        if (e.Args is ["--check-package"])
        {
            string root = System.AppContext.BaseDirectory;
            bool ready = DesktopMaintenance.CanReplaceExecutable &&
                File.Exists(Path.Combine(root, "Defaults", "configuration.json")) &&
                File.Exists(Path.Combine(root, "bot", "Kvasir.exe")) &&
                ReleaseNotesWindow.LoadCatalog() != null;
            Shutdown(ready ? 0 : 1);
            return;
        }

        try
        {
            if (e.Args is ["--move-data", var owner, var source, var destination] && int.TryParse(owner, out int pid))
            {
                var appearance = DesktopPreferences.Load();
                UiText.SelectLanguage(appearance.Language);
                Tessera.WpfStyles.Themes.ThemeManager.Apply(appearance.Theme);
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                MoveData(pid, source, destination);
                ShutdownMode = ShutdownMode.OnLastWindowClose;
            }
            _dataSession = ApplicationPaths.OpenDataSession();
            var preferences = DesktopPreferences.Load();
            UiText.SelectLanguage(preferences.Language);
            ApplicationPaths.EnsureConfiguration();
            Tessera.WpfStyles.Themes.ThemeManager.Apply(preferences.Theme);
        }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.UnauthorizedAccessException)
        {
            System.Windows.MessageBox.Show(UiText.Get("OpenDataDirectoryFailed"), "Kvasir");
            Shutdown(1);
            return;
        }
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        ConfirmUpdateStartup(window);
        window.Show();
    }

    private static void ConfirmUpdateStartup(Window window)
    {
        const string prefix = "Local\\Kvasir.Update.";
        string? signal = Environment.GetEnvironmentVariable(DesktopMaintenance.UpdateReadyEventVariable);
        if (signal == null) return;
        Environment.SetEnvironmentVariable(DesktopMaintenance.UpdateReadyEventVariable, null);
        if (!signal.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(signal.AsSpan(prefix.Length), "N", out _)) return;

        window.ContentRendered += Confirm;
        void Confirm(object? sender, EventArgs e)
        {
            window.ContentRendered -= Confirm;
            try
            {
                if (EventWaitHandle.TryOpenExisting(signal, out var ready))
                    using (ready) ready.Set();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Trace.WriteLine(ex); }
        }
    }

    private static void MoveData(int owner, string source, string destination)
    {
        var bar = new ProgressBar { Height = 8, Margin = new Thickness(16), IsIndeterminate = true };
        var window = new Window { Title = UiText.Get("MoveData"), Width = 400, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize, Content = bar };
        bool finished = false;
        window.Closing += (_, e) => e.Cancel = !finished;
        window.ContentRendered += async (_, _) =>
        {
            var progress = new Progress<int>(value => { bar.IsIndeterminate = false; bar.Value = value; });
            try
            {
                try
                {
                    using var parent = Process.GetProcessById(owner);
                    await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
                }
                catch (ArgumentException) { } // The previous desktop already exited.
                bool cleared = await Task.Run(() => DataDirectoryMigration.Move(ApplicationPaths.Location, source, destination, progress));
                if (!cleared) MessageBox.Show(window, UiText.Format("MoveDataCleanup", source), window.Title);
            }
            catch (Exception ex) { MessageBox.Show(window, UiText.Format("MoveDataFailed", ex.Message), window.Title); }
            finally { finished = true; window.Close(); }
        };
        window.ShowDialog();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _dataSession?.Dispose();
        base.OnExit(e);
    }
}
