using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Kvasir.Configuration;
using Kvasir.Desktop.Localization;
using Kvasir.Desktop.Services;
using Tessera.WpfStyles.Controls;
using Tessera.WpfStyles.Themes;

namespace Kvasir.Desktop;

public partial class SettingsWindow : Window
{
    private readonly string _preferencesPath;
    private DesktopPreferences _preferences;
    private bool _initialized;
    private RadioButton? _selectedPosition;
    internal event Action<NotificationPlacement>? NotificationPositionChanged;
    internal bool OpenJournal { get; private set; }
    public bool TokenChanged { get; private set; }

    public SettingsWindow() : this(ApplicationPaths.DesktopSettingsFile) { }

    internal SettingsWindow(string preferencesPath)
    {
        _preferencesPath = preferencesPath;
        _preferences = DesktopPreferences.Load(preferencesPath) with { Theme = ThemeManager.Current };
        InitializeComponent();
        ConfigurationPathBox.Text = ApplicationPaths.ConfigurationFile;
        LanguageBox.SelectedValue = _preferences.Language;
        UpdateLanguageHint();
        ConfigureNotificationPositions();
        ThemeButton(_preferences.Theme).IsChecked = true;
        _initialized = true;
    }

    private void LanguageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || LanguageBox.SelectedValue is not string language || language == _preferences.Language) return;
        if (TrySave(_preferences with { Language = language }))
            UpdateLanguageHint();
        else
            LanguageBox.SelectedValue = _preferences.Language;
    }

    private void UpdateLanguageHint() =>
        LanguageSavedHint.Visibility = _preferences.Language == UiText.Language ? Visibility.Collapsed : Visibility.Visible;

    private void ConfigureNotificationPositions()
    {
        (NotificationPlacement? Position, string Arrow)[] choices =
        [
            (NotificationPlacement.TopLeft, "↖"),
            (NotificationPlacement.TopCenter, "↑"),
            (NotificationPlacement.TopRight, "↗"),
            (NotificationPlacement.MiddleLeft, "←"),
            (null, ""),
            (NotificationPlacement.MiddleRight, "→"),
            (NotificationPlacement.BottomLeft, "↙"),
            (NotificationPlacement.BottomCenter, "↓"),
            (NotificationPlacement.BottomRight, "↘")
        ];
        foreach (var (position, arrow) in choices)
        {
            if (position is not { } anchor)
            {
                NotificationPositions.Children.Add(new Border());
                continue;
            }

            string label = UiText.Get("Notification" + anchor);
            var button = new RadioButton
            {
                GroupName = "NotificationPosition",
                Content = new TextBlock { Text = arrow, FontSize = 16 },
                ToolTip = label,
                Style = (Style)FindResource("NotificationPositionChoice"),
                IsChecked = anchor == _preferences.NotificationPosition
            };
            AutomationProperties.SetName(button, label);
            if (button.IsChecked == true) SelectPosition(button, label, anchor);
            button.Checked += (_, _) =>
            {
                if (anchor == _preferences.NotificationPosition) return;
                if (TrySave(_preferences with { NotificationPosition = anchor }))
                {
                    SelectPosition(button, label, anchor);
                    NotificationPositionChanged?.Invoke(anchor);
                }
                else if (_selectedPosition != null)
                    _selectedPosition.IsChecked = true;
            };
            NotificationPositions.Children.Add(button);
        }
    }

    private void SelectPosition(RadioButton button, string label, NotificationPlacement position)
    {
        _selectedPosition = button;
        NotificationPositionValue.Text = label;
        NotificationPreview.Placement = position;
    }

    private void PreviewNotification_Click(object sender, RoutedEventArgs e)
    {
        NotificationPreview.Clear();
        NotificationPreview.Show(UiText.Get("NotificationPreviewText"), heading: UiText.Get("Notification"));
    }

    private RadioButton ThemeButton(TesseraTheme theme) => theme switch
    {
        TesseraTheme.Light => lightTheme,
        TesseraTheme.Contrast => contrastTheme,
        _ => darkTheme
    };

    private void ThemeSelected(object sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not RadioButton { Tag: string key } ||
            !Enum.TryParse(key, out TesseraTheme theme) || theme == _preferences.Theme) return;
        if (TrySave(_preferences with { Theme = theme }))
            ThemeManager.Apply(theme);
        else
            ThemeButton(_preferences.Theme).IsChecked = true;
    }

    private bool TrySave(DesktopPreferences next)
    {
        try
        {
            next.Save(_preferencesPath);
            _preferences = next;
            SettingsMessage.Text = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SettingsMessage.Text = UiText.Get("SaveAppearanceFailed");
            return false;
        }
    }

    private void OpenConfiguration_Click(object sender, RoutedEventArgs e) => OpenPath(ApplicationPaths.ConfigurationFile);
    private void Token_Click(object sender, RoutedEventArgs e)
    {
        if (new TokenDialog { Owner = this }.ShowDialog() == true) { TokenChanged = true; Close(); }
    }
    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => OpenPath(ApplicationPaths.DataDirectory);
    private void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception) { SettingsMessage.Text = UiText.Get("OpenFileOrDirectoryFailed"); }
    }
    private void Journal_Click(object sender, RoutedEventArgs e)
    {
        OpenJournal = true;
        Close();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
}
