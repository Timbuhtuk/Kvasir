using Kvasir.Media;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Application.Interfaces;
using Application.Models;
using Application.Services;
using Connectors.Media;
using Entities.Models;
using Kvasir.Control;
using Logging;
using Microsoft.Extensions.Hosting;
using NetCord;
using NetCord.Gateway;

namespace CS_Discord_Bot.DesktopControl;

// The local Windows operator is a bot administrator. Discord callers still use Discord policies.
public sealed class DesktopControlService(GatewayClient gateway, IBotConnector connector,
    GuildService guilds, IMusicLibrary library, PlaylistImportService imports, IMusicDiagnostics diagnostics, IMediaCache cache,
    ISoundboardLibrary soundboard, IVideoFinderService finder, MediaActivity mediaActivity, TrackService tracks) : BackgroundService
{
    private CheckItem[] _checks = [];
    private CacheItem? _cache;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? pipeName = Environment.GetEnvironmentVariable("KVASIR_CONTROL_PIPE");
        string? session = Environment.GetEnvironmentVariable("KVASIR_CONTROL_SESSION");
        Environment.SetEnvironmentVariable("KVASIR_CONTROL_SESSION", null);
        if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(session)) return;
        if (!pipeName.StartsWith("Kvasir-", StringComparison.Ordinal) || pipeName.Length > 100) return;

        await Task.WhenAll(RunPipeAsync(pipeName, session, "", stoppingToken),
            RunPipeAsync(pipeName + "-audio", session, "audio", stoppingToken),
            RunPipeAsync(pipeName + "-media", session, "media", stoppingToken));
    }

    private async Task RunPipeAsync(string pipeName, string session, string endpoint, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using NamedPipeServerStream pipe = new(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stoppingToken);
                while (pipe.IsConnected && !stoppingToken.IsCancellationRequested)
                {
                    ControlRequest request = await ControlProtocol.ReadAsync<ControlRequest>(pipe, stoppingToken);
                    if (request.Session == null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.Session), Encoding.UTF8.GetBytes(session)))
                        break;
                    ControlResponse response;
                    try
                    {
                        response = endpoint switch
                        {
                            "media" when request.Action == "media-status" => MediaSnapshot(),
                            "media" => new(false, "Неизвестная команда статуса медиа."),
                            "audio" => await HandleAudioAsync(request),
                            _ => await HandleAsync(request, stoppingToken)
                        };
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        await Logger.AddLog("Desktop control operation failed: " + ex.Message, Microsoft.Extensions.Logging.LogLevel.Error, exception: ex);
                        string? code = ex is SoundboardException ? "soundboard" : ex is MediaSourceException source ? source.Code : ex is UnauthorizedAccessException ? "storage" :
                            ex.GetType().Name.Contains("Sqlite", StringComparison.Ordinal) ? "database" :
                            ex is System.Net.Http.HttpRequestException ? "connection" : null;
                        response = new(false, ex is ArgumentException ? ex.Message : "Не удалось выполнить действие.", ErrorCode: code, Detail: ex.GetType().Name + ": " + ex.Message);
                    }
                    await ControlProtocol.WriteAsync(pipe, response, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                await Logger.AddLog("Desktop control connection closed", Microsoft.Extensions.Logging.LogLevel.Debug);
                await Task.Delay(250, stoppingToken);
            }
        }
    }

    private ControlResponse MediaSnapshot() => new(true, "")
    {
        PendingTracks = tracks.QueuedCount,
        MediaWork = mediaActivity.Snapshot().Select(s => new MediaWorkItem(s.Subject, s.Stage.ToString(), s.Started,
            s.Updated, s.Provider, s.Bytes, s.Percent, s.Detail, s.Processed, s.Matches)).ToArray()
    };

    private async Task<ControlResponse> HandleAudioAsync(ControlRequest request)
    {
        MusicClientService? music = guilds.GetMusicClient(request.GuildId);
        if (connector.CurrentUserId == 0 || music == null) return new(false, "Дождитесь подключения и откройте сервер.", ErrorCode: "input");
        ulong? channelId = music.VoiceChannelId ?? request.VoiceChannelId;
        BotVoiceChannel? channel = gateway.Cache.Guilds.TryGetValue(request.GuildId, out var guild) && channelId.HasValue &&
            guild.Channels.TryGetValue(channelId.Value, out var value) && value is VoiceGuildChannel voice ? new(voice.Id, guild.Id, voice.Name) : null;
        var actor = new MusicActor(connector.CurrentUserId, channel?.Id, IsManager: true);
        OperationResult result;
        switch (request.Action)
        {
            case "sound-play":
                if (channel == null) return new(false, "Выберите голосовой канал.", ErrorCode: "voice-channel");
                result = music.PlayEffect(channel, actor, soundboard.GetReady(request.EffectId ?? throw new SoundboardException("Выберите эффект."))); break;
            case "sound-stop": result = music.StopEffects(actor); break;
            case "sound-volume":
                if (!double.IsFinite(request.Number) || request.Number is < 0 or > 100) throw new SoundboardException("Громкость эффектов: от 0 до 100%.");
                result = await music.ConfigureAsync(actor, s => s.EffectsVolume = (int)request.Number); break;
            case "sound-music-volume":
                if (!double.IsFinite(request.Number) || request.Number is < 0 or > 100) throw new SoundboardException("Громкость музыки: от 0 до 100%.");
                result = await music.ConfigureAsync(actor, s => s.Volume = (int)request.Number); break;
            case "sound-duck": result = await music.ConfigureAsync(actor, s => s.DuckMusic = request.Number == 1); break;
            default: return new(false, "Неизвестная команда саундборда.", ErrorCode: "input");
        }
        return new(result.Success, result.Message, ErrorCode: result.Success ? null : "soundboard", Mix: ToMix(music))
        {
            MessageKey = result.MessageKey,
            MessageArguments = result.MessageArguments
        };
    }

    private async Task<ControlResponse> HandleAsync(ControlRequest request, CancellationToken cancellationToken)
    {
        if (connector.CurrentUserId == 0 && request.Action != "snapshot")
            return new(false, "Бот ещё подключается к Discord.", await SnapshotAsync(null, null, request));
        if (request.Action == "diagnostics")
        {
            _checks = (await diagnostics.CheckAsync()).Select(c => new CheckItem(c.Name, c.Healthy, c.Detail)).ToArray();
            _cache = ToCache(await cache.InspectAsync());
        }
        if (request.Action is "cache-clean" or "cache-repair")
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            _cache = ToCache(await cache.MaintainAsync(request.Action == "cache-repair", timeout.Token));
        }

        BotGuild? guild = connector.Guilds.FirstOrDefault(g => g.Id == request.GuildId);
        if (guild == null)
        {
            if (request.Action is not ("snapshot" or "diagnostics" or "cache-clean" or "cache-repair"))
                return new(false, "Выберите доступный сервер.", await SnapshotAsync(null, null, request));
            return new(true, CacheMessage(request.Action), await SnapshotAsync(null, null, request));
        }

        MusicClientService music = await guilds.GetOrCreateAsync(guild);
        ulong? channelId = music.VoiceChannelId ?? request.VoiceChannelId;
        BotVoiceChannel? channel = gateway.Cache.Guilds.TryGetValue(guild.Id, out var cached) && channelId.HasValue &&
            cached.Channels.TryGetValue(channelId.Value, out var candidate) && candidate is VoiceGuildChannel voice
            ? new(voice.Id, guild.Id, voice.Name) : null;
        MusicActor actor = new(connector.CurrentUserId, channel?.Id, IsManager: true);
        OperationResult result = OperationResult.Ok("");
        switch (request.Action)
        {
            case "sound-import":
                var effect = await soundboard.ImportAsync(Required(request.Value), cancellationToken);
                result = OperationResult.Ok("Эффект готов: " + effect.Name, "EffectReady", effect.Name); break;
            case "sound-remove":
                await soundboard.RemoveAsync(request.EffectId ?? throw new SoundboardException("Выберите эффект."), cancellationToken);
                result = OperationResult.Ok("Эффект удалён из саундборда. Исходный файл сохранён.", "EffectRemoved"); break;
            case "snapshot": case "diagnostics": break;
            case "cache-clean": case "cache-repair":
                result = OperationResult.Ok(CacheMessage(request.Action)); break;
            case "enqueue":
                result = channel == null ? OperationResult.Fail("Выберите голосовой канал.") :
                    await music.EnqueueAsync(channel, actor, Required(request.Value)); break;
            case "pause": result = await music.PauseAsync(actor, true); break;
            case "resume": result = await music.PauseAsync(actor, false); break;
            case "skip": result = await music.SkipAsync(actor, []); break;
            case "stop": result = await music.StopAsync(actor); break;
            case "disconnect": result = await music.StopAsync(actor, true); break;
            case "repeat": result = await music.SetRepeatAsync(actor, request.Value ?? "off"); break;
            case "seek": result = await music.SeekAsync(actor, request.Number); break;
            case "volume":
                if (!double.IsFinite(request.Number) || request.Number is < 0 or > 100) throw new ArgumentException("Громкость: 0–100.");
                result = await music.ConfigureAsync(actor, s => s.Volume = (int)request.Number); break;
            case "queue-remove": case "queue-up": case "queue-down": case "queue-next": case "queue-shuffle": case "queue-undo":
                result = await music.QueueActionAsync(actor, request.Action[6..], request.EntryId); break;
            case "settings":
                CompanySettings settings = request.Settings ?? throw new ArgumentException("Настройки отсутствуют.");
                if (settings.PerUserLimit is < 1 or > 1000 || settings.Volume is < 0 or > 100) throw new ArgumentException("Лимит: 1–1000; громкость: 0–100.");
                if (settings.DjRoleId.HasValue && cached?.Roles.ContainsKey(settings.DjRoleId.Value) != true) throw new ArgumentException("Роль DJ не найдена на сервере.");
                result = await music.ConfigureAsync(actor, s =>
                {
                    s.DjRoleId = settings.DjRoleId; s.VoteSkip = settings.VoteSkip;
                    s.FairQueue = settings.FairQueue; s.PerUserLimit = settings.PerUserLimit; s.Volume = settings.Volume;
                }); break;
            case "playlist-create": case "playlist-rename":
                bool create = request.Action == "playlist-create";
                string? link = create && string.IsNullOrWhiteSpace(request.ImportQuery)
                    ? PlaylistInput.GetLink(Required(request.Value)) : null;
                string source = link ?? request.ImportQuery?.Trim() ?? "";
                source = YtDlpMetadata.GetPlaylistUrl(source) ?? source;
                bool import = source.Length > 0;
                // The desktop command pipe handles playlist mutations serially for the bot's actor.
                if (import && imports.IsImportRunning(guild.Id, actor.UserId))
                {
                    result = OperationResult.Fail("Предыдущий импорт ещё выполняется.");
                    break;
                }
                if (create)
                {
                    string name = Required(request.Value);
                    if (link != null)
                    {
                        name = (await finder.GetPlaylistName(source))?.Trim() ?? "";
                        if (name.Length == 0)
                            return new(false, "Укажите ссылку на плейлист YouTube, публичный плейлист или альбом Spotify.", ErrorCode: "input")
                                { MessageKey = "PlaylistSourceRequired" };
                        if (name.Length > 128) name = name[..128].TrimEnd();
                    }
                    Playlist created = await library.CreateAsync(guild.Id, actor, name, request.Number == 1);
                    request = request with { PlaylistId = created.Id };
                    result = OperationResult.Ok("Плейлист создан.", "PlaylistCreated");
                }
                else result = await library.EditAsync(guild.Id, actor, RequiredPlaylist(request), "rename", request.Value);
                if (import && result.Success)
                {
                    result = imports.StartImport(guild.Id, actor, RequiredPlaylist(request), source);
                    if (result.Success)
                        result = create
                            ? OperationResult.Ok("Плейлист создан, импорт запущен.", "PlaylistCreatedImportStarted")
                            : OperationResult.Ok("Плейлист обновлён, импорт запущен.", "PlaylistUpdatedImportStarted");
                }
                break;
            case "playlist-share": case "playlist-delete": case "playlist-remove": case "playlist-up": case "playlist-down":
                result = await library.EditAsync(guild.Id, actor, RequiredPlaylist(request), request.Action[9..], request.Value, request.SongId); break;
            case "playlist-move-track":
                result = request is { PlaylistId: int movePlaylist, SongId: int moved, AnchorSongId: int anchor }
                    ? await library.MoveTrackAsync(guild.Id, actor, movePlaylist, moved, anchor, request.InsertAfter)
                    : OperationResult.Fail("Укажите трек и его новую позицию.") with { MessageKey = "PlaylistDropTrackMissing" };
                break;
            case "playlist-copy-track":
                result = request is { PlaylistId: int sourcePlaylist, DestinationPlaylistId: int destinationPlaylist, SongId: int copied }
                    ? await library.CopyTrackAsync(guild.Id, actor, sourcePlaylist, destinationPlaylist, copied)
                    : OperationResult.Fail("Укажите исходный и целевой плейлисты и трек.") with { MessageKey = "PlaylistDropSourceMissing" };
                break;
            case "playlist-add-current":
                QueueEntry? current = music.Current;
                result = current == null ? OperationResult.Fail("Сейчас ничего не играет.") :
                    await library.EditAsync(guild.Id, actor, RequiredPlaylist(request), "add", songId: current.Song.Id); break;
            case "playlist-import":
                string query = Required(request.Value);
                result = imports.StartImport(guild.Id, actor, RequiredPlaylist(request), YtDlpMetadata.GetPlaylistUrl(query) ?? query);
                if (result.Success) result = OperationResult.Ok("Импорт запущен. Прогресс виден в библиотеке.", "PlaylistImportStarted");
                break;
            case "playlist-play": case "playlist-song-play":
                Playlist? playlist = await library.GetAsync(guild.Id, actor, RequiredPlaylist(request));
                IReadOnlyList<Song> songs = playlist?.Songs.Where(s => request.Action == "playlist-play" || s.Id == request.SongId).ToArray() ?? [];
                result = channel == null ? OperationResult.Fail("Выберите голосовой канал.") : await music.EnqueueAsync(channel, actor, songs: songs); break;
            case "history-play":
                Song? history = music.History.FirstOrDefault(s => s.Id == request.SongId);
                result = history == null || channel == null ? OperationResult.Fail("Выберите трек истории и голосовой канал.") :
                    await music.EnqueueAsync(channel, actor, songs: new[] { history }); break;
            default: result = OperationResult.Fail("Неизвестная команда пульта."); break;
        }
        string? errorCode = !result.Success && request.Action is "playlist-move-track" or "playlist-copy-track" ? "input" : null;
        return new(result.Success, result.Message, await SnapshotAsync(guild, music, request), ErrorCode: errorCode)
        {
            MessageKey = result.MessageKey,
            MessageArguments = result.MessageArguments
        };
    }

    private async Task<ControlState> SnapshotAsync(BotGuild? guild, MusicClientService? music, ControlRequest request)
    {
        MusicActor actor = new(connector.CurrentUserId, music?.VoiceChannelId, IsManager: true);
        Playlist? playlist = guild != null && request.PlaylistId.HasValue ? await library.GetAsync(guild.Id, actor, request.PlaylistId.Value) : null;
        ImportProgress? import = guild == null ? null : imports.GetImport(guild.Id, actor.UserId);
        GuildMusicSettings? settings = music?.Settings;
        var playlists = guild == null ? [] : (await library.ListAsync(guild.Id, actor))
            .Select(p => new PlaylistItem(p.Id, p.Name, p.AuthorId, p.IsPublic, p.Songs.Count)).ToArray();
        int? preparingSongId = cache.PreparingSongId;
        return new ControlState
        {
            Ready = connector.CurrentUserId != 0,
            Effects = soundboard.List().Select(e => new SoundEffectItem(e.Id, e.Name, e.Duration, e.Ready, e.Problem)).ToArray(),
            Mix = music == null ? null : ToMix(music),
            Guilds = connector.Guilds.Select(g => new GuildItem(g.Id, g.Name)).OrderBy(g => g.Name).ToArray(),
            GuildId = guild?.Id ?? 0,
            Channels = guild != null && gateway.Cache.Guilds.TryGetValue(guild.Id, out var cached)
                ? cached.Channels.Values.OfType<VoiceGuildChannel>().Select(c => new ChannelItem(c.Id, c.Name)).OrderBy(c => c.Name).ToArray() : [],
            VoiceChannelId = music?.VoiceChannelId,
            Current = music?.Current is { } current ? ToTrack(current.Song) : null,
            Status = music?.Status.ToString() ?? "None", Position = music?.PositionSeconds ?? 0,
            Repeat = music?.Repeat ?? "off", LastError = music?.LastError, LastErrorDetail = music?.LastErrorDetail,
            Queue = music?.Queue.Snapshot().Select(e => new QueueItem(e.Id, ToTrack(e.Song), e.RequestedBy)).ToArray() ?? [],
            History = music?.History.Select(ToTrack).ToArray() ?? [],
            Playlists = playlists,
            PlaylistId = playlist?.Id, Songs = playlist?.Songs.Select(song => ToTrack(song) with
            {
                DownloadState = song.Id == preparingSongId ? TrackDownloadState.Downloading :
                    song.IsDownloaded ? TrackDownloadState.Downloaded : TrackDownloadState.Pending
            }).ToArray() ?? [],
            Settings = settings == null ? null : new(settings.DjRoleId, settings.VoteSkip, settings.FairQueue, settings.PerUserLimit, settings.Volume),
            Import = import == null ? null : new(import.Completed, import.Total, import.Failures.ToArray(), import.Finished, import.ErrorCode, import.Detail)
            { PlaylistId = import.PlaylistId },
            Checks = music == null ? _checks : _checks.Append(new CheckItem("Голосовой канал", music.VoiceChannelId.HasValue,
                music.VoiceChannelId is { } voice ? $"Канал {voice}; состояние {music.Status}" : "Бот не подключён к голосовому каналу.")).ToArray(), Cache = _cache,
        };
    }

    private static string Required(string? value) => !string.IsNullOrWhiteSpace(value) ? value.Trim() : throw new ArgumentException("Заполните поле запроса или названия.");
    private static AudioMixItem ToMix(MusicClientService music)
    {
        var settings = music.Settings;
        return new(settings.Volume, settings.EffectsVolume, settings.DuckMusic, music.ActiveEffects);
    }
    private string CacheMessage(string action) => action is "cache-clean" or "cache-repair" ? $"Кэш: удалено {_cache?.Removed}, восстановлено {_cache?.Repaired}, ошибок {_cache?.FailedRepairs}." : "";
    private static int RequiredPlaylist(ControlRequest request) => request.PlaylistId ?? throw new ArgumentException("Выберите плейлист.");
    private static TrackItem ToTrack(Song song) => new(song.Id, song.Name, song.AuthorName, song.Duration ?? 0, song.Link);
    private static CacheItem ToCache(CacheReport report) => new(report.Bytes, report.LimitBytes, report.Files, report.InvalidFiles, report.Removed, report.Repaired, report.FailedRepairs);
}
