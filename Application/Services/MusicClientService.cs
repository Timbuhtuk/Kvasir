using Application.Interfaces;
using Application.Models;
using Entities.Enums;
using Entities.Models;
using Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Application.Services;



[LogCategory(LogCategory.Music)]
public class MusicClientService : IAsyncDisposable
{
    #region Constants
    private const int MAX_PLAYLIST_SIZE = 23;
    private const int HISTORY_LIMIT = 10;
    // 20ms аудио: 48000 Hz * 0.02 сек * 2 канала * 2 байта = 3840 байт
    private const int BUFFER_SIZE = 3840; // Было: 81_960
    private const int PAUSE_CHECK_INTERVAL_MS = 80;
    private const int TRACK_TRANSITION_DELAY = 100;
    #endregion

    #region fields

    private Entities.Models.Guild guild;
    private BotVoiceChannel? current_voice_channel;
    private readonly ISongRepository _songRepository;
    private readonly IPlaylistRepository _playlistRepository;
    private readonly IBotConnector _connector;
    private readonly IVideoFinderService _videoFinder;
    private readonly IAudioDownloaderService _audioDownloader;
    private readonly IConfiguration _config;
    private readonly IGuildRepository _guildRepository;
    public Playlist playback_history { get; protected set; } = new Playlist() { Name = "History", Id = -1, AuthorId = 0 };
    public Playlist popular_songs { get; protected set; } = new Playlist() { Name = "Popular", Id = -2, AuthorId = 0 };

    public (BotVoiceChannel, Song)? current_song { get; protected set; }
    public ObservableCollection<(BotVoiceChannel, Song)> music_queue { get; protected set; }

    public PlaybackStatus playbackStatus { get; protected set; } = PlaybackStatus.None;
    public bool on_repeat { get; protected set; } = false;
    private CancellationTokenSource SkipTokenSource { get; set; } = new CancellationTokenSource();


    public IBotVoiceConnection? audio_client { get; protected set; }

    public MusicViewService? music_view { get; protected set; }

    public event Func<Task>? ViewUpdateRequested;

    private readonly SemaphoreSlim _playbackSemaphore = new(1, 1);
    #endregion

    public MusicClientService(
        Entities.Models.Guild guild,
        IBotConnector connector,
        IConfiguration config,
        IVideoFinderService videoFinder,
        IAudioDownloaderService audioDownloader,
        IServiceProvider serviceProvider,
        ISongRepository songRepository,
        IGuildRepository guildRepository,
        IPlaylistRepository playlistRepository,
        IServiceScopeFactory serviceScopeFactory
    )
    {

        _connector = connector;
        _songRepository = songRepository;
        _playlistRepository = playlistRepository;
        _videoFinder = videoFinder;
        _audioDownloader = audioDownloader;
        _config = config;
        _guildRepository = guildRepository;

        music_queue = new ObservableCollection<(BotVoiceChannel, Song)>();
        music_queue.CollectionChanged += OnQueueCollectionChanged;

        music_view = ActivatorUtilities.CreateInstance<MusicViewService>(
            serviceProvider,
            this,
            serviceScopeFactory,
            guild.DiscordId,
            connector
        );

        ViewUpdateRequested += music_view.UpdateMusicViewAsync;

        this.guild = guild;

        LogContext.SetGuild(this.guild.DiscordId, this.guild.Name);
    }

