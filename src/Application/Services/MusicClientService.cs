using Application.Interfaces;
using Application.Models;
using Entities.Enums;
using Entities.Models;
using Logging;
using Microsoft.Extensions.DependencyInjection;
using System.Buffers.Binary;

namespace Application.Services;

[LogCategory(LogCategory.Music)]
public sealed class MusicClientService : IAsyncDisposable
{
    private const int BYTES_PER_SECOND = 192000;
    private readonly object _sync = new();
    private readonly TrackService _tracks;
    private readonly IBotConnector _connector;
    private readonly IMusicPreferences _preferences;
    private readonly PcmEffectMixer _effects = new();
    private BotVoiceChannel? _effectChannel;
    public int ActiveEffects => _effects.ActiveCount;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private CancellationTokenSource? _trackCancellation;
    private Task _playbackTask = Task.CompletedTask;
    private readonly HashSet<ulong> _skipVotes = new();
    private GuildMusicSettings _settings;
    private QueueEntry? _current;
    private PlaybackStatus _status;
    private string _repeat = "off";
    private long _position;
    private long? _seek;
    private bool _skipRequested;
    private bool _disposed;
    private bool _stopping;
    private IBotVoiceConnection? _voice;
    private ulong? _voiceChannel;
    private readonly List<Song> _history = new();
    public ulong GuildId { get; }
    public MusicQueue Queue { get; } = new();
    public MusicViewService music_view { get; }
    public QueueEntry? Current { get { lock (_sync) return _current; } }
    public PlaybackStatus Status { get { lock (_sync) return _status; } }
    public string Repeat { get { lock (_sync) return _repeat; } }
    public double PositionSeconds { get { lock (_sync) return (double)_position / BYTES_PER_SECOND; } }
    public ulong? VoiceChannelId { get { lock (_sync) return _voiceChannel; } }
    public string? LastError { get; private set; }
    public string? LastErrorDetail { get; private set; }
    public IReadOnlyList<Song> History { get { lock (_sync) return _history.ToArray(); } }
    public GuildMusicSettings Settings => _preferences.GetSettings(GuildId);

    public MusicClientService(Guild guild, IBotConnector connector, IServiceScopeFactory scopes, TrackService tracks, IMusicPreferences preferences)
    {
        GuildId = guild.DiscordId; _connector = connector; _tracks = tracks; _preferences = preferences;
        _settings = preferences.GetSettings(GuildId);
        music_view = new MusicViewService(this, scopes, connector);
    }
    public bool IsDj(MusicActor actor) => actor.IsManager || _settings.DjRoleId == null || actor.RoleIds?.Contains(_settings.DjRoleId.Value) == true;
    public OperationResult CheckVoice(MusicActor actor)
    {
        if (actor.VoiceChannelId == null) return OperationResult.Fail("Сначала войдите в голосовой канал.");
        if (VoiceChannelId != null && VoiceChannelId != actor.VoiceChannelId) return OperationResult.Fail("Управление доступно из голосового канала бота.");
        return OperationResult.Ok("");
    }
    public OperationResult CheckDj(MusicActor actor)
    {
        var result = CheckVoice(actor);
        return !result.Success ? result : IsDj(actor) ? result : OperationResult.Fail("Для этого действия нужна роль DJ.");
    }
    public async Task<OperationResult> EnqueueAsync(BotVoiceChannel channel, MusicActor actor, string? query = null, IReadOnlyList<Song>? songs = null)
    {
        var result = CheckVoice(actor); if (!result.Success) return result;
        if (channel.GuildId != GuildId || channel.Id != actor.VoiceChannelId) return OperationResult.Fail("Голосовой канал изменился. Повторите действие.");
        if (songs == null)
        {
            int capacity = actor.IsManager ? 1000 : Math.Max(0, _settings.PerUserLimit - Queue.Snapshot().Count(e => e.RequestedBy == actor.UserId));
            if (capacity == 0) return OperationResult.Fail("Ваш лимит очереди исчерпан. Уберите трек или дождитесь воспроизведения.");
            songs = await _tracks.FindAsync(query ?? "", capacity);
        }
        if (songs == null || songs.Count == 0) return OperationResult.Fail("Трек не найден или источник недоступен. Попробуйте прямую ссылку.");
        lock (_sync)
        {
            if (_disposed || _stopping) return OperationResult.Fail("Плеер останавливается. Повторите действие через секунду.");
            result = CheckVoice(actor); if (!result.Success) return result;
            result = Queue.Add(channel, songs, actor, _settings);
            if (!result.Success) return result;
            _tracks.Schedule(songs);
            if (_playbackTask.IsCompleted) _playbackTask = Task.Run(PlaybackLoopAsync);
        }
        await music_view.UpdateMusicViewAsync(); return result;
    }

