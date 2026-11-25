using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Tessera.WpfStyles.Themes;

namespace Kvasir.Desktop.Controls;

public class ThemedDialogWindow : Window
{
    private const uint UseImmersiveDarkMode = 20;
    private const uint CaptionColor = 35;
    private const uint CaptionTextColor = 36;
    private const uint DefaultColor = 0xFFFFFFFF;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ThemeChanged += ThemeChanged;
        SystemParameters.StaticPropertyChanged += SystemParameterChanged;
        ApplyCaptionTheme();
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeManager.ThemeChanged -= ThemeChanged;
        SystemParameters.StaticPropertyChanged -= SystemParameterChanged;
        base.OnClosed(e);
    }

    private void ThemeChanged(object? sender, EventArgs e) => ApplyCaptionTheme();

    private void SystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            Dispatcher.InvokeAsync(ApplyCaptionTheme);
    }

    private void ApplyCaptionTheme()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        bool highContrast = SystemParameters.HighContrast;
        uint darkMode = !highContrast && ThemeManager.Current != TesseraTheme.Light ? 1u : 0u;
        DwmSetWindowAttribute(handle, UseImmersiveDarkMode, in darkMode, sizeof(uint));

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;

        uint background = highContrast ? DefaultColor : FindColor("Ts.Raised");
        uint foreground = highContrast ? DefaultColor : FindColor("Ts.Text");
        DwmSetWindowAttribute(handle, CaptionColor, in background, sizeof(uint));
        DwmSetWindowAttribute(handle, CaptionTextColor, in foreground, sizeof(uint));
    }

    private uint FindColor(string resourceKey) => TryFindResource(resourceKey) is SolidColorBrush brush
        ? brush.Color.R | (uint)brush.Color.G << 8 | (uint)brush.Color.B << 16
        : DefaultColor;

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr handle, uint attribute, in uint value, uint size);
}
