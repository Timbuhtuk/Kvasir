using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Kvasir.Control;
using Kvasir.Configuration;
using Kvasir.Desktop.Assets;
using Kvasir.Desktop.Localization;
using Kvasir.Desktop.Services;
using Kvasir.Desktop.ViewModels;
using Tessera.WpfStyles.Controls;

namespace Kvasir.Desktop;

public partial class MainWindow : Window
{
    private static readonly string[] EffectFileExtensions = [".wav", ".mp3", ".ogg", ".flac", ".m4a", ".aac", ".webm"];
    private readonly ControlViewModel _viewModel;
    private bool _closing, _canClose, _errorOpen;
    private JournalWindow? _journal;
    private SoundboardWindow? _soundboardWindow;

    public MainWindow()
    {
        InitializeComponent();
        Notifications.Placement = DesktopPreferences.Load().NotificationPosition;
        _viewModel = new(Dispatcher);
        DataContext = _viewModel;
        _viewModel.ProblemRaised += ShowProblemAsync;
        _viewModel.CommandSucceeded += (message, server) => Notifications.Show(message, NotificationKind.Success, server ?? UiText.Get("Done"));
        _viewModel.RequestToken = ShowTokenAsync;
        InitializeUpdateChecks();
        Loaded += async (_, _) =>
        {
            try
            {
                if (new BotConfiguration().ReadToken() == null && await ShowTokenAsync(false))
                    await _viewModel.StartAsync();
            }
            catch (Exception ex) { await ShowProblemAsync(ProblemCatalog.Describe(ex.ToString(), "configuration")); }
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
            ShowNewReleaseNotes();
        };
    }

