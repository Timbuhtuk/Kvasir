using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Kvasir.Configuration;
using Kvasir.Control;
using Kvasir.Desktop;
using Kvasir.Desktop.Localization;
using Kvasir.Desktop.Services;
using Kvasir.Desktop.ViewModels;
using Tessera.WpfStyles.Controls;
using Tessera.WpfStyles.Themes;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.FirstOrDefault() == "--soundboard-backend") { SoundboardPreview.RunBackendAsync(args).GetAwaiter().GetResult(); return; }
        if (args.FirstOrDefault() == "--soundboard-preview") { SoundboardPreview.Show(); return; }
        string root = Path.Combine(Path.GetTempPath(), "kvasir-desktop-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ConfigurationChecks(root);
            DataMigrationChecks(root);
            ProblemChecks();
            PipeChecksAsync().GetAwaiter().GetResult();
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
            ThemeChecks();
            AppearanceChecks(root);
            ReleaseNotesChecks(root);
            WorkspaceChecks();
            PlaylistDialogChecks();
            LayoutChecks();
            PlaylistColumnsLayoutChecks();
            PlaybackProgressChecks();
            Console.WriteLine($"PASS: {_checks} desktop checks; configuration, appearance, localization, problem dialogs, server tabs, WPF layout.");
        }
        finally
        {
            string full = Path.GetFullPath(root);
            if (Path.GetDirectoryName(full) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) && Path.GetFileName(full).StartsWith("kvasir-desktop-tests-", StringComparison.Ordinal))
                Directory.Delete(full, true);
        }
    }

    private static void ReleaseNotesChecks(string root)
    {
        ReleaseNotes notes = ReleaseNotes.Bundled;
        Check((!ReleaseNotes.IsReleaseBuild || notes.Current.Version == ReleaseNotes.InstalledVersion.ToString(3)) && notes.Entries.Count > 1,
            "The bundled release catalog matches the running version and includes history");
        var state = new ReleaseNotesState(Path.Combine(root, "release-notes-state.json"));
        Check(!state.HasSeen(notes.Current.Version) && state.MarkSeen(notes.Current.Version) &&
            state.HasSeen(notes.Current.Version) && !state.HasSeen("9.9.9"), "Release notes are marked as seen per version");

        var window = new ReleaseNotesWindow(notes, state);
        try
        {
            var picker = (ComboBox)window.FindName("VersionBox");
            Check(ReferenceEquals(picker.SelectedItem, notes.Current), "Release notes open on the installed version");
            picker.SelectedIndex = 1;
            Check(ReferenceEquals(picker.SelectedItem, notes.Entries[1]) && notes.Entries[1].Items.Length > 0,
                "The release notes picker opens historical entries");
            string russian = notes.Current.Heading;
            UiText.SelectLanguage("en");
            Check(notes.Current.Heading != russian && notes.Current.Items.All(item => !string.IsNullOrWhiteSpace(item)),
                "Release notes follow the interface language");
        }
        finally
        {
            window.Close();
            UiText.SelectLanguage("ru");
        }
    }

    private static void ThemeChecks()
    {
        var colors = new HashSet<Color>();
        foreach (var theme in new[] { TesseraTheme.Dark, TesseraTheme.Light, TesseraTheme.Contrast })
        {
            ThemeManager.Apply(theme);
            var window = new SettingsWindow();
            Check(ThemeManager.Current == theme && ((RadioButton)window.FindName(theme.ToString().ToLowerInvariant() + "Theme")).IsChecked == true,
                $"Tessera applies {theme} and the settings dialog reflects it");
            var canvas = window.TryFindResource("Ts.Canvas") as SolidColorBrush;
            Check(canvas != null && colors.Add(canvas.Color), $"Tessera loads a distinct {theme} canvas brush");
            window.Close();
        }
        ThemeManager.Apply(TesseraTheme.Dark);
    }

    private static void AppearanceChecks(string root)
    {
        string path = Path.Combine(root, "desktop-settings.json");
        File.WriteAllText(path, """{ "theme": "Contrast" }""");
        Check(DesktopPreferences.Load(path) == new DesktopPreferences { Theme = TesseraTheme.Contrast },
            "Theme-only settings retain the theme and get default language and notification position");
        File.WriteAllText(path, """{ "theme": 42, "language": "en", "notificationPosition": "BottomLeft" }""");
        Check(DesktopPreferences.Load(path) == new DesktopPreferences { Language = "en", NotificationPosition = NotificationPlacement.BottomLeft },
            "An invalid setting does not hide other saved settings");
        File.WriteAllText(path, """{ "theme": "Dark", "language": "ru", "notificationPosition": "TopRight" }""");

        string botConfigPath = Path.Combine(root, "audio-settings.json");
        File.WriteAllText(botConfigPath, """{ "music_client": { "cache_limit_mb": 512 } }""");
        var botConfig = new BotConfiguration(botConfigPath);
        var constructor = typeof(SettingsWindow).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
            [typeof(string), typeof(BotConfiguration)])!;
        var window = (SettingsWindow)constructor.Invoke([path, botConfig]);
        try
        {
            var audioMode = (ComboBox)window.FindName("AudioConversionBox");
            Check((string)audioMode.SelectedValue == "disk", "Older configurations default to conversion during download");
            audioMode.SelectedValue = "cpu";
            using (JsonDocument saved = JsonDocument.Parse(File.ReadAllText(botConfigPath)))
                Check(botConfig.ReadDecodeOnPlayback() && saved.RootElement.GetProperty("music_client").GetProperty("cache_limit_mb").GetInt32() == 512,
                    "CPU mode persists in the bot configuration alongside existing settings");
            audioMode.SelectedValue = "disk";
            Check(!botConfig.ReadDecodeOnPlayback(), "Switching back saves disk mode");

            var cache = (TextBox)window.FindName("CacheLimitBox");
            var owner = (TextBox)window.FindName("OwnerIdBox");
            var save = typeof(SettingsWindow).GetMethod("SaveParameters_Click", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Check(cache.Text == "512", "Bot settings show the existing cache limit");
            cache.Text = "1";
            save.Invoke(window, [window, new RoutedEventArgs()]);
            Check(botConfig.ReadSettings().CacheLimitMb == 512, "Invalid cache limit leaves configuration intact");
            owner.Text = "42";
            cache.Text = "768";
            ((ComboBox)window.FindName("LogLevelBox")).SelectedItem = "Warning";
            save.Invoke(window, [window, new RoutedEventArgs()]);
            Check(botConfig.ReadSettings() is { OwnerId: "42", CacheLimitMb: 768, LogLevel: "Warning" }, "The form saves bot settings together");

            var languages = (ComboBox)window.FindName("LanguageBox");
            languages.SelectedValue = "en";
            Check(DesktopPreferences.Load(path).Language == "en" && UiText.Language == "ru" &&
                ((TextBlock)window.FindName("LanguageSavedHint")).Visibility == Visibility.Visible,
                "Language is saved for the next startup");

            var choices = (UniformGrid)window.FindName("NotificationPositions");
            Check(choices.Children.OfType<RadioButton>().Count() == 8 && choices.Children[4] is Border,
                "The notification picker has eight positions and an empty center");
            NotificationPlacement? selectedPosition = null;
            var positionEvent = typeof(SettingsWindow).GetEvent("NotificationPositionChanged", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Action<NotificationPlacement> applyPosition = position => selectedPosition = position;
            positionEvent.GetAddMethod(nonPublic: true)!.Invoke(window, [applyPosition]);
            ((RadioButton)choices.Children[7]).IsChecked = true;
            var preview = (NotificationDisplay)window.FindName("NotificationPreview");
            Check(preview.Placement == NotificationPlacement.BottomCenter && selectedPosition == preview.Placement &&
                DesktopPreferences.Load(path).NotificationPosition == preview.Placement, "The position is saved and applied immediately");
            typeof(SettingsWindow).GetMethod("PreviewNotification_Click", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, [window, new RoutedEventArgs()]);
            Check(((StackPanel)preview.Children[0]).Children[0] is NotificationCard card &&
                card.Message == UiText.Get("NotificationPreviewText"), "Preview displays a localized Tessera notification");

            ((RadioButton)window.FindName("lightTheme")).IsChecked = true;
            Check(ThemeManager.Current == TesseraTheme.Light && DesktopPreferences.Load(path) == new DesktopPreferences
                { Theme = TesseraTheme.Light, Language = "en", NotificationPosition = NotificationPlacement.BottomCenter },
                "Theme applies immediately and preserves the chosen language and notification position");

            UiText.SelectLanguage(DesktopPreferences.Load(path).Language);
            var localized = (SettingsWindow)constructor.Invoke([path, botConfig]);
            try
            {
                Check(localized.Title == "Kvasir settings" &&
                    ((TextBlock)localized.FindName("NotificationPositionValue")).Text == "Bottom center",
                    "Reopened settings reflect the saved language and notification position");
                var main = new MainWindow();
                using var viewModel = (ControlViewModel)main.DataContext;
                Check(viewModel.Status == "Stopped" && ProblemCatalog.Describe("Выберите голосовой канал.").Action == ProblemAction.Channel,
                    "Desktop text is localized and Russian wire errors still resolve to the correct action");
            }
            finally { localized.Close(); }
            Check(UiText.ResponseMessage(new(true, "Добавлено треков: 2.")
                { MessageKey = "TracksAdded", MessageArguments = ["2"] }) == "Tracks added: 2.",
                "The desktop translates structured command responses");
            Check(UiText.ResponseMessage(new(true, "Provider detail")) == "Provider detail",
                "Older and external responses retain their text");
        }
        finally
        {
            window.Close();
            UiText.SelectLanguage("ru");
            ThemeManager.Apply(TesseraTheme.Dark);
        }
    }

    private static async Task PipeChecksAsync()
    {
        string name = "Kvasir-test-" + Guid.NewGuid().ToString("N");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        using var client = new BotControlClient();
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
        Task handling = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(timeout.Token);
            var snapshot = await ControlProtocol.ReadAsync<ControlRequest>(server, timeout.Token);
            Check(snapshot is { Session: "test-session", Action: "snapshot", GuildId: 42 }, "Named pipe delivers the session and guild");
            await ControlProtocol.WriteAsync(server, new ControlResponse(true, "",
                new ControlState { Ready = true, GuildId = 42, Guilds = [new(42, "Тестовый сервер")] }), timeout.Token);
            var stop = await ControlProtocol.ReadAsync<ControlRequest>(server, timeout.Token);
            Check(stop.Action == "sound-stop", "Desktop reuses the named pipe for the next request");
            await ControlProtocol.WriteAsync(server, new ControlResponse(false, "Нет канала", ErrorCode: "voice-channel"), timeout.Token);
            var create = await ControlProtocol.ReadAsync<ControlRequest>(server, timeout.Token);
            Check(create is { Action: "playlist-create", PlaylistId: null, Value: "https://open.spotify.com/album/fixture", Number: 1, ImportQuery: null },
                "The playlist request carries the single input and visibility");
            await ControlProtocol.WriteAsync(server, new ControlResponse(true, ""), timeout.Token);
        });
        try
        {
            ControlResponse snapshot = await client.SendAsync(name, new("test-session", GuildId: 42), timeout.Token);
            Check(snapshot is { Success: true, State.Guilds: [{ Id: 42, Name: "Тестовый сервер" }] }, "Desktop reads a real pipe snapshot with Unicode");
            ControlResponse stop = await client.SendAsync(name, new("test-session", "sound-stop", 42), timeout.Token);
            Check(stop is { Success: false, ErrorCode: "voice-channel" }, "Desktop reads structured errors through the pipe");
            await client.SendAsync(name, new("test-session", "playlist-create", 42, Value: "https://open.spotify.com/album/fixture", Number: 1), timeout.Token);
            await handling;
        }
        finally
        {
            await timeout.CancelAsync();
            try { await handling; }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        }
    }

    private static void ConfigurationChecks(string root)
    {
        string path = Path.Combine(root, "configuration.json");
        File.WriteAllText(path, """{ "tokens": [], "connection_string": "Data Source=kept.db", "music_client": { "volume": 17 }, "custom": [1, 2] }""");
        var store = new BotConfiguration(path);
        Check(store.ReadToken() == null, "Missing token detected");
        string synthetic = "synthetic-test-token-no-discord-access";
        store.SaveToken(synthetic);
        Check(new BotConfiguration(path).ReadToken() == synthetic, "Token survives reload");
        using (var json = JsonDocument.Parse(File.ReadAllText(path)))
        {
            Check(json.RootElement.GetProperty("connection_string").GetString() == "Data Source=kept.db", "Connection string retained");
            Check(json.RootElement.GetProperty("music_client").GetProperty("volume").GetInt32() == 17, "Nested settings retained");
            Check(json.RootElement.GetProperty("custom").GetArrayLength() == 2, "Unknown settings retained");
        }
        string before = File.ReadAllText(path);
        foreach (string invalid in new[] { "", " ", "short", new string('x', 513), synthetic + "\nother" })
        {
            try { store.SaveToken(invalid); Check(false, "Invalid token accepted"); }
            catch (ArgumentException) { Check(File.ReadAllText(path) == before, "Invalid input never changes config"); }
        }
        store.SaveToken(" Bot " + synthetic + " ");
        Check(store.ReadToken() == synthetic, "Bot prefix and outer whitespace normalized");
        File.WriteAllText(path, """{ "ToKeNs": ["old", "second"], "keep": true, } // comment""");
        store.SaveToken(synthetic);
        Check(store.ReadToken() == synthetic, "Case-insensitive key and JSON comments supported");
        using (var json = JsonDocument.Parse(File.ReadAllText(path)))
        {
            Check(json.RootElement.GetProperty("ToKeNs")[1].GetString() == "second", "Other configured tokens retained");
            Check(json.RootElement.EnumerateObject().Count(p => p.Name.Equals("tokens", StringComparison.OrdinalIgnoreCase)) == 1, "No duplicate token property");
        }
        File.WriteAllText(path, """{ "KVASIR_DISCORD_TOKEN": "fallback" }""");
        Check(store.ReadToken() == "fallback", "Legacy JSON token supported");
        File.WriteAllText(path, "{ invalid-json");
        try { store.SaveToken(synthetic); Check(false, "Malformed JSON overwritten"); }
        catch (JsonException) { Check(File.ReadAllText(path) == "{ invalid-json", "Malformed configuration retained"); }
        Check(Directory.GetFiles(root, "*.tmp").Length == 0, "No secret-bearing temporary file left behind");
    }

    private static void DataMigrationChecks(string root)
    {
        string source = Path.Combine(root, "Kvasir"), destination = Path.Combine(root, "new", "Kvasir");
        Directory.CreateDirectory(Path.Combine(source, "appdata"));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var location = new DataLocation(source);
        var progress = new Progress<int>();
        string file = Path.Combine(source, "configuration.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new { tokens = new[] { "fixture" }, music_client = new
            { music_folder = Path.Combine(source, "music"), preferences_file = "appdata/prefs.json" }, custom = 12 }));
        File.WriteAllText(Path.Combine(source, "appdata", "fixture"), "contents");
        using (location.OpenSession(false))
        using (location.OpenSession(false))
        {
            try { DataDirectoryMigration.Move(location, source, destination, progress); Check(false, "Moved active data"); }
            catch (IOException) { Check(location.Resolve() == source && !Directory.Exists(destination), "Active sessions prevent migration"); }
        }
        Directory.CreateDirectory(destination);
        try { DataDirectoryMigration.Move(location, source, destination, progress); Check(false, "Overwrote destination"); }
        catch (IOException) { Check(File.Exists(file), "An existing destination is left alone"); }
        Directory.Delete(destination);
        // A malformed source fails after copying. Neither the active pointer nor the destination may change.
        string original = File.ReadAllText(file);
        File.WriteAllText(file, "{");
        try { DataDirectoryMigration.Move(location, source, destination, progress); Check(false, "Migrated malformed configuration"); }
        catch (JsonException) { Check(location.Resolve() == source && !Directory.Exists(destination), "Failed migration keeps the source active"); }
        File.WriteAllText(file, original);
        Check(DataDirectoryMigration.Move(location, source, destination, progress) && location.Resolve() == destination &&
            !Directory.Exists(source) && File.ReadAllText(Path.Combine(destination, "appdata", "fixture")) == "contents", "Migration moves nested data and switches the active root");
        var config = new BotConfiguration(Path.Combine(destination, "configuration.json")).Read();
        Check(config["music_client"]!["music_folder"]!.GetValue<string>() == Path.Combine(destination, "music") &&
            config["music_client"]!["preferences_file"]!.GetValue<string>() == "appdata/prefs.json" &&
            config["tokens"]![0]!.GetValue<string>() == "fixture" && config["custom"]!.GetValue<int>() == 12, "Migration rebases only local absolute paths");
        Check(DataDirectoryMigration.Move(location, destination, source, progress) && location.Resolve() == source, "Data can be moved back to its default location");
    }

    private static void ProblemChecks()
    {
        (string Text, string Code, ProblemAction Action)[] cases =
        [
            ("RestException: 401: Unauthorized", "token-invalid", ProblemAction.Token),
            ("Выберите голосовой канал.", "voice-channel", ProblemAction.Channel),
            ("Unable to load DLL opus.dll", "dependency", ProblemAction.Diagnostics),
            ("Трек не найден или источник недоступен.", "source", ProblemAction.Diagnostics),
            ("SQLite database is locked", "database", ProblemAction.Diagnostics),
            ("UnauthorizedAccessException: Access to the path is denied", "storage", ProblemAction.Settings),
            ("JsonException", "configuration", ProblemAction.Settings),
            ("Ответ не получен вовремя", "connection", ProblemAction.None),
            ("Missing Permissions 50013", "permissions", ProblemAction.None),
            ("Disallowed intents 4014", "intents", ProblemAction.None),
            ("IOException: Источник аудио недоступен.", "source", ProblemAction.Diagnostics),
            ("yt-dlp download failed: 403 Forbidden", "source", ProblemAction.Diagnostics),
            ("IOException: pipe closed", "connection", ProblemAction.None),
            ("FileNotFoundException: ffmpeg.exe not found", "dependency", ProblemAction.Diagnostics),
        ];
        foreach (var example in cases)
        {
            var problem = ProblemCatalog.Describe(example.Text);
            Check(problem.Code == example.Code, "Wrong problem category for " + example.Code);
            Check(problem.Action == example.Action, "Wrong recovery action for " + example.Code);
            Check(problem.Recovery.Length > 20 && problem.Cause.Length > 20, "Missing cause or recovery");
        }
        Check(ProblemCatalog.Describe("401 Unauthorized", "source").Code == "source", "Explicit media error must not prompt for Discord token");
        Check(ProblemCatalog.Describe("FFmpeg timeout", "conversion-timeout").Action == ProblemAction.Diagnostics, "Conversion timeout offers audio diagnostics");
    }

    private static void WorkspaceChecks()
    {
        using var vm = new ControlViewModel(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var showMedia = typeof(ControlViewModel).GetMethod("ShowMediaStatus", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var old = DateTimeOffset.UtcNow.AddSeconds(-25);
        var item = new MediaWorkItem("Fixture", "Downloading", old, old, "yt-dlp", 1024, 25, null, null);
        showMedia.Invoke(vm, [new[] { item }, 2, false]);
        Check(vm.MediaWorking && !vm.MediaIndeterminate && vm.MediaPercent == 25 && vm.MediaText.Contains("25%") &&
            vm.MediaText.Contains(UiText.Format("MediaSilent", (int)(DateTimeOffset.UtcNow - old).TotalSeconds)),
            "Media view distinguishes a measured transfer with stale progress from an error");
        showMedia.Invoke(vm, [new[] { item with { Stage = "Failed", Detail = "fixture timeout" } }, 0, false]);
        Check(!vm.MediaWorking && vm.MediaText.Contains("fixture timeout"), "Media failure retains the cause");
        showMedia.Invoke(vm, [Array.Empty<MediaWorkItem>(), 0, true]);
        Check(vm.MediaText == UiText.Get("MediaChannelUnavailable"), "Status channel loss is distinct from a slow download");
        showMedia.Invoke(vm, [Array.Empty<MediaWorkItem>(), 0, false]);
        vm.OpenServer(new(1, "Первый сервер"));
        vm.Query = "draft-one"; vm.ViewIndex = 2; vm.Limit = "31"; vm.DjRole = "123";
        vm.OpenServer(new(2, "Второй сервер"));
        vm.Query = "draft-two"; vm.ViewIndex = 3; vm.Limit = "7";
        vm.OpenServer(new(1, "Первый сервер"));
        Check(vm.OpenServers.Count == 2, "Duplicate server tab prevented");
        Check(vm.Query == "draft-one" && vm.ViewIndex == 2 && vm.Limit == "31" && vm.DjRole == "123", "First workspace draft restored");
        vm.OpenServer(new(2, "Второй сервер"));
        Check(vm.Query == "draft-two" && vm.ViewIndex == 3 && vm.Limit == "7", "Second workspace draft isolated");
        vm.CloseServer(vm.SelectedTab!);
        Check(vm.SelectedGuild?.Id == 1 && vm.Query == "draft-one", "Closing selects adjacent server without losing draft");
        vm.CloseServer(vm.SelectedTab!);
        Check(!vm.HasServer && vm.OpenServers.Count == 0 && !vm.GuildEnabled, "Last tab closes safely");
        Check(new GuildItem(1234, "Название").ToString() == "Название" && new ChannelItem(42, "Канал").ToString() == "Канал", "No record dump or technical ID in selectors");
    }

    private static void PlaylistDialogChecks()
    {
        var create = new PlaylistDialog();
        var input = (TextBox)create.FindName("InputBox");
        var submit = (Button)create.FindName("SubmitButton");
        var scope = (ComboBox)create.FindName("ScopeBox");
        Check(!create.IsLocal && !create.Shared, "Playlist creation defaults to the private server scope");
        scope.SelectedValue = "shared";
        Check(create.Shared && !create.IsLocal, "Shared remains a server scope");
        scope.SelectedValue = "local";
        Check(create.IsLocal && !create.Shared, "The local scope is distinct from shared");
        Check(!submit.IsEnabled, "A new playlist requires a name");
        input.Text = "  Подборка  ";
        Check(submit.IsEnabled && Equals(submit.Content, UiText.Get("Create")) && create.Input == "Подборка",
            "The modal can create an empty playlist");
        string link = "https://open.spotify.com/album/fixture?si=" + new string('a', 160);
        input.Text = "  " + link + "  ";
        Check(submit.IsEnabled && Equals(submit.Content, UiText.Get("CreateAndImportPlaylist")) && create.Input == link,
            "The same field accepts a link longer than a playlist name");
        create.Close();

        var edit = new PlaylistDialog("Подборка");
        Check(((StackPanel)edit.FindName("ScopeFields")).Visibility == Visibility.Collapsed, "Editing keeps the existing playlist scope");
        input = (TextBox)edit.FindName("InputBox");
        submit = (Button)edit.FindName("SubmitButton");
        Check(input.Text == "Подборка" && !submit.IsEnabled, "Editing starts with the selected playlist name");
        input.Text = link;
        Check(submit.IsEnabled && Equals(submit.Content, UiText.Get("Import")), "Import can target the existing playlist");
        input.Text = "Новое название";
        Check(submit.IsEnabled && Equals(submit.Content, UiText.Get("SavePlaylist")), "Plain text renames the selected playlist");
        edit.Close();
    }

    private static void LayoutChecks()
    {
        var window = new MainWindow();
        using var vm = (ControlViewModel)window.DataContext;
        vm.OpenServer(new(1, "Проверка"));
        var snapshot = new ControlState
        {
            GuildId = 1, Guilds = [new(1, "Проверка")], Channels = [new(10, "Канал")],
            Queue = [new(Guid.NewGuid(), new(1, "Трек", "Исполнитель", 125, null), 5)],
            History = [new(2, "Прослушанный трек", "Исполнитель", 180, null)],
            Current = new(1, "Трек", "Исполнитель", 125, null), Status = "Playing", Position = 42,
            Settings = new(null, true, false, 10, 75),
            Effects = [new(Guid.NewGuid(), "Колокольчик", 2.5, true, null), new(Guid.NewGuid(), "Недоступный эффект", 0, false, "Повреждён")],
            Mix = new(75, 40, true, 0),
        };
        typeof(ControlViewModel).GetMethod("AcceptSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, [snapshot, vm.SelectedTab, null]);
        var content = (FrameworkElement)window.Content;
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(new Kvasir.Desktop.Controls.WorkspaceContent());
        Check(peer?.GetAutomationControlType() == System.Windows.Automation.Peers.AutomationControlType.Pane, "Headerless views must expose content as an accessible pane");
        var navigation = (ListBox)window.FindName("NavigationList");
        var toggle = (ToggleButton)window.FindName("PlaybackPanelButton");
        var panel = (Border)window.FindName("PlaybackPanel");
        var panelTabs = (TabControl)window.FindName("PlaybackPanelTabs");

        content.Measure(new Size(978, 678)); content.Arrange(new Rect(0, 0, 978, 678)); content.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Check(navigation.Items.Count == 4 && panelTabs.Items.Count == 2, "Queue and history share a panel outside workspace navigation");
        Check(toggle.IsEnabled && panel.Visibility == Visibility.Collapsed, "Playback panel starts closed and is available for an open server");
        foreach (var size in new[] { new Size(978, 678), new Size(1218, 818) })
        {
            for (int q = 0; q < navigation.Items.Count; q++)
            {
                vm.ViewIndex = q;
                toggle.IsChecked = false;
                content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
                Check(double.IsFinite(content.DesiredSize.Width) && content.DesiredSize.Width <= size.Width + 1, "View exceeds minimum window width");
                toggle.IsChecked = true;
                content.UpdateLayout();
                Check(panel.Visibility == Visibility.Visible && panel.ActualWidth > 0 && vm.ViewIndex == q &&
                    panel.TranslatePoint(new Point(panel.ActualWidth, 0), content).X <= size.Width,
                    "Opening the playback panel preserves the current workspace and fits beside it");
            }
        }
        Check(panelTabs.SelectedIndex == 0, "The playback panel opens on the queue initially");
        Check(ReferenceEquals(PanelList(panelTabs).Items[0], snapshot.Queue[0]), "Queue tab shows the active server queue");
        var queue = (Grid)((TabItem)panelTabs.SelectedItem).Content;
        var enqueue = queue.Children.OfType<DockPanel>().Single().Children.OfType<Button>().Single();
        var plus = (FrameworkElement)enqueue.Content;
        var iconCenter = plus.TranslatePoint(new Point(plus.ActualWidth / 2, plus.ActualHeight / 2), enqueue);
        var buttonCenter = new Point(enqueue.ActualWidth / 2, enqueue.ActualHeight / 2);
        Check((iconCenter - buttonCenter).Length <= 1, "The add-to-queue icon is centered");
        panelTabs.SelectedIndex = 1;
        content.UpdateLayout();
        var history = PanelList(panelTabs);
        history.SelectedIndex = 0;
        Check(ReferenceEquals(history.Items[0], snapshot.History[0]) && vm.SelectedHistory == snapshot.History[0],
            "History tab shows and selects the active server's played tracks");
        var panelToggle = (IToggleProvider)new ToggleButtonAutomationPeer(toggle);
        panelToggle.Toggle();
        Check(toggle.IsChecked == false && panel.Visibility == Visibility.Collapsed, "The player toggle closes the playback panel");
        panelToggle.Toggle();
        Check(toggle.IsChecked == true && panel.Visibility == Visibility.Visible, "The player toggle reopens the playback panel");
        Check(panelTabs.SelectedIndex == 1, "Reopening the playback panel keeps the selected tab");
        Check(vm.Effects.Length == 2 && vm.EffectsVolume == 40 && vm.DuckMusic, "Soundboard cards and per-server mix applied from snapshot");
        vm.OpenServer(new(2, "Другой сервер"));
        var second = snapshot with { GuildId = 2, Mix = new(80, 15, false, 0) };
        typeof(ControlViewModel).GetMethod("AcceptSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, [second, vm.SelectedTab, null]);
        Check(vm.EffectsVolume == 15 && !vm.DuckMusic, "Second server has independent effects mix");
        vm.OpenServer(new(1, "Проверка"));
        Check(vm.EffectsVolume == 40 && vm.DuckMusic, "Returning to server restores its mix");
        vm.Query = "Unsaved query";
        typeof(ControlViewModel).GetMethod("ResetPlayback", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null);
        Check(vm.CurrentTitle == "Ничего не играет" && vm.Position == 0 && vm.Duration == 0 && vm.Queue.Length == 0, "Stopped process must clear stale player state");
        Check(vm.SelectedTab?.Snapshot is { Current: null, Queue.Length: 0 } && vm.Query == "Unsaved query", "Stopped snapshots cannot restore playing state; drafts survive");
        ServerTabsLayoutChecks(window, vm);
        foreach (var problem in new[] { ProblemCatalog.Describe("401 Unauthorized"), ProblemCatalog.Describe("Выберите голосовой канал."), ProblemCatalog.Describe("SQLite") })
        {
            var dialog = new ErrorDialog(problem);
            Check(((TextBlock)dialog.FindName("Heading")).Text == problem.Title, "Dedicated problem heading missing");
            Check(((TextBlock)dialog.FindName("Recovery")).Text == problem.Recovery, "Actionable recovery missing");
        }
        var token = new TokenDialog(true);
        Check(token.FindName("TokenBox") is PasswordBox, "Token entry must be masked");
        Check(((TextBlock)token.FindName("Reason")).Text.Contains("401"), "Rejected token needs specific explanation");
    }

    private static void PlaylistColumnsLayoutChecks()
    {
        var previousLanguage = UiText.Language;
        var accept = typeof(ControlViewModel).GetMethod("AcceptSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!;
        try
        {
            foreach (string language in new[] { "ru", "en" })
            {
                UiText.SelectLanguage(language);
                var window = new MainWindow();
                using var vm = (ControlViewModel)window.DataContext;
                vm.OpenServer(new(1, "Local server"));
                TrackDownloadState[] downloadStates = [TrackDownloadState.Downloaded, TrackDownloadState.Pending, TrackDownloadState.Downloading];
                var tracks = Enumerable.Range(1, 24)
                    .Select(id => new TrackItem(id, $"Track {id}", "A long artist name that needs to be trimmed", 3723, null)
                    {
                        DownloadState = downloadStates[(id - 1) % downloadStates.Length]
                    })
                    .ToArray();
                var snapshot = new ControlState
                {
                    GuildId = 1,
                    Playlists = [new(1, "Playlist", 1, false, tracks.Length)]
                };
                accept.Invoke(vm, [snapshot, vm.SelectedTab, null]);
                vm.SelectedPlaylist = vm.Playlists[0];
                accept.Invoke(vm, [snapshot with { PlaylistId = 1, Songs = tracks }, vm.SelectedTab, 1]);

                var now = DateTimeOffset.UtcNow;
                var downloading = new MediaWorkItem("Test audio", "Downloading", now, now, "yt-dlp", 1024, 50, null, null);
                typeof(ControlViewModel).GetMethod("ShowMediaStatus", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(vm, [new[] { downloading }, 1, false]);
                var content = (FrameworkElement)window.Content;
                var grid = (DataGrid)window.FindName("PlaylistTrackGrid");
                var toggle = (ToggleButton)window.FindName("PlaybackPanelButton");
                var size = new Size(window.MinWidth, window.MinHeight);
                content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                toggle.IsChecked = true;
                grid.Columns.Single(column => Equals(column.Header, UiText.Get("Duration"))).SortDirection = ListSortDirection.Ascending;
                content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                content.UpdateLayout();

                var bar = (ProgressBar)window.FindName("MediaProgressBar");
                Check(bar.Visibility == Visibility.Visible && bar.ActualWidth > 0 && bar.Value == 50 && !bar.IsIndeterminate,
                    $"Measured media progress has a visible determinate bar at minimum window size ({language})");
                var scroll = (ScrollViewer)grid.Template.FindName("DG_ScrollViewer", grid);
                Check(grid.Items.Count == tracks.Length && scroll.ViewportWidth > 0 &&
                    grid.Columns.Sum(column => column.ActualWidth) <= scroll.ViewportWidth + 1,
                    $"All playlist columns must fit at minimum width with the playback panel open ({language})");

                var artistColumn = grid.Columns.Single(column => column.SortMemberPath == "Author");
                var header = FindVisual<DataGridColumnHeader>(grid, candidate => candidate.Column == artistColumn)!;
                var label = FindVisual<TextBlock>(header, candidate => candidate.Text == UiText.Get("Artist"))!;
                double headerX = label.TranslatePoint(new Point(), grid).X;
                foreach (var track in tracks.Take(downloadStates.Length))
                {
                    var artist = FindVisual<TextBlock>(artistColumn.GetCellContent(track), candidate => candidate.Text == track.Author)!;
                    Check(Math.Abs(headerX - artist.TranslatePoint(new Point(), grid).X) <= 1,
                        $"Playlist artist text must line up with its column header ({language}, {track.DownloadState})");
                }
            }
        }
        finally { UiText.SelectLanguage(previousLanguage); }
    }

    private static T? FindVisual<T>(DependencyObject parent, Predicate<T> matches) where T : DependencyObject
    {
        if (parent is T candidate && matches(candidate)) return candidate;
        for (int q = 0; q < VisualTreeHelper.GetChildrenCount(parent); q++)
        {
            var child = FindVisual(VisualTreeHelper.GetChild(parent, q), matches);
            if (child != null) return child;
        }
        return null;
    }

    private static ListBox PanelList(TabControl tabs)
    {
        var page = (Grid)((TabItem)tabs.SelectedItem).Content;
        return page.Children.OfType<ListBox>().Single();
    }

    private static void ServerTabsLayoutChecks(MainWindow window, ControlViewModel vm)
    {
        var tabs = (TabControl)window.FindName("ServerTabs");
        var second = vm.OpenServers[1];
        tabs.SetCurrentValue(Selector.SelectedItemProperty, second);
        Check(ReferenceEquals(vm.SelectedTab, second), "Selecting a Tessera tab switches the server workspace");

        var content = (FrameworkElement)window.Content;
        var size = new Size(978, 678);
        content.Measure(size);
        content.Arrange(new Rect(size));
        content.UpdateLayout();

        var header = (TabItem)tabs.ItemContainerGenerator.ContainerFromItem(second);
        Check(AutomationProperties.GetName(header) == second.Name, "Tessera tab exposes the server name");
        var close = FindTabCloseButton(header);
        Check(close != null && ReferenceEquals(close.DataContext, second), "Tessera tab close button targets its server");
        close!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(vm.OpenServers.Count == 1 && ReferenceEquals(tabs.SelectedItem, vm.SelectedTab),
            "Closing a Tessera tab selects the remaining server");
    }

    private static void PlaybackProgressChecks()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var window = new MainWindow();
        using var vm = (ControlViewModel)window.DataContext;
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(978, 678)); content.Arrange(new Rect(0, 0, 978, 678)); content.UpdateLayout();
        dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        var slider = (Slider)window.FindName("PlaybackPositionSlider");
        var operationLoader = (CarouselLoader)window.FindName("OperationLoader");
        var playbackLoader = (CarouselLoader)window.FindName("PlaybackPreparationLoader");
        var importLoader = (CarouselLoader)window.FindName("PlaylistImportLoader");
        Check(operationLoader.Visibility == Visibility.Collapsed && operationLoader.IsPaused, "The operation loader stays inactive while idle");

        var dot = (System.Windows.Shapes.Ellipse)window.FindName("BotStatusDot");
        var startIcon = (Kvasir.Desktop.Assets.SvgIcon)((Button)window.FindName("BotStartButton")).Content;
        var stopIcon = (Kvasir.Desktop.Assets.SvgIcon)((Button)window.FindName("BotStopButton")).Content;
        double CenterY(FrameworkElement element) => element.TranslatePoint(new Point(0, element.ActualHeight / 2), content).Y;
        Check(new FrameworkElement[] { startIcon, stopIcon }.All(element => Math.Abs(CenterY(element) - CenterY(dot)) <= 1),
            "Start, stop and the connection dot share a vertical center");
        Check(window.FindName("BotStatusLabel") == null && Equals(dot.ToolTip, vm.Status),
            "The bot status is shown as a tooltip on the connection dot without a header label");
        Check(dot.Fill == window.FindResource("BotDisconnectedBrush") && stopIcon.Foreground == dot.Fill,
            "The disconnected dot and stop icon use the red brush");
        vm.OpenServer(new(1, "Проверка позиции"));
        const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
        var controller = (BotProcessController)typeof(ControlViewModel).GetField("_process", fields)!.GetValue(vm)!;
        using var process = Process.GetCurrentProcess();
        typeof(BotProcessController).GetField("_process", fields)!.SetValue(controller, process);
        var refreshCommands = typeof(ControlViewModel).GetMethod("RefreshCommands", fields)!;
        refreshCommands.Invoke(vm, null);
        Check(dot.Fill == window.FindResource("BotConnectingBrush") && operationLoader.Visibility == Visibility.Visible,
            "A started process waiting for Discord shows the orange dot and carousel");
        typeof(ControlViewModel).GetField("_connected", fields)!.SetValue(vm, true);
        typeof(ControlViewModel).GetField("_discordReady", fields)!.SetValue(vm, true);
        refreshCommands.Invoke(vm, null);
        Check(dot.Fill == window.FindResource("BotConnectedBrush") && operationLoader.Visibility == Visibility.Collapsed,
            "Discord readiness turns the dot green and clears the connection loader");

        var snapshot = new ControlState { GuildId = 1, Current = new(1, "Трек", "", 120, null), Status = "Playing", Position = 10 };
        var accept = typeof(ControlViewModel).GetMethod("AcceptSnapshot", fields)!;
        void Apply(ControlState state) => accept.Invoke(vm, [state, vm.SelectedTab, null]);
        void Advance(double seconds)
        {
            typeof(ControlViewModel).GetField("_positionSampleTime", fields)!
                .SetValue(vm, Stopwatch.GetTimestamp() - (long)(seconds * Stopwatch.Frequency));
            typeof(ControlViewModel).GetMethod("AdvancePosition", fields)!.Invoke(vm, null);
        }

        var import = new ImportItem(0, 0, [], false);
        Apply(snapshot with { Status = "None", Position = 0, Import = import });
        Check(playbackLoader.Visibility == Visibility.Visible && !vm.CanSeek,
            "Preparing audio shows the carousel and disables seeking");
        Check(importLoader.Visibility == Visibility.Visible,
            "Import lookup shows the carousel before the track count is known");
        Apply(snapshot with { Import = import with { Completed = 2, Total = 5 } });
        Check(playbackLoader.Visibility == Visibility.Collapsed && vm.CanSeek, "Playback restores the seek slider");
        Check(importLoader.Visibility == Visibility.Collapsed && vm.ImportText.Length == 0,
            "Audio downloads leave the playlist-wide report hidden while rows show their own state");
        Apply(snapshot with { Import = import with { Completed = 5, Total = 5, Finished = true } });
        Check(importLoader.Visibility == Visibility.Collapsed && vm.ImportText.Contains("5/5"), "Completed import keeps its report and hides the loader");
        var grid = (DataGrid)window.FindName("PlaylistTrackGrid");
        var artistColumn = grid.Columns.OfType<DataGridTemplateColumn>().Single(column => column.SortMemberPath == "Author");
        var artistCell = (DockPanel)artistColumn.CellTemplate.LoadContent();
        var indicator = (ContentControl)artistCell.Children[0];
        var artist = (TextBlock)artistCell.Children[1];
        var track = new TrackItem(3, "Трек из импорта", "Исполнитель", 30, null);
        void LayoutArtist(TrackDownloadState state)
        {
            artistCell.DataContext = track with { DownloadState = state };
            artistCell.Measure(new Size(150, 30)); artistCell.Arrange(new Rect(0, 0, 150, 30));
            indicator.ApplyTemplate(); artistCell.UpdateLayout();
        }
        LayoutArtist(TrackDownloadState.Pending);
        Check(VisualTreeHelper.GetChild(indicator, 0) is Grid { Children: [Kvasir.Desktop.Assets.SvgIcon { Kind: "Warning" }] } &&
            artist.Text == track.Author && indicator.TranslatePoint(new Point(), artistCell).X > artist.TranslatePoint(new Point(), artistCell).X,
            "The pending track shows a warning triangle after the artist");
        LayoutArtist(TrackDownloadState.Downloading);
        Check(VisualTreeHelper.GetChild(indicator, 0) is Grid { Children: [ProgressBar { IsIndeterminate: true }] },
            "The active track renders an indeterminate progress bar in the same cell");
        LayoutArtist(TrackDownloadState.Downloaded);
        Check(indicator.Visibility == Visibility.Collapsed && indicator.ToolTip == null, "The downloaded track clears its indicator and gives the space back to the artist");
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => frame.Continue = false, dispatcher);
        PropertyChangedEventHandler moving = (_, e) =>
        {
            if (e.PropertyName == nameof(vm.Position) && vm.Position > 10.04) frame.Continue = false;
        };
        vm.PropertyChanged += moving;
        Dispatcher.PushFrame(frame);
        timeout.Stop();
        vm.PropertyChanged -= moving;
        Check(vm.Position is > 10.04 and < 11 && slider.Value == vm.Position, "Playback slider moves between bot snapshots");

        Advance(1);
        double displayed = vm.Position;
        Apply(snapshot with { Position = displayed - 0.3 });
        Check(vm.Position == displayed, "Small snapshot corrections do not jump the thumb backwards");

        vm.BeginSeek();
        vm.Position = 45;
        Apply(snapshot with { Position = 12 });
        Check(vm.Position == 45, "A snapshot cannot overwrite a seek being dragged");
        vm.EndSeek(commit: false);
        Check(vm.Position is >= 12 and < 13, "Canceling a seek restores the playback position");

        Apply(snapshot with { Status = "Paused", Position = 12.5 });
        var timer = (DispatcherTimer)typeof(ControlViewModel).GetField("_positionTimer", fields)!.GetValue(vm)!;
        Check(vm.Position == 12.5 && !timer.IsEnabled, "Pausing stops the display clock at the reported position");

        Apply(snapshot with { Position = 44 });
        Check(vm.Position == 44 && timer.IsEnabled, "Resuming or seeking uses the reported position immediately");
        Advance(6);
        Check(vm.Position == 47 && !timer.IsEnabled, "Extrapolation stops when the snapshot is stale");

        Apply(snapshot with { Current = new(2, "Следующий трек", "", 60, null), Position = 0 });
        Check(vm.Position == 0 && vm.Duration == 60, "A new track starts its own display clock");

        var paused = snapshot with { Status = "Paused", Position = 12 };
        Apply(paused);
        string name = "Kvasir-seek-" + Guid.NewGuid().ToString("N");
        typeof(BotProcessController).GetProperty(nameof(controller.PipeName))!.SetValue(controller, name);
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Task<ControlRequest> seek = Task.Run(async () =>
        {
            await pipe.WaitForConnectionAsync(deadline.Token);
            var request = await ControlProtocol.ReadAsync<ControlRequest>(pipe, deadline.Token);
            await ControlProtocol.WriteAsync(pipe, new ControlResponse(true, "Перемотка", paused), deadline.Token);
            return request;
        });
        var seeking = new DispatcherFrame();
        using var cancel = deadline.Token.Register(() => dispatcher.BeginInvoke(new Action(() => seeking.Continue = false)));
        PropertyChangedEventHandler applied = (_, e) =>
        {
            if (e.PropertyName == nameof(vm.CanSeek) && vm.CanSeek) seeking.Continue = false;
        };
        vm.PropertyChanged += applied;
        dispatcher.BeginInvoke(new Action(() =>
        {
            vm.BeginSeek();
            slider.SetCurrentValue(RangeBase.ValueProperty, 80d);
            vm.EndSeek(commit: true);
            Check(operationLoader.Visibility == Visibility.Visible, "A command shows a loader while the bot response is pending");
        }));
        Dispatcher.PushFrame(seeking);
        vm.PropertyChanged -= applied;
        Check(!deadline.IsCancellationRequested && seek.GetAwaiter().GetResult() is { Action: "seek", Number: 80 },
            "Releasing the slider sends the selected position to the bot");
        Check(slider.Value == 80 && !timer.IsEnabled, "A seek acknowledgement keeps the new paused position even with the old snapshot");
        Check(operationLoader.Visibility == Visibility.Collapsed, "A command acknowledgement clears the loader");
    }

    private static Button? FindTabCloseButton(DependencyObject element)
    {
        if (element is Button button && AutomationProperties.GetName(button) == "Закрыть вкладку сервера")
            return button;

        int children = VisualTreeHelper.GetChildrenCount(element);
        for (int q = 0; q < children; q++)
        {
            var close = FindTabCloseButton(VisualTreeHelper.GetChild(element, q));
            if (close != null)
                return close;
        }

        return null;
    }
}
