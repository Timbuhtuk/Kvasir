using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kvasir.Control;
using Kvasir.Configuration;
using Kvasir.Desktop.Localization;
using Kvasir.Desktop.Services;

namespace Kvasir.Desktop.ViewModels;

public enum BotConnectionState { Disconnected, Connecting, Connected }

public sealed partial class ControlViewModel : INotifyPropertyChanged, IDisposable
{
    private const double MaxPositionExtrapolation = 3;
    private const double MaxSmoothPositionCorrection = 0.75;
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ImportSnapshotInterval = TimeSpan.FromMilliseconds(500);

    private readonly BotProcessController _process = new();
    private readonly BotControlClient _client = new();
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer, _errorTimer, _positionTimer;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly Queue<string> _log = new();
    private readonly HashSet<string> _reportedLogErrors = [];
    private readonly Queue<DesktopProblem> _problems = new();
    private bool _showingProblem;
    private CancellationTokenSource _session = new();
    private bool _busy, _lifecycleBusy, _starting, _connected, _discordReady, _applying, _settingsDirty, _fatalReported, _disposed;
    private bool _loadingSelection;
    private int _selectionVersion;
    public bool EditingPlayer { get; set; }
    private string _status = UiText.Get("Stopped"), _logText = "", _search = "", _query = "";
    private ServerWorkspace? _selectedTab;
    private ChannelItem? _selectedChannel;
    private PlaylistItem? _selectedPlaylist;
    private QueueItem? _selectedEntry;
    private TrackItem? _selectedSong, _selectedHistory;
    private bool _voteSkip, _fairQueue;
    private string _djRole = "", _limit = "10";
    private double _volume = 75, _position;
    private double _positionSample, _positionCorrection;
    private long _positionSampleTime;
    private int? _currentTrackId;
    private string? _coverVideoId;
    private CancellationTokenSource? _coverRequest;
    private BitmapSource? _currentCover;
    private bool _editingPosition, _seekPending;
    private ulong _loadedGuild;
    private int _viewIndex;
    private string _playback = "None", _repeat = "off";
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Func<DesktopProblem, Task>? ProblemRaised;
    internal event Action<string, string?>? CommandSucceeded;
    public Func<bool, Task<bool>>? RequestToken { get; set; }
    public AsyncCommand ActionCommand { get; }
    public ObservableCollection<ServerWorkspace> OpenServers { get; } = [];
    public bool Running => _process.Running;
    public bool CanStart => !Running && !_lifecycleBusy;
    public bool CanStop => Running && !_lifecycleBusy;
    public bool CanSelect => Running && _connected && !_busy && !_lifecycleBusy;
    public bool GuildEnabled => CanSelect && SelectedGuild != null && SelectedGuild.Id == _loadedGuild;
    public bool HasServer => SelectedTab != null;
    public string WelcomeTitle => Running ? UiText.Get("OpenServerHeading") : UiText.Get("ReadyForMusic");
    public string WelcomeDetail => UiText.Get(Running ? "OpenServerHint" : "StartBotHint");
    public string Status { get => _status; private set => Set(ref _status, value); }
    public BotConnectionState ConnectionState
    {
        get
        {
            if (_starting) return BotConnectionState.Connecting;
            if (!Running || _session.IsCancellationRequested) return BotConnectionState.Disconnected;
            return _connected && _discordReady ? BotConnectionState.Connected : BotConnectionState.Connecting;
        }
    }
    public bool IsWaiting => _lifecycleBusy || Running && !_session.IsCancellationRequested &&
        (!_connected || !_discordReady || _busy || _mixPending > 0 || _loadingSelection || ConnectingEffect);
    public string LogText { get => _logText; private set => Set(ref _logText, value); }
    public GuildItem[] Guilds { get; private set; } = [];
    public ChannelItem[] Channels { get; private set; } = [];
    public QueueItem[] Queue { get; private set; } = [];
    public TrackItem[] History { get; private set; } = [];
    public PlaylistItem[] Playlists { get; private set; } = [];
    public ObservableCollection<TrackItem> Songs { get; } = [];
    public CheckItem[] Checks { get; private set; } = [];
    public string CurrentTitle { get; private set; } = UiText.Get("NothingPlaying");
    public string CurrentDetail { get; private set; } = UiText.Get("AddTrackToQueueHint");
    public BitmapSource? CurrentCover => _currentCover;
    public bool HasCurrentCoverSource => _coverVideoId != null;
    public bool IsCurrentCoverLoading => _coverRequest != null;
    public string PositionText => $"{Time(_position)} / {Time(Duration)}";
    public double Position
    {
        get => _position;
        set
        {
            double previous = _position;
            Set(ref _position, value);
            if (Math.Floor(previous) != Math.Floor(value)) Raise(nameof(PositionText));
        }
    }
    public double Duration { get; private set; }
    public bool IsPreparingPlayback => Running && _connected && !_lifecycleBusy && _currentTrackId.HasValue && _playback == "None";
    public bool CanSeek => GuildEnabled && Duration > 0 && !IsPreparingPlayback;
    public string PlayIcon => _playback == "Paused" || _playback == "None" ? "Play" : "Pause";
    public string PlayHint => _playback is "Paused" or "None" ? UiText.Get("ResumePlayback") : UiText.Get("PausePlayback");
    public string RepeatIcon => _repeat == "track" ? "RepeatOne" : "Repeat";
    public bool RepeatEnabled => _repeat != "off";
    public string RepeatText => _repeat switch { "track" => UiText.Get("RepeatTrack"), "queue" => UiText.Get("RepeatQueue"), _ => UiText.Get("RepeatOff") };
    public string CacheText { get; private set; } = UiText.Get("CacheCheckHint");
    private ImportItem? _import;
    private string? _reportedLocalImport;
    public bool ImportIndeterminate => _import is { Finished: false, Total: <= 0 };
    public string ImportText
    {
        get
        {
            if (_import is not { } import) return "";
            if (ImportIndeterminate) return UiText.Get("FindingImportTracks");
            if (!import.Finished) return "";
            string title = UiText.Get("ImportFinished");
            string text = import.Total > 0 ? UiText.Format("ImportCountFormat", title, import.Completed, import.Total) : title;
            return import.Failures.Length == 0 ? text : text + "\n" + string.Join("\n", import.Failures);
        }
    }
    public string Query { get => _query; set => Set(ref _query, value); }
    public string SeekText { get; set; } = "0:00";
    public string Search { get => _search; set { Set(ref _search, value); Raise(nameof(FilteredPlaylists)); } }
    public int ViewIndex { get => _viewIndex; set => Set(ref _viewIndex, value); }
    public PlaylistItem[] FilteredPlaylists => Playlists.Where(p => p.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)).ToArray();
    public GuildItem? SelectedGuild => SelectedTab?.Guild;
    public ServerWorkspace? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (ReferenceEquals(_selectedTab, value)) return;
            SaveWorkspace();
            Set(ref _selectedTab, value);
            RestoreWorkspace();
            Raise(nameof(SelectedGuild)); Raise(nameof(HasServer)); RefreshCommands();
            RefreshSelection();
        }
    }
    public ChannelItem? SelectedChannel { get => _selectedChannel; set => Set(ref _selectedChannel, value); }
    public PlaylistItem? SelectedPlaylist
    {
        get => _selectedPlaylist;
        set
        {
            int? previous = _selectedPlaylist?.Id;
            Set(ref _selectedPlaylist, value);
            if (!_applying && previous != value?.Id)
            {
                Songs.Clear(); SelectedSong = null; SetImport(null); RefreshSelection();
            }
        }
    }
    public QueueItem? SelectedEntry { get => _selectedEntry; set => Set(ref _selectedEntry, value); }
    public TrackItem? SelectedSong { get => _selectedSong; set => Set(ref _selectedSong, value); }
    public TrackItem? SelectedHistory { get => _selectedHistory; set => Set(ref _selectedHistory, value); }
    public bool VoteSkip { get => _voteSkip; set { Set(ref _voteSkip, value); if (!_applying) _settingsDirty = true; } }
    public bool FairQueue { get => _fairQueue; set { Set(ref _fairQueue, value); if (!_applying) _settingsDirty = true; } }
    public string DjRole { get => _djRole; set { Set(ref _djRole, value); if (!_applying) _settingsDirty = true; } }
    public string Limit { get => _limit; set { Set(ref _limit, value); if (!_applying) _settingsDirty = true; } }
    public double Volume { get => _volume; set => Set(ref _volume, value); }
    private readonly BotControlClient _audioClient = new();
    private readonly SemaphoreSlim _audioGate = new(1, 1);
    private double _effectsVolume = 75;
    private bool _duckMusic;
    private int _mixPending;
    private bool ConnectingEffect => SelectedTab?.Snapshot is { VoiceChannelId: null, Mix.ActiveEffects: > 0 };
    public bool EditingEffects { get; set; }
    public bool SoundboardEnabled => Running && _connected && !_lifecycleBusy && SelectedGuild?.Id == _loadedGuild;
    public SoundEffectItem[] Effects { get; private set; } = [];
    public double EffectsVolume { get => _effectsVolume; set => Set(ref _effectsVolume, value); }
    public bool DuckMusic { get => _duckMusic; set => Set(ref _duckMusic, value); }
    public string SoundboardStatus { get; private set; } = "";
    public int EffectImportTotal { get; private set; }
    public int EffectsProcessed { get; private set; }
    public bool IsImportingEffects => EffectImportTotal > 0;
    public string EffectImportProgressText => UiText.Format("EffectImportProgress", EffectsProcessed, EffectImportTotal);
    public AsyncCommand SoundboardCommand { get; }
    private sealed record ImportEffects(IReadOnlyList<string> Files);
    private sealed record RemoveEffect(Guid Id);
    public Task ImportEffectsAsync(IReadOnlyList<string> files) =>
        GuildEnabled && files.Count > 0 ? ExecuteAsync(new ImportEffects(files)) : Task.CompletedTask;
    public Task RemoveEffectAsync(Guid id) => ExecuteAsync(new RemoveEffect(id));

    private void SetEffectImportProgress(int total, int processed)
    {
        EffectImportTotal = total;
        EffectsProcessed = processed;
        Raise(nameof(EffectImportTotal));
        Raise(nameof(EffectsProcessed));
        Raise(nameof(IsImportingEffects));
        Raise(nameof(EffectImportProgressText));
    }

    private void SetSoundboardStatus(string message)
    {
        SoundboardStatus = message;
        Raise(nameof(SoundboardStatus));
    }

    private async Task ExecuteSoundboardAsync(object? parameter)
    {
        if (!SoundboardEnabled) return;
        var tab = SelectedTab;
        ControlRequest request = Request(parameter is SoundEffectItem ? "sound-play" : parameter?.ToString() ?? "");
        if (parameter is SoundEffectItem effect) request = request with { EffectId = effect.Id };
        if (request.Action == "sound-volume") request = request with { Number = Math.Round(EffectsVolume) };
        if (request.Action == "sound-music-volume") request = request with { Number = Math.Round(Volume) };
        if (request.Action == "sound-duck") request = request with { Number = DuckMusic ? 1 : 0 };
        _mixPending++;
        Raise(nameof(IsWaiting));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_session.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            await _audioGate.WaitAsync(timeout.Token);
            ControlResponse response;
            try { response = await _audioClient.SendAsync(_process.PipeName + "-audio", request, timeout.Token); }
            finally { _audioGate.Release(); }
            if (response.Mix is { } mix && tab?.Snapshot is { } snapshot)
                tab.Snapshot = snapshot with { Mix = mix, Settings = snapshot.Settings is { } settings ? settings with { Volume = mix.MusicVolume } : null };
            if (tab == SelectedTab) SetSoundboardStatus(UiText.ResponseMessage(response));
            if (!response.Success) NotifyProblem(ProblemCatalog.Describe(response.Detail ?? response.Message, response.ErrorCode));
        }
        catch (OperationCanceledException) when (_session.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppendLog(UiText.Get("SoundboardLogPrefix") + ex.Message);
            string message = UiText.Get("SoundboardNoResponse");
            if (tab == SelectedTab) SetSoundboardStatus(message);
            NotifyProblem(ProblemCatalog.Describe(message, "soundboard"));
        }
        finally { _mixPending--; Raise(nameof(IsWaiting)); }
    }

    public ControlViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        ActionCommand = new(ExecuteAsync, () => CanSelect);
        SoundboardCommand = new(ExecuteSoundboardAsync, () => SoundboardEnabled);
        _errorTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(450), DispatcherPriority.Background, async (_, _) =>
        {
            _errorTimer!.Stop();
            if (_showingProblem || _problems.Count == 0) return;
            var problem = _problems.Dequeue();
            _showingProblem = true;
            try { if (ProblemRaised != null) await ProblemRaised(problem); }
            finally { _showingProblem = false; if (_problems.Count > 0) _errorTimer.Start(); }
        }, dispatcher);
        _errorTimer.Stop();
        _process.Log += text => _dispatcher.BeginInvoke(new Action(() => ReceiveLog(text)));
        _process.Exited += (code, expected) => _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            if (_process.Running) return;
            _session.Cancel(); _connected = false; _discordReady = false; _client.Dispose(); _audioClient.Dispose();
            ResetPlayback();
            Status = expected ? UiText.Get("Stopped") : UiText.Get("BotExited");
            if (!expected && !_fatalReported)
            {
                _fatalReported = true;
                NotifyProblem(ProblemCatalog.Describe("", "process-exit"));
            }
            AppendLog(UiText.Format("ProcessExitedFormat", code)); RefreshCommands();
        }));
        _mediaTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, async (_, _) => await ReadMediaStatusAsync(), dispatcher);
        _timer = new DispatcherTimer(SnapshotInterval, DispatcherPriority.Background, async (_, _) => await RefreshAsync(), dispatcher);
        _positionTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _positionTimer.Tick += (_, _) => AdvancePosition();
        AppendLog(UiText.Get("ControlReady"));
    }

    public void BeginSeek()
    {
        if (!CanSeek) return;
        _editingPosition = true;
        _positionTimer.Stop();
    }

    public void EndSeek(bool commit)
    {
        if (!_editingPosition) return;
        _editingPosition = false;
        if (commit && CanSeek)
            ActionCommand.Execute("seek-position");
        else
        {
            if (_positionSampleTime != 0 && _connected && !_lifecycleBusy && Running)
                AdvancePosition();
            else
                Position = _positionSample;
            SyncPositionTimer();
        }
    }

    public void OpenServer(GuildItem guild)
    {
        var tab = OpenServers.FirstOrDefault(s => s.Guild.Id == guild.Id);
        if (tab == null) { tab = new(guild); OpenServers.Add(tab); }
        SelectedTab = tab;
    }

    public void CloseServer(ServerWorkspace tab)
    {
        int index = OpenServers.IndexOf(tab);
        if (ReferenceEquals(tab, SelectedTab))
            SelectedTab = OpenServers.Where(s => s != tab).ElementAtOrDefault(Math.Max(0, index - 1)) ?? OpenServers.FirstOrDefault(s => s != tab);
        OpenServers.Remove(tab);
    }

    private void SaveWorkspace()
    {
        if (SelectedTab is not { } tab) return;
        tab.ChannelId = SelectedChannel?.Id; tab.PlaylistId = SelectedPlaylist?.Id;
        tab.EntryId = SelectedEntry?.Id; tab.SongId = SelectedSong?.Id; tab.HistoryId = SelectedHistory?.Id;
        tab.Query = Query; tab.Search = Search; tab.SeekText = SeekText; tab.ViewIndex = ViewIndex;
        tab.SettingsDirty = _settingsDirty; tab.DjRole = DjRole; tab.Limit = Limit;
        tab.Draft = new(null, VoteSkip, FairQueue, 10, (int)Math.Round(Volume));
    }

    private void RestoreWorkspace()
    {
        ClearGuild();
        _applying = true;
        var tab = SelectedTab;
        Query = tab?.Query ?? ""; Search = tab?.Search ?? ""; SeekText = tab?.SeekText ?? "0:00";
        ViewIndex = tab?.ViewIndex ?? 0; _settingsDirty = tab?.SettingsDirty ?? false;
        VoteSkip = tab?.Draft.VoteSkip ?? true; FairQueue = tab?.Draft.FairQueue ?? false;
        Volume = tab?.Draft.Volume ?? 75; DjRole = tab?.DjRole ?? ""; Limit = tab?.Limit ?? "10";
        if (tab?.Snapshot is { } snapshot) Apply(snapshot, tab.ChannelId, tab.PlaylistId, tab.EntryId, tab.SongId, tab.HistoryId, isLive: false);
        _applying = false;
        Raise(nameof(SeekText));
    }

    public async Task StartAsync()
    {
        if (!CanStart) return;
        Status = UiText.Get("Connecting");
        _starting = _lifecycleBusy = true; RefreshCommands();
        bool missingToken = false;
        try
        {
            await _process.StopAsync();
            await _audioGate.WaitAsync();
            try { _audioClient.Dispose(); } finally { _audioGate.Release(); }
            _client.Dispose(); _session.Dispose(); _session = new();
            _fatalReported = false; _problems.Clear(); _errorTimer.Stop();
            _reportedLogErrors.Clear(); _reportedLocalImport = null;
            _process.Start(); Status = UiText.Get("Connecting"); SetSoundboardStatus("");
            AppendLog(UiText.Get("BotProcessStarted"));
        }
        catch (MissingBotTokenException) { Status = UiText.Get("TokenRequiredStatus"); missingToken = true; }
        catch (Exception ex) { Status = UiText.Get("StartFailed"); Error(ex.ToString(), true); }
        finally { _starting = _lifecycleBusy = false; RefreshCommands(); }
        if (missingToken && RequestToken != null && await RequestToken(false)) { await StartAsync(); return; }
        await RefreshAsync();
    }

    public async Task<bool> StopAsync()
    {
        if (_lifecycleBusy) return false;
        if (!Running) { await _process.StopAsync(); ResetPlayback(); return true; }
        Status = UiText.Get("Stopping");
        _lifecycleBusy = true; _session.Cancel(); _connected = false; _discordReady = false; RefreshCommands();
        try
        {
            await _requestGate.WaitAsync();
            try { _client.Dispose(); }
            finally { _requestGate.Release(); }
            await _audioGate.WaitAsync();
            try { _audioClient.Dispose(); } finally { _audioGate.Release(); }
            await _process.StopAsync();
            ResetPlayback();
            Status = UiText.Get("Stopped"); AppendLog(UiText.Get("BotStopped")); return true;
        }
        catch (Exception ex) { Status = UiText.Get(Running ? "StopFailed" : "Stopped"); Error(ex.Message, true); return !Running; }
        finally { _lifecycleBusy = false; RefreshCommands(); }
    }

    private void RefreshSelection()
    {
        _selectionVersion++;
        _loadingSelection = Running && SelectedTab != null;
        Raise(nameof(IsWaiting));
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (!Running || _session.IsCancellationRequested || _lifecycleBusy || _busy || !await _requestGate.WaitAsync(0)) return;
        var tab = SelectedTab;
        int selectionVersion = _selectionVersion;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_session.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            var request = Request("snapshot");
            var response = await _client.SendAsync(_process.PipeName, request, timeout.Token);
            if (!Running || _session.IsCancellationRequested) return;
            _connected = true;
            _discordReady = response.State?.Ready == true;
            if (response.State is { } state) AcceptSnapshot(state, tab, request.PlaylistId);
            Status = UiText.Get(_discordReady ? "Connected" : "ConnectingToDiscord");
        }
        catch (OperationCanceledException) { if (!_lifecycleBusy && Running) { _connected = false; Status = UiText.Get("WaitingForResponse"); } }
        catch (Exception) { _connected = false; if (Running) Status = UiText.Get("Connecting"); }
        finally
        {
            _requestGate.Release();
            if (selectionVersion == _selectionVersion) _loadingSelection = false;
            RefreshCommands();
            // A tab/playlist may have changed while the previous snapshot was in flight.
            if (Running && !_lifecycleBusy && selectionVersion != _selectionVersion)
                _ = _dispatcher.BeginInvoke(new Action(async () => await RefreshAsync()));
        }
    }

    private ControlRequest Request(string action) => new(_process.Session, action, SelectedGuild?.Id ?? 0,
        SelectedChannel?.Id, SelectedEntry?.Id, SelectedPlaylist?.Id, SelectedSong?.Id);

    private sealed record PlaylistEdit(int? Id, string Input, bool Shared, bool IsLink, bool IsLocal)
    {
        public string Action => Id == null ? "playlist-create" : IsLink ? "playlist-import" : "playlist-rename";
    }

    private sealed record PlaylistTrackEdit(int SongId, int? AnchorSongId = null, bool After = false, int? DestinationPlaylistId = null)
    {
        public string Action => DestinationPlaylistId.HasValue ? "playlist-copy-track" : "playlist-move-track";
    }

    public Task MovePlaylistTrackAsync(int songId, int anchorSongId, bool after) =>
        GuildEnabled && SelectedPlaylist != null ? ExecuteAsync(new PlaylistTrackEdit(songId, anchorSongId, after)) : Task.CompletedTask;

    public Task CopyPlaylistTrackAsync(int songId, int destinationPlaylistId) =>
        GuildEnabled && SelectedPlaylist != null ? ExecuteAsync(new PlaylistTrackEdit(songId, DestinationPlaylistId: destinationPlaylistId)) : Task.CompletedTask;

    public Task EditPlaylistAsync(PlaylistItem? playlist, string input, bool shared, bool local = false)
    {
        if (!GuildEnabled) return Task.CompletedTask;
        string? link = PlaylistInput.GetLink(input);
        return ExecuteAsync(new PlaylistEdit(playlist?.Id, link ?? input, shared, link != null, playlist == null && local));
    }

    private async Task ExecuteAsync(object? parameter)
    {
        string? action = parameter switch
        {
            PlaylistEdit playlist => playlist.Action,
            PlaylistTrackEdit track => track.Action,
            ImportEffects => "sound-import",
            RemoveEffect => "sound-remove",
            string text => text,
            _ => null,
        };
        if (action == null || !CanSelect) return;
        var tab = SelectedTab;
        ControlRequest request = Request(action);
        // Capture the target and all inputs before awaiting: switching tabs never retargets a command.
        try
        {
            if (action == "toggle-play") action = _playback == "Paused" || _playback == "None" ? "resume" : "pause";
            if (action == "cycle-repeat") action = "repeat-" + (_repeat == "off" ? "track" : _repeat == "track" ? "queue" : "off");
            request = request with { Action = action };
            if (parameter is RemoveEffect remove) request = request with { EffectId = remove.Id };
            if (action == "enqueue") request = request with { Value = Query };
            if (parameter is PlaylistEdit edit)
                request = request with
                {
                    PlaylistId = edit.Id,
                    Value = edit.Input,
                    Number = edit.Shared ? 1 : 0,
                    CreateLocalPlaylist = edit.IsLocal,
                };
            if (parameter is PlaylistTrackEdit track)
                request = request with
                {
                    SongId = track.SongId,
                    AnchorSongId = track.AnchorSongId,
                    InsertAfter = track.After,
                    DestinationPlaylistId = track.DestinationPlaylistId,
                };
            if (action == "history-play") request = request with { SongId = SelectedHistory?.Id };
            if (action.StartsWith("repeat-", StringComparison.Ordinal)) request = request with { Action = "repeat", Value = action[7..] };
            if (action == "volume") request = request with { Number = Math.Round(Volume) };
            if (action == "seek-position") request = request with { Action = "seek", Number = Position };
            if (action == "seek")
            {
                if (!double.TryParse(SeekText, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
                {
                    if (!TimeSpan.TryParseExact(SeekText, new[] { @"m\:ss", @"mm\:ss", @"h\:mm\:ss", @"hh\:mm\:ss" }, CultureInfo.InvariantCulture, out var time))
                        throw new ArgumentException(UiText.Get("SeekInputHint"));
                    seconds = time.TotalSeconds;
                }
                request = request with { Number = seconds };
            }
            if (action == "settings")
            {
                if (!int.TryParse(Limit, out int limit) || limit is < 1 or > 1000) throw new ArgumentException(UiText.Get("TrackLimitInputHint"));
                ulong? role = null;
                if (!string.IsNullOrWhiteSpace(DjRole) && DjRole != "0")
                    role = ulong.TryParse(DjRole, out ulong parsed) ? parsed : throw new ArgumentException(UiText.Get("DjRoleInputHint"));
                request = request with { Settings = new(role, VoteSkip, FairQueue, limit, (int)Math.Round(Volume)) };
            }
        }
        catch (ArgumentException ex) { Error(ex.Message, true, "input"); return; }
        _seekPending = request.Action == "seek";
        int? seekTrack = _seekPending ? _currentTrackId : null;
        _busy = true; RefreshCommands();
        var sessionToken = _session.Token;
        var import = parameter as ImportEffects;
        int imported = 0;
        bool batch = import?.Files.Count > 1;
        DesktopProblem? importProblem = null;
        if (import != null) SetEffectImportProgress(import.Files.Count, 0);
        bool entered = false;
        try
        {
            await _requestGate.WaitAsync(sessionToken);
            entered = true;
            IEnumerable<ControlRequest> requests = import == null ? [request] : import.Files.Select(file => request with { Value = file });
            foreach (var current in requests)
            {
                sessionToken.ThrowIfCancellationRequested();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
                timeout.CancelAfter(action is "cache-clean" or "cache-repair" ? TimeSpan.FromMinutes(4) : TimeSpan.FromMinutes(2));
                ControlResponse response = await _client.SendAsync(_process.PipeName, current, timeout.Token);
                int? playlistId = current.PlaylistId;
                if (response.Success && action == "settings" && tab != null)
                {
                    tab.SettingsDirty = false;
                    if (tab == SelectedTab) _settingsDirty = false;
                }
                if (response.Success && action == "playlist-create" && response.State?.PlaylistId is { } created && tab != null)
                {
                    tab.PlaylistId = created;
                    if (tab == SelectedTab)
                    {
                        _applying = true;
                        var playlist = response.State.Playlists.FirstOrDefault(p => p.Id == created);
                        if (playlist != null && !playlist.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)) Search = "";
                        SelectedPlaylist = playlist;
                        _applying = false;
                    }
                    playlistId = created;
                }
                if (response.State is { } state)
                {
                    // The audio loop may apply a successful seek just after the bot takes its snapshot.
                    if (response.Success && seekTrack != null && state.Current?.Id == seekTrack && state.GuildId == current.GuildId)
                        state = state with { Position = current.Number };
                    if (_seekPending && tab == SelectedTab) _positionSampleTime = 0;
                    _seekPending = false;
                    AcceptSnapshot(state, tab, playlistId);
                }
                string responseMessage = UiText.ResponseMessage(response);
                string message = (response.Success ? "" : UiText.Get("ErrorPrefix")) + (string.IsNullOrEmpty(responseMessage) ? UiText.Get("Done") : responseMessage);
                if (action is "sound-import" or "sound-remove") SetSoundboardStatus(message);
                AppendLog((tab == null ? "" : tab.Name + ": ") + message);
                if (!response.Success)
                {
                    string? code = response.ErrorCode;
                    if (import != null && code == "soundboard") code = "sound-import";
                    var problem = ProblemCatalog.Describe(code == "input" ? responseMessage : response.Detail ?? response.Message, code);
                    if (batch)
                    {
                        string cause = System.IO.Path.GetFileName(current.Value) + " — " + problem.Cause;
                        AppendLog(cause);
                        importProblem ??= problem with { Cause = cause };
                    }
                    else NotifyProblem(problem);
                }
                else if (!batch && !string.IsNullOrWhiteSpace(responseMessage) &&
                    action is "enqueue" or "history-play" or "playlist-play" or "playlist-song-play" or
                        "playlist-create" or "playlist-rename" or "playlist-share" or "playlist-delete" or
                        "playlist-add-current" or "playlist-remove" or "playlist-import" or "playlist-move-track" or "playlist-copy-track" or "playlist-retry-track" or
                        "queue-undo" or "settings" or "sound-import" or "sound-remove")
                    CommandSucceeded?.Invoke(responseMessage, tab?.Name);
                if (import != null)
                {
                    if (response.Success) imported++;
                    SetEffectImportProgress(import.Files.Count, EffectsProcessed + 1);
                }
            }
            if (importProblem != null) NotifyProblem(importProblem);
        }
        catch (OperationCanceledException)
        {
            _connected = false;
            if (Running && !sessionToken.IsCancellationRequested) Status = UiText.Get("WaitingForResponse");
            if (!_lifecycleBusy) Error(UiText.Get("CommandTimedOut"), true, "connection");
        }
        catch (Exception ex) when (ex is System.IO.IOException or TimeoutException)
        {
            _connected = false;
            if (Running && !sessionToken.IsCancellationRequested) Status = UiText.Get("WaitingForResponse");
            Error(UiText.Get("BotConnectionLost") + ex.Message, true, "connection");
        }
        catch (Exception ex) { Error(ex.Message, true); }
        finally
        {
            if (entered) _requestGate.Release();
            if (import != null)
            {
                if (batch && !sessionToken.IsCancellationRequested)
                {
                    string summary = UiText.Format("EffectImportResult", imported, import.Files.Count);
                    SetSoundboardStatus(summary);
                    if (imported == import.Files.Count) CommandSucceeded?.Invoke(summary, tab?.Name);
                }
                SetEffectImportProgress(0, 0);
            }
            _seekPending = false; _busy = false; RefreshCommands();
            if (_loadingSelection) _ = RefreshAsync();
        }
    }

    private void AcceptSnapshot(ControlState state, ServerWorkspace? tab, int? requestedPlaylist)
    {
        if (!Effects.SequenceEqual(state.Effects)) { Effects = state.Effects; Raise(nameof(Effects)); }
        Guilds = state.Guilds; Raise(nameof(Guilds));
        if (tab != null && state.GuildId == tab.Guild.Id)
        {
            tab.Snapshot = state;
            string? failure = state.LastErrorDetail ?? state.LastError;
            if (!string.IsNullOrWhiteSpace(failure) && tab.ReportedError != failure)
            {
                tab.ReportedError = failure;
                AppendLog(tab.Name + ": " + failure);
                NotifyProblem(ProblemCatalog.Describe(failure, "playback"));
            }
            if (string.IsNullOrWhiteSpace(failure)) tab.ReportedError = null;
            tab.ReportedImport = ReportImportFailure(state.Import, tab.ReportedImport, tab.Name);
            if (tab == SelectedTab)
                _reportedLocalImport = ReportImportFailure(state.LocalImport, _reportedLocalImport,
                    state.Playlists.FirstOrDefault(p => p.Id == state.LocalImport?.PlaylistId)?.Name ?? UiText.Get("LocalPlaylist"));
            if (tab == SelectedTab && requestedPlaylist == SelectedPlaylist?.Id)
                Apply(state, SelectedChannel?.Id, SelectedPlaylist?.Id, SelectedEntry?.Id, SelectedSong?.Id, SelectedHistory?.Id);
        }
        else if (tab == SelectedTab && tab != null)
        {
            ClearGuild();
            CurrentDetail = UiText.Get("ServerUnavailable"); Raise(nameof(CurrentDetail));
        }
        Checks = state.Checks; Raise(nameof(Checks));
        if (state.Cache is { } cache)
            CacheText = UiText.Format("CacheSummaryFormat", cache.Bytes / 1048576d, cache.LimitBytes / 1048576d,
                cache.Files, cache.InvalidFiles, cache.Repaired, cache.Removed, cache.FailedRepairs);
        Raise(nameof(CacheText));
    }

    private string? ReportImportFailure(ImportItem? import, string? previous, string source)
    {
        if (import is not { Finished: true, Failures.Length: > 0 }) return null;
        string incident = $"{import.PlaylistId}\n{string.Join("\n", import.Failures)}";
        if (previous == incident) return incident;

        string detail = UiText.Format("ImportFailuresFormat", source, import.Completed, import.Total, import.Failures.Length);
        AppendLog(detail + " " + import.Detail);
        if (import.Total == 0 && import.Detail != null)
            NotifyProblem(ProblemCatalog.Describe(import.Detail, import.ErrorCode));
        else
        {
            detail += "\n" + string.Join("\n", import.Failures.Take(3));
            if (import.Detail != null) detail += "\n" + ProblemCatalog.Describe(import.Detail, import.ErrorCode).Cause;
            NotifyProblem(ProblemCatalog.Describe(detail, "import-partial"));
        }
        return incident;
    }

    private void Apply(ControlState state, ulong? channel, int? playlist, Guid? entry, int? song, int? history, bool isLive = true)
    {
        _applying = true;
        try
        {
            bool sameTrack = _loadedGuild == state.GuildId && _currentTrackId == state.Current?.Id;
            _loadedGuild = state.GuildId;
            Channels = state.Channels; Raise(nameof(Channels));
            SelectedChannel = Channels.FirstOrDefault(c => c.Id == (state.VoiceChannelId ?? channel));
            if (!Queue.SequenceEqual(state.Queue)) { Queue = state.Queue; Raise(nameof(Queue)); }
            SelectedEntry = Queue.FirstOrDefault(e => e.Id == entry);
            if (!History.SequenceEqual(state.History)) { History = state.History; Raise(nameof(History)); }
            SelectedHistory = History.FirstOrDefault(s => s.Id == history);
            if (!Playlists.SequenceEqual(state.Playlists))
            {
                Playlists = state.Playlists; Raise(nameof(Playlists)); Raise(nameof(FilteredPlaylists));
            }
            SelectedPlaylist = Playlists.FirstOrDefault(p => p.Id == playlist);
            ApplySongs(SelectedPlaylist?.Id == state.PlaylistId ? state.Songs : []);
            SelectedSong = Songs.FirstOrDefault(s => s.Id == song);
            CurrentTitle = state.Current?.Name ?? UiText.Get("NothingPlaying");
            CurrentDetail = state.Current == null ? UiText.Get("AddTrackByNameHint") :
                state.Status == "None" ? UiText.Get("PreparingAudio") : state.Current.Author;
            UpdateCurrentCover(state.Current?.Link);
            UpdatePlaybackPosition(state, sameTrack, isLive);
            _repeat = state.Repeat;
            if (!_settingsDirty && state.Settings is { } settings)
            {
                VoteSkip = settings.VoteSkip; FairQueue = settings.FairQueue;
                Limit = settings.PerUserLimit.ToString(CultureInfo.InvariantCulture); DjRole = settings.DjRoleId?.ToString() ?? "";
            }
            if (!EditingPlayer && state.Settings != null) Volume = state.Settings.Volume;
            if (!EditingEffects && _mixPending == 0 && state.Mix != null)
            {
                EffectsVolume = state.Mix.EffectsVolume; DuckMusic = state.Mix.DuckMusic;
            }
            var import = SelectedPlaylist?.IsLocal == true ? state.LocalImport : state.Import;
            SetImport(import is { PlaylistId: { } id } && id != SelectedPlaylist?.Id ? null : import);
        }
        finally { _applying = false; RaisePlayer(); }
    }

    private void UpdateCurrentCover(string? link)
    {
        string? videoId = YouTubeCoverCache.GetVideoId(link);
        if (_coverVideoId == videoId) return;

        _coverRequest?.Cancel();
        _coverVideoId = videoId;
        _coverRequest = videoId == null ? null : new CancellationTokenSource();
        Set(ref _currentCover, null, nameof(CurrentCover));
        Raise(nameof(HasCurrentCoverSource));
        Raise(nameof(IsCurrentCoverLoading));
        if (videoId != null && _coverRequest is { } request)
            _ = LoadCurrentCoverAsync(videoId, request);
    }

    private async Task LoadCurrentCoverAsync(string videoId, CancellationTokenSource request)
    {
        var token = request.Token;
        try
        {
            var image = await Task.Run(() => YouTubeCoverCache.Shared.GetAsync(videoId, token), token);
            if (ReferenceEquals(_coverRequest, request)) Set(ref _currentCover, image, nameof(CurrentCover));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_coverRequest, request))
            {
                _coverRequest = null;
                Raise(nameof(IsCurrentCoverLoading));
            }
            request.Dispose();
        }
    }

    private void ApplySongs(TrackItem[] songs)
    {
        int sharedCount = Math.Min(Songs.Count, songs.Length);
        for (int q = 0; q < sharedCount; q++)
            if (Songs[q] != songs[q]) Songs[q] = songs[q];
        while (Songs.Count > songs.Length) Songs.RemoveAt(Songs.Count - 1);
        for (int q = sharedCount; q < songs.Length; q++) Songs.Add(songs[q]);
    }

    private void UpdatePlaybackPosition(ControlState state, bool sameTrack, bool isLive)
    {
        double duration = state.Current?.Duration ?? 0;
        Duration = double.IsFinite(duration) ? Math.Max(0, duration) : 0;
        double reported = double.IsFinite(state.Position) ? Math.Clamp(state.Position, 0, Duration) : 0;
        bool playing = state.Status == "Playing" && Duration > 0;
        double drift = Position - reported;
        bool smooth = isLive && sameTrack && playing && _playback == "Playing" && _positionSampleTime != 0 &&
            !_editingPosition && !_seekPending && Math.Abs(drift) <= MaxSmoothPositionCorrection;

        _playback = state.Status;
        _currentTrackId = state.Current?.Id;
        _positionSample = reported;
        _positionSampleTime = isLive && playing ? Stopwatch.GetTimestamp() : 0;
        _positionCorrection = smooth ? drift : 0;
        if (!sameTrack) _editingPosition = false;
        if (!_editingPosition && !smooth && (!_seekPending || !sameTrack)) Position = reported;
        SyncPositionTimer();
    }

    private void AdvancePosition()
    {
        double elapsed = Math.Min(Stopwatch.GetElapsedTime(_positionSampleTime).TotalSeconds, MaxPositionExtrapolation);
        // Blend out small differences over one second without moving backwards during playback.
        Position = Math.Clamp(_positionSample + elapsed + _positionCorrection * Math.Max(0, 1 - elapsed), 0, Duration);
        if (elapsed >= MaxPositionExtrapolation || Position >= Duration) _positionTimer.Stop();
    }

    private void SyncPositionTimer()
    {
        if (!_disposed && !_editingPosition && !_seekPending && !_lifecycleBusy && _connected && Running &&
            _positionSampleTime != 0 && Position < Duration &&
            Stopwatch.GetElapsedTime(_positionSampleTime).TotalSeconds < MaxPositionExtrapolation)
            _positionTimer.Start();
        else
            _positionTimer.Stop();
    }

    private void ClearPlaybackPosition()
    {
        UpdateCurrentCover(null);
        _positionTimer.Stop();
        _editingPosition = false;
        _currentTrackId = null;
        _position = _positionSample = _positionCorrection = Duration = 0;
        _positionSampleTime = 0;
    }

    private void ResetPlayback()
    {
        _mediaClient.Dispose();
        ShowMediaStatus([], 0);
        foreach (var tab in OpenServers)
            if (tab.Snapshot is { } snapshot)
                tab.Snapshot = snapshot with { Current = null, Status = "None", Position = 0, Repeat = "off", VoiceChannelId = null, Queue = [], Import = null, LastError = null, LastErrorDetail = null,
                    Songs = snapshot.Songs.Select(InterruptDownload).ToArray(), Mix = snapshot.Mix is { } mix ? mix with { ActiveEffects = 0 } : null };
        CurrentTitle = UiText.Get("NothingPlaying"); CurrentDetail = UiText.Get("BotStoppedStatus");
        _playback = "None"; _repeat = "off"; ClearPlaybackPosition();
        Queue = []; SelectedEntry = null; _loadingSelection = false; SetImport(null);
        int? songId = SelectedSong?.Id;
        ApplySongs(Songs.Select(InterruptDownload).ToArray());
        SelectedSong = Songs.FirstOrDefault(song => song.Id == songId);
        SetSoundboardStatus(UiText.Get("BotStoppedNoEffects"));
        Raise(nameof(Queue)); RaisePlayer();
    }

    private static TrackItem InterruptDownload(TrackItem song) => song.DownloadState == TrackDownloadState.Downloading
        ? song with { DownloadState = TrackDownloadState.Pending } : song;

    private void ClearGuild()
    {
        _applying = true; _loadedGuild = 0;
        EffectsVolume = 75; DuckMusic = false;
        Channels = []; Queue = []; History = []; Playlists = []; Songs.Clear();
        SelectedChannel = null; SelectedEntry = null; SelectedSong = null; SelectedHistory = null; SelectedPlaylist = null;
        CurrentTitle = UiText.Get("NothingPlaying"); CurrentDetail = UiText.Get("AddTrackByNameHint");
        ClearPlaybackPosition(); _repeat = "off"; _playback = "None"; SetImport(null);
        foreach (string name in new[] { nameof(Channels), nameof(Queue), nameof(History), nameof(Playlists), nameof(FilteredPlaylists) }) Raise(name);
        _applying = false; RaisePlayer();
    }
    private void RaisePlayer()
    {
        foreach (string name in new[] { nameof(CurrentTitle), nameof(CurrentDetail), nameof(Duration), nameof(Position), nameof(PositionText), nameof(RepeatText), nameof(RepeatIcon), nameof(RepeatEnabled), nameof(PlayIcon), nameof(PlayHint), nameof(IsPreparingPlayback), nameof(CanSeek) }) Raise(name);
    }
    private void SetImport(ImportItem? import)
    {
        _import = import;
        var interval = import is { Finished: false } ? ImportSnapshotInterval : SnapshotInterval;
        if (_timer.Interval != interval) _timer.Interval = interval;
        Raise(nameof(ImportIndeterminate)); Raise(nameof(ImportText));
    }
    private static string Time(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Clamp(double.IsFinite(seconds) ? seconds : 0, 0, 864000));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }
    private void Error(string message, bool critical = false, string? code = null)
    {
        AppendLog(message);
        if (critical) NotifyProblem(ProblemCatalog.Describe(message, code));
    }
    private void NotifyProblem(DesktopProblem problem)
    {
        if (_problems.Any(p => p.Code == problem.Code)) return;
        _problems.Enqueue(problem); if (!_showingProblem) _errorTimer.Start();
    }
    private void ReceiveLog(string text)
    {
        if (_disposed) return;
        AppendLog(text);
        bool fatal = text.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase) || text.Contains("[CRI]", StringComparison.OrdinalIgnoreCase) || text.Contains("[FATAL]", StringComparison.OrdinalIgnoreCase) || text.Contains("crit:", StringComparison.OrdinalIgnoreCase);
        var problem = ProblemCatalog.Describe(text);
        if (problem.Code == "token-invalid" || fatal)
        {
            if (problem.Code == "unexpected" && _fatalReported) return;
            string key = problem.Code;
            if (_reportedLogErrors.Count >= 100) _reportedLogErrors.Clear();
            if (!_reportedLogErrors.Add(key)) return;
            _fatalReported = true;
            NotifyProblem(problem);
        }
    }
    public void ClearLog() { _log.Clear(); LogText = ""; }
    private void AppendLog(string text)
    {
        _log.Enqueue($"[{DateTime.Now:HH:mm:ss}] {text}");
        while (_log.Count > 500) _log.Dequeue();
        LogText = string.Join(Environment.NewLine, _log);
    }
    private void RefreshCommands()
    {
        SyncPositionTimer();
        foreach (string name in new[] { nameof(Running), nameof(CanStart), nameof(CanStop), nameof(CanSelect), nameof(GuildEnabled), nameof(CanSeek), nameof(WelcomeTitle), nameof(WelcomeDetail), nameof(SoundboardEnabled) }) Raise(name);
        Raise(nameof(ConnectionState));
        Raise(nameof(IsPreparingPlayback));
        Raise(nameof(IsWaiting));
        ActionCommand.Refresh();
        SoundboardCommand.Refresh();
    }
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value; Raise(name);
    }
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public void Dispose() { _disposed = true; _mediaTimer.Stop(); _mediaClient.Dispose(); UpdateCurrentCover(null); _timer.Stop(); _errorTimer.Stop(); _positionTimer.Stop(); _session.Cancel(); _session.Dispose(); _client.Dispose(); _audioClient.Dispose(); }
}
