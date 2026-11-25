using Kvasir.Desktop.Localization;
using Kvasir.Desktop.Services;

namespace Kvasir.Desktop;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
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
        base.OnStartup(e);
    }
}
