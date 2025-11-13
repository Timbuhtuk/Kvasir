using Application.Interfaces;
using Discord;
using Discord.Audio;
using Discord.Commands;
using Discord.WebSocket;
using Entities.Enums;
using Entities.Models;
using Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Diagnostics;


namespace Application.Services;

[LogCategory(LogCategory.Music)]
public class MusicClientService : IAsyncDisposable
{
    #region Constants
    private const int MAX_PLAYLIST_SIZE = 23;
    private const int HISTORY_LIMIT = 10;
    private const int BUFFER_SIZE = 81920;
    private const int MAIN_LOOP_INTERVAL_MS = 899000;
    private const int CONNECTION_WAIT_MS = 1000;
    private const int PAUSE_CHECK_INTERVAL_MS = 100;
    #endregion

    #region fields

    private Guild guild;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly DiscordSocketClient _discordClient;
    private readonly IConfiguration _config;
    private readonly VideoFinderService _videoFinder;
    private readonly IAudioDownloaderService _audioDownloader;
    private readonly IGuildRepository _guildRepository;
    private readonly ISongRepository _songRepository;
    private readonly IPlaylistRepository _playlistRepository;
    public Playlist playback_history { get; protected set; } = new Playlist() { Name = "History", Id = -1, AuthorId = 0 };
    public Playlist popular_songs { get; protected set; } = new Playlist() { Name = "Popular", Id = -2, AuthorId = 0 };
    public List<Playlist>? saved_music { get; protected set; }
    public KeyValuePair<IVoiceChannel, Song>? current_song { get; protected set; }
    public IVoiceChannel? current_voice_channel { get; protected set; }

    public bool is_playing { get; protected set; }
    public bool is_paused { get; protected set; }
    public bool on_repeat { get; protected set; }
    private CancellationTokenSource SkipTokenSource { get; set; } = new CancellationTokenSource();

    public ConcurrentQueue<KeyValuePair<IVoiceChannel, Song>> music_queue { get; protected set; }

    public IAudioClient? audio_client { get; protected set; }

    protected Process? ffmpeg;

    public MusicViewService? music_view { get; protected set; }

    public Timer MainLoop { get; protected set; }

    public event Func<Task>? ViewUpdateRequested;
    #endregion

    public MusicClientService(
        Guild guild,
        IServiceScopeFactory serviceScopeFactory,
        DiscordSocketClient discordClient,
        IConfigurationRoot config,
        VideoFinderService videoFinder,
        IAudioDownloaderService audioDownloader,
        IServiceProvider serviceProvider,
        IGuildRepository guildRepository,
        ISongRepository songRepository,
        IPlaylistRepository playlistRepository)
    {
        music_queue = new ConcurrentQueue<KeyValuePair<IVoiceChannel, Song>>();
        saved_music = new List<Playlist>() { playback_history };
        _audioDownloader = audioDownloader;

        // Создаем MusicViewService через scope (scoped сервис)
        // Репозитории будут автоматически инжектированы из scope
        music_view = ActivatorUtilities.CreateInstance<MusicViewService>(
            serviceProvider,
            this,
            serviceScopeFactory,
            guild.DiscordId,
            discordClient);

        // Подписываем метод обновления вьюшки на событие
        ViewUpdateRequested += music_view.UpdateMusicViewAsync;

        is_playing = false;
        is_paused = false;
        on_repeat = false;

        _serviceScopeFactory = serviceScopeFactory;
        _discordClient = discordClient;
        _config = config;
        _videoFinder = videoFinder;
        _guildRepository = guildRepository;
        _songRepository = songRepository;
        _playlistRepository = playlistRepository;

        this.guild = _guildRepository.GetByDiscordIdAsync(guild.DiscordId).Result ?? guild;

        // Устанавливаем контекст гильдии для всех логов этого MusicClientService
        LogContext.SetGuild(this.guild.DiscordId, this.guild.Name);

        MainLoop = new System.Threading.Timer(AfterConstructCallback, null, 0, MAIN_LOOP_INTERVAL_MS);
    }

    private void AfterConstructCallback(object? state)
    {
        _ = AfterConstructAsync();
    }

