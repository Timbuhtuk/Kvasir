using Kvasir.Desktop.Localization;
using Kvasir.Desktop.Services;

namespace Kvasir.Desktop;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        if (e.Args is ["--check-package"])
        {
            string root = System.AppContext.BaseDirectory;
            bool hasTemplate = System.IO.File.Exists(System.IO.Path.Combine(root, "Defaults", "configuration.json"));
            bool hasBot = System.IO.File.Exists(System.IO.Path.Combine(root, "bot", "Kvasir.exe"));
            Shutdown(hasTemplate && hasBot && ReleaseNotesWindow.LoadCatalog() != null ? 0 : 1);
            return;
        }

        var preferences = DesktopPreferences.Load();
        UiText.SelectLanguage(preferences.Language);
        try
        {
            Kvasir.Configuration.ApplicationPaths.EnsureConfiguration();
            Tessera.WpfStyles.Themes.ThemeManager.Apply(preferences.Theme);
        }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.UnauthorizedAccessException)
        {
            System.Windows.MessageBox.Show(UiText.Get("OpenDataDirectoryFailed"), "Kvasir");
            Shutdown(1);
            return;
        }
        StartupUri = new System.Uri("MainWindow.xaml", System.UriKind.Relative);
        base.OnStartup(e);
    }
}