    public OperationResult PlayEffect(BotVoiceChannel channel, MusicActor actor, SoundEffectClip clip)
    {
        var result = CheckDj(actor); if (!result.Success) return result;
        if (channel.GuildId != GuildId || channel.Id != actor.VoiceChannelId) return OperationResult.Fail("Голосовой канал изменился. Повторите действие.");
        lock (_sync)
        {
            if (_disposed || _stopping) return OperationResult.Fail("Плеер останавливается. Повторите действие через секунду.");
            if (!_effects.Play(clip)) return OperationResult.Fail("Одновременно можно запустить до 8 эффектов. Дождитесь завершения или остановите эффекты.");
            _effectChannel = channel;
            if (_playbackTask.IsCompleted) _playbackTask = Task.Run(PlaybackLoopAsync);
        }
        return OperationResult.Ok("Эффект: " + clip.Info.Name, "EffectPlaying", clip.Info.Name);
    }
    public OperationResult StopEffects(MusicActor actor)
    {
        var result = CheckDj(actor); if (!result.Success) return result;
        _effects.Stop();
        return OperationResult.Ok("Эффекты остановлены. Музыка продолжает играть.", "EffectsStopped");
    }
    private async Task EnsureVoiceAsync(BotVoiceChannel channel, CancellationToken token)
    {
        if (_voice != null && _voiceChannel == channel.Id) return;
        if (_voice != null) { await _voice.DisposeAsync(); _voice = null; }
        _voice = await _connector.ConnectVoiceAsync(channel, token);
        lock (_sync) _voiceChannel = channel.Id;
    }
    private async Task PlayEffectsOnlyAsync(CancellationToken token)
    {
        BotVoiceChannel channel = _effectChannel ?? throw new SoundboardException("Выберите голосовой канал.");
        await EnsureVoiceAsync(channel, token);
        await using Stream output = _voice!.CreatePcmStream();
        byte[] buffer = new byte[PcmEffectMixer.FrameBytes];
        while (!token.IsCancellationRequested && _effects.ActiveCount > 0 && Queue.Snapshot().Count == 0)
        {
            GuildMusicSettings settings = _settings;
            _effects.Mix(buffer, 0, settings.Volume, settings.EffectsVolume, settings.DuckMusic);
            await output.WriteAsync(buffer, token);
        }
        if (_effects.ActiveCount == 0) await output.FlushAsync(token);
    }
    private async Task PlaybackLoopAsync()
    {
        ulong previousUser = 0;
        QueueEntry? repeatTrack = null;
        while (!_lifetime.IsCancellationRequested)
        {
            QueueEntry? entry; CancellationToken token;
            lock (_sync)
            {
                if (_stopping) { _playbackTask = Task.CompletedTask; return; }
                entry = repeatTrack ?? Queue.Take(_settings.FairQueue, previousUser); repeatTrack = null;
                if (entry == null && _effects.ActiveCount == 0) { _playbackTask = Task.CompletedTask; return; }
                _current = entry; _position = 0; _seek = null; _skipRequested = false; _skipVotes.Clear();
                _trackCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); token = _trackCancellation.Token;
            }
            bool completed = false;
            try
            {
                if (entry == null)
                {
                    await PlayEffectsOnlyAsync(token);
                    continue;
                }
                await using Stream pcm = await _tracks.OpenAsync(entry.Song, token);
                token.ThrowIfCancellationRequested();
                await EnsureVoiceAsync(entry.Channel, token);
                lock (_sync) { _status = PlaybackStatus.Playing; _history.RemoveAll(s => s.Id == entry.Song.Id); _history.Insert(0, entry.Song); if (_history.Count > 50) _history.RemoveAt(50); }
                LastError = null; LastErrorDetail = null; await music_view.UpdateMusicViewAsync();
                await using Stream output = _voice!.CreatePcmStream();
                byte[] buffer = new byte[3840];
                while (!token.IsCancellationRequested)
                {
                    long? seek; PlaybackStatus status;
                    lock (_sync) { seek = _seek; _seek = null; status = _status; }
                    if (seek.HasValue && pcm.CanSeek) { pcm.Position = Math.Clamp(seek.Value, 0, Math.Max(0, pcm.Length - 4)) / 4 * 4; lock (_sync) _position = pcm.Position; }
                    int count = status == PlaybackStatus.Paused ? 0 : await pcm.ReadAtLeastAsync(buffer.AsMemory(), buffer.Length, throwOnEndOfStream: false, token);
                    if (count == 0 && status != PlaybackStatus.Paused) { completed = true; break; }
                    GuildMusicSettings settings = _settings;
                    _effects.Mix(buffer, count, settings.Volume, settings.EffectsVolume, settings.DuckMusic);
                    await output.WriteAsync(buffer, token);
                    lock (_sync) _position = pcm.Position;
                }
                if (completed && _effects.ActiveCount == 0) await output.FlushAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _effects.Stop();
                LastError = "Не удалось воспроизвести трек. Проверьте источник и голосовое соединение в диагностике.";
                LastErrorDetail = ex.GetType().Name + ": " + ex.Message;
                await Logger.AddLog("Playback failed", Microsoft.Extensions.Logging.LogLevel.Error, guildId: GuildId, exception: ex);
                if (_voice != null) { try { await _voice.DisposeAsync(); } catch (Exception) { } _voice = null; lock (_sync) _voiceChannel = null; }
            }
            finally
            {
                lock (_sync)
                {
                    if (completed && entry != null && !_skipRequested && !_stopping)
                    {
                        if (_repeat == "track") repeatTrack = entry;
                        if (_repeat == "queue") Queue.Append(entry);
                    }
                    _current = null; _status = PlaybackStatus.None; _trackCancellation?.Dispose(); _trackCancellation = null;
                }
            }
            if (completed && entry != null)
            {
                try { await _tracks.RecordPlayAsync(entry.Song); }
                catch (Exception ex) { await Logger.AddLog("Could not update play count", exception: ex); }
            }
            if (entry != null) previousUser = entry.RequestedBy;
            await music_view.UpdateMusicViewAsync();
        }
    }

    public static void ApplyVolume(byte[] buffer, int count, int volume)
    {
        for (int q = 0; q + 1 < count; q += 2)
        {
            short sample = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(q, 2));
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(q, 2), (short)(sample * Math.Clamp(volume, 0, 100) / 100));
        }
    }
    public async Task<OperationResult> PauseAsync(MusicActor actor, bool? paused = null)
    {
        var result = CheckDj(actor); if (!result.Success) return result;
        lock (_sync)
        {
            if (_current == null || _status == PlaybackStatus.None) return OperationResult.Fail("Дождитесь начала воспроизведения.");
            _status = (paused ?? _status != PlaybackStatus.Paused) ? PlaybackStatus.Paused : PlaybackStatus.Playing;
        }
        await music_view.UpdateMusicViewAsync();
        return Status == PlaybackStatus.Paused
            ? OperationResult.Ok("Пауза.", "PlaybackPaused")
            : OperationResult.Ok("Воспроизведение продолжено.", "PlaybackResumed");
    }
    public async Task<OperationResult> SkipAsync(MusicActor actor, IReadOnlyCollection<ulong> listeners)
    {
        var result = CheckVoice(actor); if (!result.Success) return result;
        lock (_sync)
        {
            if (_current == null) return OperationResult.Fail("Сейчас ничего не играет.");
            bool privileged = actor.IsManager || _settings.DjRoleId != null && IsDj(actor);
            if (_settings.VoteSkip && !privileged)
            {
                if (!listeners.Contains(actor.UserId)) return OperationResult.Fail("Голосовать могут слушатели канала.");
                _skipVotes.IntersectWith(listeners); _skipVotes.Add(actor.UserId);
                int needed = Math.Max(1, listeners.Distinct().Count() / 2 + 1);
                if (_skipVotes.Count < needed) return OperationResult.Ok($"Голос за пропуск: {_skipVotes.Count}/{needed}.");
            }
            else if (!IsDj(actor)) return OperationResult.Fail("Для пропуска нужна роль DJ.");
            _skipRequested = true; _trackCancellation?.Cancel();
        }
        await music_view.UpdateMusicViewAsync(); return OperationResult.Ok("Трек пропущен.", "TrackSkipped");
    }
    public async Task<OperationResult> SeekAsync(MusicActor actor, double seconds)
    {
        var result = CheckDj(actor); if (!result.Success) return result;
        if (!double.IsFinite(seconds) || seconds < 0) return OperationResult.Fail("Укажите время от начала трека, например 2:30.");
        lock (_sync)
        {
            if (_current == null || _status == PlaybackStatus.None) return OperationResult.Fail("Дождитесь начала воспроизведения.");
            if (_current.Song.Duration > 0 && seconds >= _current.Song.Duration) return OperationResult.Fail("Позиция находится за концом трека.");
            _seek = (long)(Math.Min(seconds, 86400) * BYTES_PER_SECOND);
        }
        await music_view.UpdateMusicViewAsync(); return OperationResult.Ok("Позиция воспроизведения изменена.", "PlaybackPositionChanged");
    }
    public async Task<OperationResult> QueueActionAsync(MusicActor actor, string action, Guid? id = null)
    {
        var result = CheckVoice(actor); if (!result.Success) return result;
        if (action == "undo") result = Queue.Undo(actor.UserId);
        else if (action == "shuffle") { result = CheckDj(actor); if (result.Success) Queue.Shuffle(); }
        else result = id.HasValue ? Queue.Edit(id.Value, action, actor, IsDj(actor)) : OperationResult.Fail("Сначала выберите трек.");
        await music_view.UpdateMusicViewAsync(); return result.Success ? OperationResult.Ok("Очередь обновлена.", "QueueUpdated") : result;
    }
    public async Task<OperationResult> StopAsync(MusicActor actor, bool disconnect = false)
    {
        var result = CheckDj(actor); if (!result.Success) return result;
        Task playback;
        lock (_sync) { _stopping = true; Queue.Clear(); _effects.Stop(); _skipRequested = true; _trackCancellation?.Cancel(); playback = _playbackTask; }
        try
        {
            await playback;
            if (disconnect && _voice != null) { await _voice.DisposeAsync(); _voice = null; lock (_sync) _voiceChannel = null; }
        }
        finally { lock (_sync) _stopping = false; }
        await music_view.UpdateMusicViewAsync();
        return disconnect
            ? OperationResult.Ok("Бот отключён.", "BotDisconnected")
            : OperationResult.Ok("Воспроизведение остановлено, очередь очищена.", "PlaybackStoppedQueueCleared");
    }
    public async Task<OperationResult> SetRepeatAsync(MusicActor actor, string mode)
    {
        var result = CheckDj(actor); if (!result.Success) return result;
        if (mode is not ("off" or "track" or "queue")) return OperationResult.Fail("Неизвестный режим повтора.");
        lock (_sync) _repeat = mode;
        await music_view.UpdateMusicViewAsync(); return OperationResult.Ok("Режим повтора изменён.", "RepeatModeChanged");
    }
    public async Task<OperationResult> ConfigureAsync(MusicActor actor, Action<GuildMusicSettings> change, bool managerOnly = true)
    {
        if (managerOnly && !actor.IsManager) return OperationResult.Fail("Настройки сервера доступны администратору.");
        if (!managerOnly) { var result = CheckDj(actor); if (!result.Success) return result; }
        await _settingsGate.WaitAsync();
        try { var next = Settings; change(next); await _preferences.SaveSettingsAsync(GuildId, next); _settings = next; }
        finally { _settingsGate.Release(); }
        await music_view.UpdateMusicViewAsync(); return OperationResult.Ok("Настройки сохранены.", "SettingsSaved");
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true; _effects.Stop(); _lifetime.Cancel(); await _playbackTask;
        if (_voice != null) await _voice.DisposeAsync();
        await music_view.DisposeAsync(); _lifetime.Dispose(); _settingsGate.Dispose();
    }
}