    protected async Task AfterConstructAsync()
    {
        try
        {
            await UpdateLikedMusicAsync();
            await UpdatePopularSongsAsync();
            await music_view!.RerenderMusicViewAsync();
        }
        catch (Exception ex)
        {
            await Logger.AddLog($"Error in AfterConstructAsync: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error);
        }
    }

    public async Task ClearAsync(ICommandContext context)
    {
        music_queue.Clear();
        await LeaveAsync(context);
        is_paused = false;
        is_playing = false;
        current_song = null;
        if (ffmpeg != null)
        {
            ffmpeg.Kill();
        }
    }
    public async Task SkipAsync(ICommandContext? context = null)
    {
        await Logger.AddLog("SkipAsync called ");
        if (on_repeat)
        {
            KeyValuePair<IVoiceChannel, Song> song;
            music_queue.TryDequeue(out song);
        }
        if (is_playing)
        {
            SkipTokenSource.Cancel();
            SkipTokenSource = new CancellationTokenSource();
        }
        is_paused = false;
        is_playing = false;
        current_song = null;
    }
    public async Task LeaveAsync(ICommandContext? context = null)
    {
        if (audio_client != null)
        {
            await audio_client.StopAsync();
            audio_client = null;
        }
    }
    public async Task JoinAsync(IVoiceChannel channel)
    {
        // guildId и guildName автоматически берутся из LogContext
        await Logger.AddLog($"JoinAsync to voice channel {channel.Name} called ");

        if (audio_client != null && audio_client.ConnectionState == ConnectionState.Connected && current_voice_channel != channel)
        {
            await audio_client.StopAsync();
            audio_client = await channel.ConnectAsync();
            current_voice_channel = channel;
        }
        else if (audio_client != null && audio_client.ConnectionState == ConnectionState.Connecting && current_voice_channel != channel)
        {
            await Task.Delay(CONNECTION_WAIT_MS);
            await audio_client.StopAsync();
            audio_client = await channel.ConnectAsync();
            current_voice_channel = channel;
        }
        else if (audio_client != null && audio_client.ConnectionState == ConnectionState.Disconnecting)
        {
            await Task.Delay(CONNECTION_WAIT_MS);
            audio_client = await channel.ConnectAsync();
            current_voice_channel = channel;
        }
        else
        {
            audio_client = await channel.ConnectAsync();
            current_voice_channel = channel;
        }

    }

    public async Task TogglePauseAsync(ICommandContext? context = null)
    {

        if (is_playing && !is_paused)
        {
            is_paused = true;
            is_playing = false;

            await Logger.AddLog("Paused");
        }
        else if (is_paused)
        {
            is_paused = false;
            is_playing = true;

            await Logger.AddLog("Resumed");
        }
    }
    public async Task PlayMusicAsync(CancellationToken cancellationToken)
    {
        if (!is_playing && !is_paused && music_queue.Count > 0)
        {
            KeyValuePair<IVoiceChannel, Song> channelSong = music_queue.First();
            IVoiceChannel voiceChannel = channelSong.Key;
            Song song = channelSong.Value;

            try
            {
                await JoinAsync(voiceChannel);
            }
            catch (Exception e)
            {
                // guildId и guildName автоматически берутся из LogContext
                await Logger.AddLog(e.Message, Microsoft.Extensions.Logging.LogLevel.Error, exception: e);
                return;
            }

            // Проверяем наличие PCM файла
            string? pcmFilePath = song.FilePath;
            if (string.IsNullOrEmpty(pcmFilePath) || !File.Exists(pcmFilePath))
            {
                music_queue.TryDequeue(out _);
                _ = PlayMusicAsync(SkipTokenSource.Token);
                return;
            }

            using var pcmStream = new FileStream(pcmFilePath, FileMode.Open, FileAccess.Read);
            AudioOutStream? output = audio_client?.CreatePCMStream(AudioApplication.Music, bufferMillis: 100);

            is_playing = true;
            is_paused = false;

            if (!on_repeat)
            {
                music_queue.TryDequeue(out channelSong);
                current_song = channelSong;
                playback_history.Songs.Add(channelSong.Value);
            }
            else
            {
                current_song = music_queue.First();
            }

            await RequestedViewUpdateAsync();
            await UpdatePlayHistoryAsync();

            try
            {
                byte[] buffer = new byte[BUFFER_SIZE];
                int bytesRead;

                while ((bytesRead = await pcmStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    while (is_paused)
                    {
                        await Task.Delay(PAUSE_CHECK_INTERVAL_MS);
                    }
                    await output.WriteAsync(buffer, 0, bytesRead);
                    if (cancellationToken.IsCancellationRequested)
                        break;
                }
                //add view to channelSong;
                if (!cancellationToken.IsCancellationRequested)
                {
                    Song? songDb = await _songRepository.GetByIdAsync(current_song.Value.Value.Id);
                    if (songDb != null)
                    {
                        songDb.Views++;
                        await _songRepository.UpdateAsync(songDb);
                        await UpdatePopularSongsAsync();
                    }
                }
            }
            catch (OperationCanceledException ex)
            {
                await Logger.AddLog($"CanceledException during playback: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Warning);
            }
            catch (Exception ex)
            {
                await Logger.AddLog($"Error during playback: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error);
            }
            finally
            {
                await output.FlushAsync();
                current_song = null;
                is_playing = false;
                _ = PlayMusicAsync(SkipTokenSource.Token);
            }
            return;
        }
        else if (!is_playing && !is_paused && music_queue.Count == 0)
        {
            await RequestedViewUpdateAsync();
            await LeaveAsync();
        }
    }


    /// <summary>
    /// PLay channelSong provided in query in current voice channel
    /// </summary>
    /// <param name="voiceChannel">object representing voice channel</param>
    /// <param name="query">name of channelSong or Url or file name </param>
    /// <param name="textChannel">object representing text channel, where command was called</param>
    /// <remarks>
    /// Find => Download (if needed) => Add to queue channelSong by query. 
    /// This implementation use IDs and DB
    /// </remarks>
    /// <returns>Task</returns>
    public async Task PlayAsync(IVoiceChannel? voiceChannel, int? song_id = null, int? playlist_id = null)
    {
        if (voiceChannel == null)
        {
            await Logger.AddLog("User not in voice chat", Microsoft.Extensions.Logging.LogLevel.Warning);
            return;
        }

        if (song_id != null)
        {
            Song? song = await _songRepository.GetByIdAsync(song_id);
            if (song == null)
                return;

            KeyValuePair<IVoiceChannel, Song> temp = new(voiceChannel, song);

            music_queue.Enqueue(temp);
        }
        if (playlist_id != null)
        {
            Playlist? playlist = await _playlistRepository.GetWithSongsAsync(playlist_id.Value);
            if (playlist == null)
                return;
            foreach (Song s in playlist.Songs)
            {
                KeyValuePair<IVoiceChannel, Song> temp = new(voiceChannel, s);
                music_queue.Enqueue(temp);
            }
        }
        await Logger.AddLog("Line len - " + music_queue.Count.ToString());

        await PlayMusicAsync(SkipTokenSource.Token);
        return;
    }
    /// <summary>
    /// PLay channelSong provided in query in current voice channel
    /// </summary>
    /// <param name="voiceChannel">object representing voice channel</param>
    /// <param name="query">name of channelSong or Url or file name </param>
    /// <param name="textChannel">object representing text channel, where command was called</param>
    /// <remarks>
    /// Find => Download (if needed) => Add to queue channelSong by query. 
    /// This implementation use search, DB and online search
    /// </remarks>
    /// <returns>Task</returns>
    public async Task PlayAsync(IVoiceChannel? voiceChannel, string query = "", IMessageChannel? textChannel = null)
    {
        if (voiceChannel == null)
        {
            await Logger.AddLog("User not in voice chat", Microsoft.Extensions.Logging.LogLevel.Warning);
            if (textChannel != null)
            {
                await textChannel.SendMessageAsync("user not in voice");
            }
            return;
        }

        List<Song> songs;

        Song? song = await _songRepository.GetByNameOrLinkAsync(query);
        Playlist? playlist = await _playlistRepository.GetByNameAsync(query);

        if (song != null)
        {
            songs = new List<Song>() { song };
            await Logger.AddLog("used saved track instead downloading");
        }
        else if (playlist != null)
        {
            Playlist? playlistWithSongs = await _playlistRepository.GetWithSongsAsync(playlist.Id);
            songs = playlistWithSongs?.Songs.ToList() ?? new List<Song>();
        }
        else
        {
            List<Song>? songs_info = await _videoFinder.Find(query, true);
            if (songs_info == null)
            {
                if (textChannel != null)
                {
                    await textChannel.SendMessageAsync(embed: new EmbedBuilder()
                        .WithDescription("q cant find that trash, мой маленький гой")
                        .WithColor(Color.Orange)
                        .Build()
                    );
                }
                return;
            }

            // Сначала добавляем треки в БД (если их там нет)
            List<Song> songs_db_instances = new();
            foreach (Song s in songs_info)
            {
                IQueryable<Song> songsByName = await _songRepository.GetByNameAsync(s.Name);
                Song? inst = songsByName.FirstOrDefault(S => s.Name == S.Name && s.AuthorName == S.AuthorName);
                if (inst == null)
                {
                    Song added = await _songRepository.AddAsync(s);
                    songs_db_instances.Add(added);
                }
                else
                {
                    songs_db_instances.Add(inst);
                }
            }

            // Параллельное скачивание с добавлением в очередь по мере готовности
            string musicFolderPath = Path.Combine(Environment.CurrentDirectory, _config["music_folder"] ?? "music");
            ConcurrentDictionary<int, Song?> downloadedSongs = new();

            // Запускаем параллельное скачивание в фоне
            _ = Task.Run(async () =>
            {
                await _audioDownloader.DownloadAsync(songs_db_instances, _songRepository, musicFolderPath, downloadedSongs);
            });

            // Отслеживаем готовые треки и добавляем в очередь по порядку
            _ = Task.Run(async () =>
            {
                int nextIndex = 0;
                while (nextIndex < songs_db_instances.Count)
                {
                    if (downloadedSongs.TryGetValue(nextIndex, out Song? downloadedSong))
                    {
                        if (downloadedSong != null)
                        {
                            KeyValuePair<IVoiceChannel, Song> temp = new(voiceChannel, downloadedSong);
                            music_queue.Enqueue(temp);
                            await Logger.AddLog($"Added to queue: {downloadedSong.Name}");
                        }
                        nextIndex++;
                    }
                    else
                    {
                        // Ждем немного перед следующей проверкой
                        await Task.Delay(100);
                    }
                }

                await Logger.AddLog("All songs processed for queue");
                // Вызываем событие обновления вьюшки
                await RequestedViewUpdateAsync();
            });

            // Запускаем воспроизведение, если очередь пуста (будет ждать первого трека)
            if (music_queue.IsEmpty)
            {
                _ = PlayMusicAsync(SkipTokenSource.Token);
            }
            return;
        }





        foreach (Song s in songs)
        {
            KeyValuePair<IVoiceChannel, Song> temp = new(voiceChannel, s);
            music_queue.Enqueue(temp);
        }

        await Logger.AddLog("Line len - " + music_queue.Count.ToString());
        await RequestedViewUpdateAsync();

        await PlayMusicAsync(SkipTokenSource.Token);
        return;
    }
    public async Task PlayAsync(ICommandContext ctx, string query)
    {
        await PlayAsync((ctx.User as IGuildUser).VoiceChannel, query, ctx.Channel as ITextChannel);
    }
    public async Task PlayAsync(ICommandContext ctx)
    {
        await TogglePauseAsync(ctx);
    }
    public async Task SetAnchorAsync(ICommandContext context)
    {
        Guild? guildEntity = await _guildRepository.GetByIdAsync(guild.Id);
        if (guildEntity != null)
        {
            guildEntity.Anchor = context.Channel.Id;
            await _guildRepository.UpdateAsync(guildEntity);
            guild = guildEntity;
            await Logger.AddLog($"Anchor for {guild.Name} is now {context.Channel.Name}");
        }
    }
    protected async Task UpdatePlayHistoryAsync()
    {
        if (current_song == null)
            return;

        int history_limit = HISTORY_LIMIT;
        Song song = current_song.Value.Value;
        if (!playback_history.Songs.Select(s => s.Id).Contains(song.Id))
        {
            playback_history.Songs.Add(song);
        }
        ;
        while (playback_history.Songs.Count > history_limit)
        {
            playback_history.Songs.Remove(playback_history.Songs.Last());
        }
        await Logger.AddLog("Playback updated");
    }

    public async Task<bool> ToggleMusicLikeAsync(int? playlistId, int? songId = null)
    {
        songId = current_song?.Value.Id;

        if (songId == null || playlistId == null)
            return false;

        Playlist? playlist = await _playlistRepository.GetWithSongsAsync(playlistId.Value);
        Song? song = await _songRepository.GetByIdAsync(songId);

        if (playlist == null || song == null)
            return false;

        if (playlist.Songs.Contains(song))
        {
            playlist.Songs.Remove(song);
            await Logger.AddLog($"{song.Name} removed from favorite on guild {guild.DiscordId}");
        }
        else if (playlist.Songs.Count == MAX_PLAYLIST_SIZE)//limit 25, -2 reserved options in every selector
        {
            await Logger.AddLog($"{song.Name} did not add to favorite on guild {guild.DiscordId} to much favorite");
            return false;
        }
        else
        {
            playlist.Songs.Add(song);
            await Logger.AddLog($"{song.Name} added to favorite on guild {guild.DiscordId}");
        }

        await _playlistRepository.UpdateAsync(playlist);

        return true;
    }
    public async Task<bool> AddPlaylistAsync(string? playlist_name, ulong? author_id)
    {
        await Logger.AddLog($"AddPlaylistAsync called, playlist name: {playlist_name}");

        if (guild.Playlists.Count == MAX_PLAYLIST_SIZE)
        {
            await Logger.AddLog("Playlist not added - guild lists count overflow", Microsoft.Extensions.Logging.LogLevel.Warning);
            return false;
        }

        if (playlist_name == null || author_id == null)
        {
            await Logger.AddLog("Playlist or author Id == null", Microsoft.Extensions.Logging.LogLevel.Warning);
            return false;
        }

        bool playlistExists = guild.Playlists.Select(p => p.Name).Any(name => name == playlist_name);

        if (playlistExists)
        {
            await Logger.AddLog("Playlist already exist", Microsoft.Extensions.Logging.LogLevel.Warning);
            return false;
        }
        Playlist playlist_entity;

        List<Song>? found_songs = await _videoFinder.FindPlaylistByLink(playlist_name, true);
        if (found_songs != null)
        {
            List<Song> song_list = found_songs?.Take(MAX_PLAYLIST_SIZE).ToList() ?? new List<Song>();

            List<Song> song_db_instances = new();

            foreach (Song s in song_list)
            {
                IQueryable<Song> songsByName = await _songRepository.GetByNameAsync(s.Name);
                Song? inst = songsByName.FirstOrDefault(S => s.Name == S.Name && s.AuthorName == S.AuthorName);
                if (inst == null)
                {
                    Song added = await _songRepository.AddAsync(s);
                    song_db_instances.Add(added);
                }
                else
                {
                    song_db_instances.Add(inst);
                }
            }

            // Параллельное скачивание файлов для треков плейлиста
            string musicFolderPath = Path.Combine(Environment.CurrentDirectory, _config["music_folder"] ?? "music");
            ConcurrentDictionary<int, Song?> downloadedSongs = new();

            // Создаем плейлист сразу (будет обновляться по мере добавления треков)
            playlist_entity = new Playlist
            {
                Name = await _videoFinder.GetPlaylistName(playlist_name) ?? playlist_name,
                Songs = new List<Song>(),
                AuthorId = author_id.Value
            };
            playlist_entity = await _playlistRepository.AddAsync(playlist_entity);

            // Запускаем параллельное скачивание в фоне
            _ = Task.Run(async () =>
            {
                await _audioDownloader.DownloadAsync(song_db_instances, _songRepository, musicFolderPath, downloadedSongs);
            });

            // Отслеживаем готовые треки и добавляем в плейлист по порядку
            _ = Task.Run(async () =>
            {
                int nextIndex = 0;
                while (nextIndex < song_db_instances.Count)
                {
                    if (downloadedSongs.TryGetValue(nextIndex, out Song? downloadedSong))
                    {
                        if (downloadedSong != null)
                        {
                            // Обновляем плейлист в БД, добавляя новый трек
                            // Используем GetWithSongsAsync для загрузки навигационных свойств
                            Playlist? currentPlaylist = await _playlistRepository.GetWithSongsAsync(playlist_entity.Id);
                            if (currentPlaylist != null)
                            {
                                if (!currentPlaylist.Songs.Any(s => s.Id == downloadedSong.Id))
                                {
                                    currentPlaylist.Songs.Add(downloadedSong);
                                    await _playlistRepository.UpdateAsync(currentPlaylist);
                                    await Logger.AddLog($"Added to playlist: {downloadedSong.Name}");
                                }
                            }
                        }
                        nextIndex++;
                    }
                    else
                    {
                        // Ждем немного перед следующей проверкой
                        await Task.Delay(100);
                    }
                }

                await Logger.AddLog("All songs processed for playlist");
            });
        }
        else
        {
            // Если плейлист не найден по ссылке, создаем пустой
            playlist_entity = new Playlist
            {
                Name = playlist_name,
                AuthorId = author_id.Value
            };
            playlist_entity = await _playlistRepository.AddAsync(playlist_entity);
        }

        Guild? guildWithPlaylists = await _guildRepository.GetByDiscordIdWithPlaylistsAsync(guild.DiscordId);
        if (guildWithPlaylists != null)
        {
            guildWithPlaylists.Playlists.Add(playlist_entity);
            await _guildRepository.UpdateAsync(guildWithPlaylists);
            guild = guildWithPlaylists;
        }

        await UpdateLikedMusicAsync();
        await RequestedViewUpdateAsync();
        return true;
    }
    public async Task UpdatePopularSongsAsync()
    {
        popular_songs.Songs.Clear();
        List<Song> songs = await _songRepository.GetPopularSongsListAsync(MAX_PLAYLIST_SIZE);
        popular_songs.Songs = songs;
        await Task.CompletedTask;
    }
    public async Task<bool> RemovePlaylistAsync(int playlist_id)
    {
        await Logger.AddLog($"RemovePlaylistAsync called, playlist id: {playlist_id}");

        Playlist? playlist = await _playlistRepository.GetByIdAsync(playlist_id);

        if (playlist == null)
            return false;
        await _playlistRepository.RemoveAsync(playlist);
        await UpdateLikedMusicAsync();
        return true;
    }
    public async Task UpdateLikedMusicAsync()
    {
        Guild? guildWithPlaylists = await _guildRepository.GetByDiscordIdWithPlaylistsAsync(guild.DiscordId);
        if (guildWithPlaylists != null)
        {
            guild = guildWithPlaylists;
            List<Playlist> playlists = guild.Playlists.ToList();

            saved_music = playlists;
            saved_music.Add(playback_history);
            saved_music.Add(popular_songs);
            await Logger.AddLog("Saved music updated");
        }
    }
    public async Task ToggleRepeatAsync()
    {
        if (on_repeat)
        {
            KeyValuePair<IVoiceChannel, Song> song;
            music_queue.TryDequeue(out song);
            await Logger.AddLog("Repeat: off");
        }
        else
        {
            if (current_song != null)
                music_queue.Enqueue(current_song.Value);
            await Logger.AddLog("Repeat: on");
        }

        this.on_repeat = !this.on_repeat;

    }

    /// <summary>
    /// Вызывает событие обновления вьюшки
    /// </summary>
    protected async Task RequestedViewUpdateAsync()
    {

        await ViewUpdateRequested.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (ffmpeg != null)
            ffmpeg.Dispose();
        if (audio_client != null)
            audio_client.Dispose();
        if (music_view != null)
            await music_view.DeleteViewMessageAsync();
        return;
    }
}
