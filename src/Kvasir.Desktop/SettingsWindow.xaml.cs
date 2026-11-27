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
    private readonly BotConfiguration _botConfiguration;
    private DesktopPreferences _preferences;
    private bool _decodeOnPlayback;
    private bool _initialized;
    private RadioButton? _selectedPosition;
    internal event Action<NotificationPlacement>? NotificationPositionChanged;
    internal bool OpenJournal { get; private set; }
    public bool TokenChanged { get; private set; }

    public SettingsWindow() : this(ApplicationPaths.DesktopSettingsFile, new BotConfiguration()) { }

    internal SettingsWindow(string preferencesPath, BotConfiguration botConfiguration)
    {
        _preferencesPath = preferencesPath;
        _botConfiguration = botConfiguration;
        _preferences = DesktopPreferences.Load(preferencesPath) with { Theme = ThemeManager.Current };
        InitializeComponent();
        LoadParameters();
        LoadAudioConversion();
        LanguageBox.SelectedValue = _preferences.Language;
        UpdateLanguageHint();
        ConfigureNotificationPositions();
        ThemeButton(_preferences.Theme).IsChecked = true;
        InitializeMaintenance();
        _initialized = true;
    }

    private void LoadAudioConversion()
    {
        try
        {
            _decodeOnPlayback = _botConfiguration.ReadDecodeOnPlayback();
            AudioConversionBox.SelectedValue = _decodeOnPlayback ? "cpu" : "disk";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            AudioConversionBox.IsEnabled = false;
            SettingsMessage.Text = UiText.Get("ReadAudioConversionFailed");
        }
    }

    private void AudioConversionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || AudioConversionBox.SelectedValue is not string choice) return;
        bool enabled = choice == "cpu";
        if (enabled == _decodeOnPlayback) return;

        try
        {
            _botConfiguration.SaveDecodeOnPlayback(enabled);
            _decodeOnPlayback = enabled;
            SettingsMessage.Text = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            AudioConversionBox.SelectedValue = _decodeOnPlayback ? "cpu" : "disk";
            SettingsMessage.Text = UiText.Get("SaveAudioConversionFailed");
        }
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

    private void LoadParameters()
    {
        try
        {
            var settings = _botConfiguration.ReadSettings();
            OwnerIdBox.Text = settings.OwnerId;
            CacheLimitBox.Text = settings.CacheLimitMb.ToString();
            CacheAgeBox.Text = settings.CacheAgeDays.ToString();
            SpotifyBox.IsChecked = settings.SpotifyEnabled;
            SpotifyIdBox.Text = settings.SpotifyClientId;
            SpotifySecretBox.Password = settings.SpotifySecret;
            LogLevelBox.ItemsSource = DbLogLevelBox.ItemsSource = BotSettings.LogLevels;
            LogLevelBox.SelectedItem = settings.LogLevel;
            DbLogLevelBox.SelectedItem = settings.DatabaseLogLevel;
            FileLoggingBox.IsChecked = settings.FileLogging;
            ConsoleLoggingBox.IsChecked = settings.ConsoleLogging;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            BotParametersForm.IsEnabled = false;
            SettingsMessage.Text = UiText.Get("ConfigurationErrorTitle");
        }
    }

    private void SaveParameters_Click(object sender, RoutedEventArgs e)
    {
        string owner = OwnerIdBox.Text.Trim();
        string id = SpotifyIdBox.Text.Trim();
        string secret = SpotifySecretBox.Password.Trim();
        if (owner.Length > 0 && (!ulong.TryParse(owner, System.Globalization.NumberStyles.None, null, out ulong parsed) || parsed == 0))
            ParametersStatus.Text = UiText.Get("InvalidOwnerId");
        else if (!int.TryParse(CacheLimitBox.Text, out int limit) || limit is < 64 or > 1048576)
            ParametersStatus.Text = UiText.Get("InvalidCacheLimit");
        else if (!int.TryParse(CacheAgeBox.Text, out int days) || days is < 1 or > 3650)
            ParametersStatus.Text = UiText.Get("InvalidCacheAge");
        else if (SpotifyBox.IsChecked == true && (id.Length == 0 || secret.Length == 0))
            ParametersStatus.Text = UiText.Get("MissingSpotifyCredentials");
        else
        {
            try
            {
                _botConfiguration.SaveSettings(new(owner, limit, days, SpotifyBox.IsChecked == true, id, secret,
                    (string)LogLevelBox.SelectedItem, (string)DbLogLevelBox.SelectedItem, FileLoggingBox.IsChecked == true, ConsoleLoggingBox.IsChecked == true));
                ParametersStatus.Text = UiText.Get("BotParametersSaved");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                ParametersStatus.Text = UiText.Get("SaveBotParametersFailed");
            }
        }
    }
    private void ReleaseNotes_Click(object sender, RoutedEventArgs e)
    {
        if (ReleaseNotesWindow.LoadCatalog() is not { } notes)
        {
            SettingsMessage.Text = UiText.Get("ReleaseNotesUnavailable");
            return;
        }
        new ReleaseNotesWindow(notes, ReleaseNotesState.Local) { Owner = this }.ShowDialog();
    }
    private void Token_Click(object sender, RoutedEventArgs e)
    {
        if (new TokenDialog(configuration: _botConfiguration) { Owner = this }.ShowDialog() == true) { TokenChanged = true; Close(); }
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