    private void OnQueueCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            _ = PlayMusicAsync(SkipTokenSource.Token);

    }

    public async Task ClearAsync()
    {
        music_queue.Clear();

        playbackStatus = PlaybackStatus.None;
        current_song = null;
        await LeaveAsync();
    }

    public async Task SkipAsync()
    {
        await Logger.AddLog("SkipAsync called ");

        if (playbackStatus != PlaybackStatus.None)
        {
            SkipTokenSource.Cancel();
        }
        else
        {
            playbackStatus = PlaybackStatus.None;
            current_song = null;
        }
        await RequestedViewUpdateAsync();
    }

    public async Task LeaveAsync()
    {
        if (audio_client != null)
        {
            await audio_client.DisposeAsync();
            audio_client = null;
        }
        await Task.CompletedTask;
    }

    public async Task<bool> JoinAsync(BotVoiceChannel channel)
    {
        await Logger.AddLog($"JoinAsync called for channel {channel.Name}");

        // Если уже подключены к этому каналу, возвращаем успех
        if (audio_client != null && current_voice_channel?.Id == channel.Id)
        {
            await Logger.AddLog("Already connected to this voice channel");
            return true;
        }

        // Очищаем предыдущее соединение если есть
        if (audio_client != null)
        {
            try
            {
                await audio_client.DisposeAsync();
            }
            catch { }
            audio_client = null;
        }
        try
        {
            audio_client = await _connector.ConnectVoiceAsync(channel);
            current_voice_channel = channel;
            await Logger.AddLog("Voice connected successfully");
            return true;
        }
        catch (Exception ex)
        {
            await Logger.AddLog($"Voice connection failed: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, exception: ex);
            throw;
        }

    }

    public async Task TogglePauseAsync()
    {
        playbackStatus = playbackStatus switch
        {
            PlaybackStatus.Paused => PlaybackStatus.Playing,
            PlaybackStatus.Playing => PlaybackStatus.Paused,
            _ => PlaybackStatus.None,
        };
        await Logger.AddLog(playbackStatus.ToString());
    }

    //политика метода - если вызвали то пытаюсь играть - если что-то не так - я скипну и закроюсь а потом вызову себя же
    public async Task PlayMusicAsync(CancellationToken skipCancellationToken)
    {
        if (!await _playbackSemaphore.WaitAsync(100))
        {
            return;
        }
        try
        {
            if (music_queue.Count == 0)
            {
                await RequestedViewUpdateAsync();
                return;
            }

            BotVoiceChannel voiceChannel = music_queue[0].Item1;
            Song song = music_queue[0].Item2;

            try
            {
                await JoinAsync(voiceChannel);
            }
            catch
            {
                await SkipAsync();
                return;
            }

            Stream? pcmStream = await _songRepository.GetFileStreamAsync(song);
            if (pcmStream == null)
            {
                await SkipAsync();
                return;
            }


            try
            {
                using (pcmStream)
                {
                    Stream? voiceOutStream = audio_client?.CreatePcmStream();

                    if (voiceOutStream == null)
                    {
                        await Logger.AddLog("Failed to create output stream - audio client may be null", LogLevel.Error);
                        await SkipAsync();
                        return;
                    }

                    playbackStatus = PlaybackStatus.Playing;
                    current_song = music_queue[0];

                    if (!on_repeat)
                        music_queue.RemoveAt(0);

                    await UpdatePlayHistoryAsync();
                    await RequestedViewUpdateAsync();

                    byte[] buffer = new byte[BUFFER_SIZE];
                    int bytesRead;
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    var nextPacketTime = stopwatch.ElapsedMilliseconds;

                    while ((bytesRead = await pcmStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        while (playbackStatus == PlaybackStatus.Paused && !skipCancellationToken.IsCancellationRequested)
                            await Task.Delay(PAUSE_CHECK_INTERVAL_MS);

                        if (skipCancellationToken.IsCancellationRequested)
                            break;

                        await voiceOutStream.WriteAsync(buffer, 0, bytesRead);
                    }

                    await voiceOutStream.FlushAsync();
                    await voiceOutStream.DisposeAsync();
                }

                if (await _songRepository.IncrementViewsAsync(current_song.Value.Item2))
                    await UpdatePopularSongsAsync();
            }
            catch (OperationCanceledException ex)
            {
                await Logger.AddLog($"CanceledException during playback: {ex.Message}", LogLevel.Warning);
                _ = RequestedViewUpdateAsync();
            }
            catch (Exception ex)
            {
                _ = Logger.AddLog($"Error during playback: {ex.Message}", LogLevel.Error, exception: ex);
            }
            finally
            {
                current_song = null;
                playbackStatus = PlaybackStatus.None;

                if (skipCancellationToken.IsCancellationRequested)
                {
                    SkipTokenSource = new CancellationTokenSource();
                    await Task.Delay(TRACK_TRANSITION_DELAY);
                }

                _ = PlayMusicAsync(SkipTokenSource.Token);
            }
        }
        finally
        {
            _playbackSemaphore.Release();
        }
    }

    public async Task PlayAsync(BotVoiceChannel? voiceChannel, int? songId = null, int? playlistId = null, ulong? textChannelId = null)
    {
        if (!await ValidateVoiceChannelAsync(voiceChannel, textChannelId))
            return;

        if (songId != null)
        {
            Song? song = await _songRepository.GetByIdAsync(songId.Value);
            if (song == null)
                return;

            music_queue.Add((voiceChannel!, song));
        }

        if (playlistId != null)
        {
            Playlist? playlist = await _playlistRepository.GetWithSongsAsync(playlistId.Value);
            if (playlist == null)
                return;

            foreach (var song in playlist.Songs)
                music_queue.Add((voiceChannel!, song));
        }

        await Logger.AddLog($"Line len - {music_queue.Count}");
        if (playbackStatus != PlaybackStatus.None) await RequestedViewUpdateAsync();
    }

    public async Task PlayAsync(BotVoiceChannel? voiceChannel, string query = "", ulong? textChannelId = null)
    {
        if (!await ValidateVoiceChannelAsync(voiceChannel, textChannelId))
            return;

        List<Song>? songs = await _songRepository.GetByQueryAsync(query);

        if (songs == null || songs.Count == 0)
        {

            if (textChannelId == null)
                return;

            await _connector.SendMessageAsync(textChannelId.Value, new BotMessageContent("q cant find that trash, мой маленький гой"));
            return;
        }

        foreach (Song song in songs)
        {
            _ = Logger.AddLog($"Added to queue: {song.Name}");
            music_queue.Add((voiceChannel!, song));
        }
        _ = Logger.AddLog($"Line len - {music_queue.Count}");
        if (playbackStatus != PlaybackStatus.None) await RequestedViewUpdateAsync();
    }


    private async Task<bool> ValidateVoiceChannelAsync(BotVoiceChannel? voiceChannel, ulong? textChannelId = null)
    {
        if (voiceChannel != null)
            return true;

        await Logger.AddLog("User not in voice chat", Microsoft.Extensions.Logging.LogLevel.Warning);

        if (textChannelId != null)
            await _connector.SendMessageAsync(textChannelId.Value, new BotMessageContent("user not in voice"));

        return false;
    }




    protected async Task UpdatePlayHistoryAsync()
    {
        if (current_song == null)
            return;

        int history_limit = HISTORY_LIMIT;
        Song song = current_song.Value.Item2;
        if (!playback_history.Songs.Select(s => s.Id).Contains(song.Id))
            playback_history.Songs.Add(song);
        while (playback_history.Songs.Count > history_limit)
            playback_history.Songs.Remove(playback_history.Songs.Last());
        await Logger.AddLog("Playback updated");
    }

    public async Task<bool> ToggleMusicLikeAsync(int? playlistId, int? songId = null)
    {
        songId = current_song?.Item2.Id;

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
        else if (playlist.Songs.Count == MAX_PLAYLIST_SIZE)
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

        if (playlist_name == null || author_id == null)
        {
            await Logger.AddLog("Playlist name or author Id is null", LogLevel.Warning);
            return false;
        }

        // Get guild with playlists
        Entities.Models.Guild? guildWithPlaylists = await _guildRepository.GetByDiscordIdWithPlaylistsAsync(guild.DiscordId);
        if (guildWithPlaylists == null)
        {
            await Logger.AddLog("Guild not found", LogLevel.Warning);
            return false;
        }

        // Check if playlist limit reached
        if (guildWithPlaylists.Playlists.Count >= MAX_PLAYLIST_SIZE)
        {
            await Logger.AddLog("Playlist not added - guild lists count overflow", LogLevel.Warning);
            return false;
        }

        // Check if playlist already exists
        bool playlistExists = guildWithPlaylists.Playlists.Any(p => p.Name == playlist_name);
        if (playlistExists)
        {
            await Logger.AddLog("Playlist already exists", LogLevel.Warning);
            return false;
        }

        // Find songs using VideoFinderService
        List<FinderSongDTO>? foundSongs = await _videoFinder.Find(playlist_name, true);

        Playlist playlistEntity;
        if (foundSongs != null && foundSongs.Count > 0)
        {
            // Get playlist name (if URL provided)
            string playlistDisplayName = await _videoFinder.GetPlaylistName(playlist_name) ?? playlist_name;

            // Create playlist entity
            playlistEntity = new Playlist
            {
                Name = playlistDisplayName,
                Songs = new List<Song>(),
                AuthorId = author_id.Value
            };
            playlistEntity = await _playlistRepository.AddAsync(playlistEntity);

            // Process songs asynchronously
            _ = Task.Run(async () =>
            {
                try
                {
                    List<Song> songDbInstances = new();

                    // Get or create songs in database
                    foreach (var finderSong in foundSongs.Take(MAX_PLAYLIST_SIZE))
                    {
                        // Try to find existing song by query (searches by name and link)
                        var existingSongs = await _songRepository.GetByQueryAsync(finderSong.Link ?? finderSong.Name);
                        Song? existingSong = existingSongs?.FirstOrDefault(s =>
                            (s.Link == finderSong.Link && !string.IsNullOrEmpty(finderSong.Link)) ||
                            (s.Name == finderSong.Name && s.AuthorName == finderSong.AuthorName));

                        if (existingSong != null)
                        {
                            songDbInstances.Add(existingSong);
                        }
                        else
                        {
                            // Create new song from FinderSongDTO
                            Song newSong = new()
                            {
                                Name = finderSong.Name,
                                AuthorName = finderSong.AuthorName,
                                Link = finderSong.Link,
                                Duration = finderSong.Duration ?? 0
                            };
                            Song added = await _songRepository.AddAsync(newSong);
                            songDbInstances.Add(added);
                        }
                    }

                    // Download songs
                    string musicFolderPath = Path.Combine(Environment.CurrentDirectory, _config["music_client:music_folder"] ?? "music");
                    foreach (var song in songDbInstances)
                    {
                        await _audioDownloader.DownloadAsync(song);
                    }

                    // Add songs to playlist
                    Playlist? currentPlaylist = await _playlistRepository.GetWithSongsAsync(playlistEntity.Id);
                    if (currentPlaylist != null)
                    {
                        foreach (var song in songDbInstances)
                        {
                            if (!currentPlaylist.Songs.Any(s => s.Id == song.Id))
                            {
                                currentPlaylist.Songs.Add(song);

                                // Truncate if exceeds limit
                                if (currentPlaylist.Songs.Count > MAX_PLAYLIST_SIZE)
                                {
                                    int excessCount = currentPlaylist.Songs.Count - MAX_PLAYLIST_SIZE;
                                    List<Song> songsList = currentPlaylist.Songs.ToList();
                                    for (int i = 0; i < excessCount; i++)
                                        currentPlaylist.Songs.Remove(songsList[i]);
                                    await Logger.AddLog($"Playlist truncated to {MAX_PLAYLIST_SIZE} tracks (removed {excessCount} oldest tracks)");
                                }

                                await _playlistRepository.UpdateAsync(currentPlaylist);
                                await Logger.AddLog($"Added to playlist: {song.Name}");
                            }
                        }
                    }

                    await Logger.AddLog("All songs processed for playlist");
                }
                catch (Exception ex)
                {
                    await Logger.AddLog($"Error processing playlist songs: {ex.Message}", LogLevel.Error, exception: ex);
                }
            });
        }
        else
        {
            // Create empty playlist
            playlistEntity = new Playlist
            {
                Name = playlist_name,
                AuthorId = author_id.Value
            };
            playlistEntity = await _playlistRepository.AddAsync(playlistEntity);
        }

        // Add playlist to guild
        guildWithPlaylists.Playlists.Add(playlistEntity);
        await _guildRepository.UpdateAsync(guildWithPlaylists);
        guild = guildWithPlaylists;

        await RequestedViewUpdateAsync();
        return true;
        //await Logger.AddLog($"AddPlaylistAsync called, playlist name: {playlist_name}");

        //if (guild.Playlists.Count == MAX_PLAYLIST_SIZE)
        //{
        //    await Logger.AddLog("Playlist not added - guild lists count overflow", LogLevel.Warning);
        //    return false;
        //}

        //if (playlist_name == null || author_id == null)
        //{
        //    await Logger.AddLog("Playlist or author Id == null", LogLevel.Warning);
        //    return false;
        //}

        //bool playlistExists = guild.Playlists.Select(p => p.Name).Any(name => name == playlist_name);

        //if (playlistExists)
        //{
        //    await Logger.AddLog("Playlist already exist", LogLevel.Warning);
        //    return false;
        //}
        //Playlist playlist_entity;

        //List<Song>? found_songs = await _videoFinder.Find(playlist_name, true);
        //if (found_songs != null)
        //{
        //    List<Song> song_list = found_songs?.Take(MAX_PLAYLIST_SIZE).ToList() ?? new List<SongO>();

        //    List<Song> song_db_instances = new();

        //    foreach (Song s in song_list)
        //    {
        //        IQueryable<Song> songsByName = await _songRepository.GetByNameAsync(s.Name);
        //        FinderSongDTO? inst = songsByName.FirstOrDefault(S => s.Name == S.Name && s.AuthorName == S.AuthorName);
        //        if (inst == null)
        //        {
        //            FinderSongDTO added = await _songRepository.AddAsync(s);
        //            song_db_instances.Add(added);
        //        }
        //        else
        //            song_db_instances.Add(inst);
        //    }

        //    string musicFolderPath = Path.Combine(Environment.CurrentDirectory, config["music_folder"] ?? "music");
        //    ConcurrentDictionary<int, Song?> downloadedSongs = new();

        //    playlist_entity = new Playlist
        //    {
        //        Name = await _videoFinder.GetPlaylistName(playlist_name) ?? playlist_name,
        //        Songs = new List<Song>(),
        //        AuthorId = author_id.Value
        //    };
        //    playlist_entity = await _playlistRepository.AddAsync(playlist_entity);

        //    _ = Task.Run(async () =>
        //    {
        //        await _audioDownloader.DownloadAsync(song_db_instances, _songRepository, musicFolderPath, downloadedSongs);
        //    });

        //    _ = Task.Run(async () =>
        //    {
        //        int nextIndex = 0;
        //        while (nextIndex < song_db_instances.Count)
        //        {
        //            if (downloadedSongs.TryGetValue(nextIndex, out FinderSongDTO? downloadedSong))
        //            {
        //                if (downloadedSong != null)
        //                {
        //                    Playlist? currentPlaylist = await _playlistRepository.GetWithSongsAsync(playlist_entity.Id);
        //                    if (currentPlaylist != null)
        //                    {
        //                        if (!currentPlaylist.Songs.Any(s => s.Id == downloadedSong.Id))
        //                        {
        //                            currentPlaylist.Songs.Add(downloadedSong);

        //                            if (currentPlaylist.Songs.Count > MAX_PLAYLIST_SIZE)
        //                            {
        //                                int excessCount = currentPlaylist.Songs.Count - MAX_PLAYLIST_SIZE;
        //                                List<FinderSongDTO> songsList = currentPlaylist.Songs.ToList();
        //                                for (int i = 0; i < excessCount; i++)
        //                                    currentPlaylist.Songs.Remove(songsList[i]);
        //                                await Logger.AddLog($"Playlist truncated to {MAX_PLAYLIST_SIZE} tracks (removed {excessCount} oldest tracks)");

        //                                if (_ephemeralMessageService != null && interaction != null)
        //                                {
        //                                    await _ephemeralMessageService.SendEphemeralAsync(
        //                                        interaction,
        //                                        new EmbedBuilder()
        //                                            .WithDescription($"Плейлист усечен до {MAX_PLAYLIST_SIZE} треков. Удалено {excessCount} самых старых треков.")
        //                                            .WithColor(Color.Orange)
        //                                            .Build()
        //                                    );
        //                                }
        //                            }

        //                            await _playlistRepository.UpdateAsync(currentPlaylist);
        //                            await Logger.AddLog($"Added to playlist: {downloadedSong.Name}");
        //                        }
        //                    }
        //                }
        //                nextIndex++;
        //            }
        //            else
        //                await Task.Delay(100);
        //        }

        //        await Logger.AddLog("All songs processed for playlist");
        //    });
        //}
        //else
        //{
        //    playlist_entity = new Playlist
        //    {
        //        Name = playlist_name,
        //        AuthorId = author_id.Value
        //    };
        //    playlist_entity = await _playlistRepository.AddAsync(playlist_entity);
        //}

        //Guild? guildWithPlaylists = await guildRepository.GetByDiscordIdWithPlaylistsAsync(guild.DiscordId);
        //if (guildWithPlaylists != null)
        //{
        //    guildWithPlaylists.Playlists.Add(playlist_entity);
        //    await guildRepository.UpdateAsync(guildWithPlaylists);
        //    guild = guildWithPlaylists;
        //}

        //await RequestedViewUpdateAsync();
        //return true;
    }

    public async Task UpdatePopularSongsAsync()
    {
        popular_songs.Songs.Clear();
        List<Song> songs = await _songRepository.GetPopularSongsListAsync(MAX_PLAYLIST_SIZE);
        popular_songs.Songs = songs;
        await Task.CompletedTask;
    }

    public async Task ToggleRepeatAsync()
    {
        if (on_repeat)
        {
            if (music_queue.Count > 0)
                music_queue.RemoveAt(0);
            await Logger.AddLog("Repeat: off");
        }
        else
        {
            if (current_song != null)
                music_queue.Add(current_song.Value);
            await Logger.AddLog("Repeat: on");
        }

        this.on_repeat = !this.on_repeat;
    }

    protected async Task RequestedViewUpdateAsync() => await ViewUpdateRequested?.Invoke()!;

    public async ValueTask DisposeAsync()
    {

        if (audio_client != null)
            await audio_client.DisposeAsync();
        if (music_view != null)
        {
            music_view.DisposeAutoUpdateTimer();
            await music_view.DeleteViewMessageAsync();
        }
        _playbackSemaphore?.Dispose();
        return;
    }
}