    private void ShowNewReleaseNotes()
    {
        if (!ReleaseNotes.IsReleaseBuild || _closing || !IsVisible ||
            ReleaseNotesWindow.LoadCatalog() is not { } notes) return;
        ReleaseNotesState state = ReleaseNotesState.Local;
        if (state.HasSeen(notes.Current.Version)) return;
        new ReleaseNotesWindow(notes, state) { Owner = this, ShowActivated = false }.Show();
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await _viewModel.StartAsync();
    private async void Stop_Click(object sender, RoutedEventArgs e) => await _viewModel.StopAsync();
    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow { Owner = this };
        settings.NotificationPositionChanged += position => Notifications.Placement = position;
        settings.AutoCheckUpdatesChanged += SetAutomaticUpdateChecks;
        settings.ShowAvailableRelease(_availableRelease);
        settings.ShowDialog();
        if (settings.OpenJournal) ShowJournal();
        if (settings.TokenChanged && await _viewModel.StopAsync()) await _viewModel.StartAsync();
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_canClose) return;
        e.Cancel = true;
        await CloseAfterStoppingAsync();
    }

    internal async Task<bool> CloseAfterStoppingAsync(Action? startHelper = null)
    {
        if (_closing) return false;
        _closing = true;
        try
        {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            if (!await _viewModel.StopAsync()) return false;
            startHelper?.Invoke();
            _soundboardWindow?.Close();
            _viewModel.Dispose(); _canClose = true; Close();
            return true;
        }
        finally { _closing = false; }
    }

    private void AddServer_Click(object sender, RoutedEventArgs e)
    {
        var menu = Menu(sender);
        foreach (var guild in _viewModel.Guilds)
        {
            var item = new MenuItem { Header = guild.Name, IsCheckable = true, IsChecked = _viewModel.OpenServers.Any(s => s.Guild.Id == guild.Id) };
            item.Click += (_, _) => _viewModel.OpenServer(guild);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = UiText.Get("ServersNotLoaded"), IsEnabled = false });
        menu.IsOpen = true;
    }
    private void CloseServer_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ServerWorkspace tab) _viewModel.CloseServer(tab);
        e.Handled = true;
    }
    private ContextMenu Menu(object sender)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = (UIElement)sender, Placement = PlacementMode.Custom, DataContext = _viewModel,
            CustomPopupPlacementCallback = (popup, target, _) =>
            [
                new(new Point(target.Width - popup.Width, target.Height), PopupPrimaryAxis.Vertical),
                new(new Point(target.Width - popup.Width, -popup.Height), PopupPrimaryAxis.Vertical),
            ],
        };
        menu.SetResourceReference(StyleProperty, "Ts.ContextMenu");
        ((FrameworkElement)sender).ContextMenu = menu;
        return menu;
    }
    private void AddAction(ContextMenu menu, string title, string action, string icon)
    {
        menu.Items.Add(new MenuItem
        {
            Header = title, Icon = new SvgIcon { Kind = icon, Width = 16, Height = 16 },
            Command = _viewModel.ActionCommand, CommandParameter = action,
        });
    }
    private void QueueMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        var row = (ListBoxItem)sender;
        if (!_viewModel.GuildEnabled || row.DataContext is not QueueItem || row.ContextMenu is not { } menu)
        {
            e.Handled = true;
            return;
        }

        row.IsSelected = true;
        if (menu.Items.Count != 0) return;

        AddAction(menu, UiText.Get("PlayNext"), "queue-next", "Next");
        AddAction(menu, UiText.Get("MoveUp"), "queue-up", "Up");
        AddAction(menu, UiText.Get("MoveDown"), "queue-down", "Down");
        AddAction(menu, UiText.Get("RemoveFromQueue"), "queue-remove", "Trash");
        menu.Items.Add(new Separator());
        AddAction(menu, UiText.Get("UndoLastQueueAdd"), "queue-undo", "Undo");
    }
    private void PlayerMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = Menu(sender);
        AddAction(menu, UiText.Get("StopAndClearQueue"), "stop", "Stop");
        AddAction(menu, UiText.Get("LeaveVoiceChannel"), "disconnect", "Disconnect");
        menu.IsOpen = true;
    }
    private void PlaylistTrackMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        if (!_viewModel.GuildEnabled || _viewModel.SelectedPlaylist == null ||
            sender is not DataGridRow { Item: TrackItem track, ContextMenu: { } menu } row)
        {
            e.Handled = true;
            return;
        }

        row.IsSelected = true;
        menu.Items.Clear();
        AddAction(menu, UiText.Get("QueueTrackFromPlaylist"), "playlist-song-play", "Plus");
        if (track.DownloadState == TrackDownloadState.Pending)
            AddAction(menu, UiText.Get("RetryTrackDownload"), "playlist-retry-track", "Refresh");
        menu.Items.Add(new Separator());
        AddAction(menu, UiText.Get("MoveTrackUp"), "playlist-up", "Up");
        AddAction(menu, UiText.Get("MoveTrackDown"), "playlist-down", "Down");
        AddAction(menu, UiText.Get("RemoveFromPlaylist"), "playlist-remove", "Trash");
    }

    private void PlaylistRowMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        if (!_viewModel.GuildEnabled || sender is not ListBoxItem { Content: PlaylistItem playlist, ContextMenu: { } menu } row)
        {
            e.Handled = true;
            return;
        }

        row.IsSelected = true;
        menu.Items.Clear();
        var edit = new MenuItem { Header = UiText.Get("EditPlaylistAction"), Icon = new SvgIcon { Kind = "Tool", Width = 16, Height = 16 } };
        edit.Click += EditPlaylist_Click;
        menu.Items.Add(edit);
        if (!playlist.IsLocal)
            AddAction(menu, UiText.Get(playlist.Shared ? "MakePrivate" : "MakeShared"), "playlist-share", "Users");
        menu.Items.Add(new Separator());
        var delete = new MenuItem { Header = UiText.Get("DeletePlaylistAction"), Icon = new SvgIcon { Kind = "Trash", Width = 16, Height = 16 } };
        delete.Click += DeletePlaylist_Click;
        menu.Items.Add(delete);
    }
    private async void CreatePlaylist_Click(object sender, RoutedEventArgs e) => await ShowPlaylistDialogAsync(null);
    private async void EditPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedPlaylist is { } playlist) await ShowPlaylistDialogAsync(playlist);
    }
    private async Task ShowPlaylistDialogAsync(PlaylistItem? playlist)
    {
        if (!_viewModel.GuildEnabled) return;
        var server = _viewModel.SelectedTab;
        var dialog = new PlaylistDialog(playlist?.Name) { Owner = this };
        if (dialog.ShowDialog() == true && ReferenceEquals(server, _viewModel.SelectedTab))
            await _viewModel.EditPlaylistAsync(playlist, dialog.Input, dialog.Shared, dialog.IsLocal);
    }
    private void DeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedPlaylist is not { } playlist) return;
        if (ConfirmRemoval(UiText.Format("DeletePlaylistPrompt", playlist.Name), UiText.Get("DeletePlaylistTitle")))
            _viewModel.ActionCommand.Execute("playlist-delete");
    }

    private bool ConfirmRemoval(string message, string title)
    {
        var labels = new TesseraDialogLabels(Yes: UiText.Get("Delete"), No: UiText.Get("Cancel"), Close: UiText.Get("Close"));
        return TesseraDialog.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question,
            labels, confirmByDefault: false) == MessageBoxResult.Yes;
    }
    private void Query_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _viewModel.ActionCommand.Execute("enqueue"); e.Handled = true; }
    }
    private void History_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.GuildEnabled || e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement((ListBox)sender, source) is not ListBoxItem { DataContext: TrackItem track }) return;

        _viewModel.SelectedHistory = track;
        _viewModel.ActionCommand.Execute("history-play");
        e.Handled = true;
    }
    private static DataGridRow? FindRow(DependencyObject? source)
    {
        while (source != null && source is not DataGridRow)
            source = source is Visual ? VisualTreeHelper.GetParent(source) : (source as FrameworkContentElement)?.Parent;
        return source as DataGridRow;
    }
    private async void ImportEffect_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = UiText.Get("AddEffectFileTitle"), CheckFileExists = true, Multiselect = true,
            Filter = UiText.Get("AudioFiles") + "|" + string.Join(";", EffectFileExtensions.Select(extension => "*" + extension)),
        };
        if (dialog.ShowDialog(this) == true) await _viewModel.ImportEffectsAsync(dialog.FileNames);
    }

    private static bool IsEffectFile(string path) =>
        EffectFileExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private string[] GetEffectDropFiles(DragEventArgs e) =>
        _viewModel.GuildEnabled && (e.AllowedEffects & DragDropEffects.Copy) != 0 &&
        e.Data.GetData(DataFormats.FileDrop) is string[] files ? files : [];

    private void Soundboard_DragOver(object sender, DragEventArgs e)
    {
        bool canDrop = GetEffectDropFiles(e).Any(IsEffectFile);
        EffectDropHighlight.Visibility = canDrop ? Visibility.Visible : Visibility.Collapsed;
        e.Effects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Soundboard_DragLeave(object sender, DragEventArgs e) =>
        EffectDropHighlight.Visibility = Visibility.Collapsed;

    private async void Soundboard_Drop(object sender, DragEventArgs e)
    {
        EffectDropHighlight.Visibility = Visibility.Collapsed;
        string[] files = GetEffectDropFiles(e).Where(IsEffectFile).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        e.Effects = files.Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (files.Length > 0) await _viewModel.ImportEffectsAsync(files);
    }

    private void OpenSoundboard_Click(object sender, RoutedEventArgs e)
    {
        if (_soundboardWindow == null)
        {
            _soundboardWindow = new SoundboardWindow { DataContext = _viewModel };
            _soundboardWindow.Closed += (_, _) => _soundboardWindow = null;
            _soundboardWindow.Show();
        }
        if (_soundboardWindow.WindowState == WindowState.Minimized) _soundboardWindow.WindowState = WindowState.Normal;
        _soundboardWindow.Activate();
    }
    private async void RemoveEffect_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not SoundEffectItem effect) return;
        if (ConfirmRemoval(UiText.Format("DeleteEffectPrompt", effect.Name), UiText.Get("DeleteEffectTitle")))
            await _viewModel.RemoveEffectAsync(effect.Id);
    }
    private void EffectsEdit_Start(object sender, MouseButtonEventArgs e) => _viewModel.EditingEffects = true;
    private void EffectsEdit_KeyDown(object sender, KeyEventArgs e) { if (SliderKey(e.Key)) _viewModel.EditingEffects = true; }
    private void EffectsEdit_LostCapture(object sender, MouseEventArgs e) => _viewModel.EditingEffects = false;
    private void EffectsVolume_Changed(object sender, MouseButtonEventArgs e) { _viewModel.SoundboardCommand.Execute("sound-volume"); _viewModel.EditingEffects = false; }
    private void EffectsVolume_KeyUp(object sender, KeyEventArgs e) { if (SliderKey(e.Key)) _viewModel.SoundboardCommand.Execute("sound-volume"); _viewModel.EditingEffects = false; }
    private void SoundboardMusicVolume_Changed(object sender, MouseButtonEventArgs e) { _viewModel.SoundboardCommand.Execute("sound-music-volume"); _viewModel.EditingPlayer = false; }
    private void SoundboardMusicVolume_KeyUp(object sender, KeyEventArgs e) { if (SliderKey(e.Key)) _viewModel.SoundboardCommand.Execute("sound-music-volume"); _viewModel.EditingPlayer = false; }
    private void DuckMusic_Click(object sender, RoutedEventArgs e) => _viewModel.SoundboardCommand.Execute("sound-duck");
    private void PlayerEdit_Start(object sender, MouseButtonEventArgs e) => _viewModel.EditingPlayer = true;
    private void PlayerEdit_KeyDown(object sender, KeyEventArgs e) { if (SliderKey(e.Key)) _viewModel.EditingPlayer = true; }
    private void PlayerEdit_LostCapture(object sender, MouseEventArgs e) => _viewModel.EditingPlayer = false;
    private void Volume_Changed(object sender, MouseButtonEventArgs e) { _viewModel.ActionCommand.Execute("volume"); _viewModel.EditingPlayer = false; }
    private void Volume_KeyUp(object sender, KeyEventArgs e) { if (SliderKey(e.Key)) _viewModel.ActionCommand.Execute("volume"); _viewModel.EditingPlayer = false; }
    private void Seek_Start(object sender, MouseButtonEventArgs e) => _viewModel.BeginSeek();
    private void Seek_Changed(object sender, MouseButtonEventArgs e) => _viewModel.EndSeek(commit: true);
    private void Seek_Canceled(object sender, RoutedEventArgs e) => _viewModel.EndSeek(commit: false);
    private void Seek_KeyDown(object sender, KeyEventArgs e) { if (SliderKey(e.Key)) _viewModel.BeginSeek(); }
    private void Seek_KeyUp(object sender, KeyEventArgs e) { if (SliderKey(e.Key)) _viewModel.EndSeek(commit: true); }
    private static bool SliderKey(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End;

    private void ShowJournal()
    {
        if (_journal == null)
        {
            _journal = new JournalWindow { Owner = this, DataContext = _viewModel };
            _journal.Closed += (_, _) => _journal = null;
            _journal.Show();
        }
        if (_journal.WindowState == WindowState.Minimized) _journal.WindowState = WindowState.Normal;
        _journal.Activate();
    }
    private Task<bool> ShowTokenAsync(bool rejected) => Task.FromResult(new TokenDialog(rejected) { Owner = this }.ShowDialog() == true);
    private async Task ShowProblemAsync(DesktopProblem problem)
    {
        if (_errorOpen || _canClose) return;
        if (problem.Code == "input")
        {
            Notifications.Show(problem.Cause, NotificationKind.Warning, problem.Title);
            return;
        }

        _errorOpen = true;
        try
        {
            if (problem.Action == ProblemAction.Token)
            {
                if (await _viewModel.StopAsync() && await ShowTokenAsync(problem.Code == "token-invalid"))
                    await _viewModel.StartAsync();
                return;
            }
            if (problem.Action == ProblemAction.Diagnostics && !_viewModel.HasServer)
                problem = problem with { Action = ProblemAction.None, ActionText = null };
            Window owner = _soundboardWindow is { IsActive: true } widget ? widget : this;
            var dialog = new ErrorDialog(problem) { Owner = owner };
            if (dialog.ShowDialog() == true)
            {
                if (problem.Action == ProblemAction.Settings) Settings_Click(this, new RoutedEventArgs());
                if (problem.Action == ProblemAction.Diagnostics) _viewModel.ViewIndex = 2;
                if (problem.Action == ProblemAction.Channel) { VoiceChannelBox.Focus(); VoiceChannelBox.IsDropDownOpen = true; }
            }
            if (dialog.OpenJournal) ShowJournal();
        }
        finally { _errorOpen = false; }
    }
}
