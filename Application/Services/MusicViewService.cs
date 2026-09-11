using Application.Interfaces;
using Application.Models;
using Entities.Enums;
using Entities.Models;
using Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Application.Services;

/// <summary>
/// Builds a platform-independent music view. Rendering is delegated to IBotConnector.
/// </summary>
public class MusicViewService
{
    private const int AUTO_UPDATE_INTERVAL_MS = 899000;
    private const int MAX_PLAYLIST_SIZE = 23;
    private const string PLAYING_ICON_URL = "https://media0.giphy.com/media/v1.Y2lkPTc5MGI3NjExMHpuNm8ycXdiaTRkem81ZHN6M2w5MDdibnVrNmZ3MHhxNjIwa3VyNCZlcD12MV9pbnRlcm5hbF9naWZfYnlfaWQmY3Q9cw/vJHNq9tziq9C3HMrIB/giphy.gif";

    protected readonly MusicClientService _music_client;
    protected readonly IServiceScopeFactory _serviceScopeFactory;
    protected readonly ulong _guildDiscordId;
    protected readonly IBotConnector _connector;
    protected readonly SemaphoreSlim _renderSemaphore = new(1, 1);

    private readonly SemaphoreSlim _autoUpdateSemaphore = new(1, 1);
    private readonly HashSet<ulong> _deletedMessageIds = new();
    private readonly Timer _autoUpdateTimer;
    private ulong? _viewMessageId;
    private ulong? _viewChannelId;

    public Playlist current_playlist;

    public MusicViewService(
        MusicClientService music_client,
        IServiceScopeFactory serviceScopeFactory,
        ulong guildDiscordId,
        IBotConnector connector)
    {
        _music_client = music_client;
        _serviceScopeFactory = serviceScopeFactory;
        _guildDiscordId = guildDiscordId;
        _connector = connector;
        current_playlist = music_client.playback_history;

        _autoUpdateTimer = new Timer(AutoUpdateCallback, null, AUTO_UPDATE_INTERVAL_MS, AUTO_UPDATE_INTERVAL_MS);
    }

    private void AutoUpdateCallback(object? state) => _ = AutoUpdateAsync();

    private async Task AutoUpdateAsync()
    {
        if (!await _autoUpdateSemaphore.WaitAsync(0))
            return;

        try
        {
            using IServiceScope scope = _serviceScopeFactory.CreateScope();
            ISongRepository songRepository = scope.ServiceProvider.GetRequiredService<ISongRepository>();
            List<Song> songs = await songRepository.GetPopularSongsListAsync(MAX_PLAYLIST_SIZE);

            _music_client.popular_songs.Songs.Clear();
            _music_client.popular_songs.Songs = songs;

            await RerenderMusicViewAsync();
        }
        catch (Exception ex)
        {
            await Logger.AddLog($"Error in AutoUpdateAsync: {ex.Message}", LogLevel.Error, exception: ex);
        }
        finally
        {
            _autoUpdateSemaphore.Release();
        }
    }

    public async Task<List<BotComponent>> CreateComponentsAsync()
    {
        List<BotSelectOption> songs = new();
        List<BotSelectOption> playlists = new();

        using (IServiceScope scope = _serviceScopeFactory.CreateScope())
        {
            IPlaylistRepository playlistRepository = scope.ServiceProvider.GetRequiredService<IPlaylistRepository>();
            IGuildRepository guildRepository = scope.ServiceProvider.GetRequiredService<IGuildRepository>();

            if (current_playlist.Id > 0)
            {
                Playlist? loadedPlaylist = await playlistRepository.GetWithSongsAsync(current_playlist.Id);
                if (loadedPlaylist != null)
                    current_playlist = loadedPlaylist;
            }

            if (current_playlist.Songs.Count > 0)
            {
                if (current_playlist.Id is not (-1) and not (-2))
                    songs.Add(new BotSelectOption("run all", "run all"));

                foreach (Song song in current_playlist.Songs)
                    songs.Add(new BotSelectOption(song.Name, song.Id.ToString()));
            }

            if (current_playlist.Id is not (-1) and not (-2))
                songs.Add(new BotSelectOption("delete list", "delete list"));

            Entities.Models.Guild? guild = await guildRepository.GetByDiscordIdWithPlaylistsAsync(_guildDiscordId);
            List<Playlist> guildPlaylists = guild?.Playlists.ToList() ?? new List<Playlist>();
            guildPlaylists.Add(_music_client.playback_history);
            guildPlaylists.Add(_music_client.popular_songs);

            for (int q = guildPlaylists.Count - 1; q >= 0; q--)
                playlists.Add(new BotSelectOption(guildPlaylists[q].Name, guildPlaylists[q].Id.ToString()));
        }

        List<BotComponent> components = new();
        List<BotComponent> buttons = new()
        {
            new BotButton("LIKE", GetLikeButtonLabel(), GetLikeButtonStyle(), GetLikeButtonDisabled()),
            new BotButton("PAUSE_UNPAUSE", GetPauseUnpauseLabel(), GetPauseUnpauseStyle()),
            new BotButton("FORWARD", "▶▶|", GetForwardButtonStyle()),
            new BotButton("REPEAT", "⟳", GetRepeatButtonStyle()),
            new BotButton("ADDPLAYLIST", "+new playlist", BotButtonStyle.Success),
        };

        components.Add(new BotComponentRow(buttons));

        if (playlists.Count > 0)
            components.Add(new BotSelect("PLAYLIST_MENU", current_playlist.Name, playlists));

        if (songs.Count > 0)
            components.Add(new BotSelect("SONGS_MENU", "playlist content", songs));

        return components;
    }

    public async Task RerenderMusicViewAsync(ulong? channelId = null)
    {
        if (!await _renderSemaphore.WaitAsync(0))
            return;

        try
        {
            await DeleteViewMessageCoreAsync();
            await EnsureViewMessageAsync(channelId);
        }
        catch (Exception ex)
        {
            await Logger.AddLog($"Music view render failed: {ex.Message}", LogLevel.Error, exception: ex);
        }
        finally
        {
            _renderSemaphore.Release();
        }

        await UpdateMusicViewAsync();
    }

    public async Task<bool> UpdateMusicViewAsync()
    {
        if (!await _renderSemaphore.WaitAsync(300))
            return false;

        try
        {
            await EnsureViewMessageAsync();
            if (!_viewMessageId.HasValue || !_viewChannelId.HasValue)
                return false;

            bool isPlaying = _music_client.playbackStatus == PlaybackStatus.Playing;
            BotEmbed? embed = null;
            if (_music_client.music_queue.Count > 0 || _music_client.current_song != null)
            {
                embed = new BotEmbed(
                    RenderQueue(),
                    isPlaying ? 0x5EC130 : 0x17472D,
                    "---LINE--------------------------------------------------",
                    isPlaying ? PLAYING_ICON_URL : null);
            }

            List<BotComponent> components = await CreateComponentsAsync();
            BotMessageContent content = new("", embed, components);
            await _connector.UpdateMessageAsync(_viewChannelId.Value, _viewMessageId.Value, content);
            return true;
        }
        catch (Exception ex)
        {
            await Logger.AddLog($"View update failure: {ex.Message}", LogLevel.Warning);
            return false;
        }
        finally
        {
            _renderSemaphore.Release();
        }
    }

    private async Task EnsureViewMessageAsync(ulong? channelId = null)
    {
        if (_viewMessageId.HasValue && _viewChannelId.HasValue)
            return;

        if (!channelId.HasValue)
        {
            using IServiceScope scope = _serviceScopeFactory.CreateScope();
            IGuildRepository guildRepository = scope.ServiceProvider.GetRequiredService<IGuildRepository>();
            Entities.Models.Guild? guild = await guildRepository.GetByDiscordIdAsync(_guildDiscordId);
            channelId = guild?.Anchor;
        }

        if (!channelId.HasValue || !await _connector.IsTextChannelAsync(_guildDiscordId, channelId.Value))
        {
            await Logger.AddLog("No accessible anchor channel. Cannot create music view", LogLevel.Warning);
            return;
        }

        BotMessage message = await _connector.SendMessageAsync(channelId.Value, new BotMessageContent("."));
        _viewMessageId = message.Id;
        _viewChannelId = message.ChannelId;
    }

    public Task SetViewMessage(BotMessage? message)
    {
        _viewMessageId = message?.Id;
        _viewChannelId = message?.ChannelId;
        return Task.CompletedTask;
    }

    public async Task DeleteViewMessageAsync()
    {
        await _renderSemaphore.WaitAsync();
        try
        {
            await DeleteViewMessageCoreAsync();
        }
        finally
        {
            _renderSemaphore.Release();
        }
    }

    private async Task DeleteViewMessageCoreAsync()
    {
        if (!_viewMessageId.HasValue || !_viewChannelId.HasValue)
            return;

        ulong messageId = _viewMessageId.Value;
        ulong channelId = _viewChannelId.Value;
        _deletedMessageIds.Add(messageId);

        try
        {
            await _connector.DeleteMessageAsync(channelId, messageId);
        }
        catch
        {
        }

        _viewMessageId = null;
        _viewChannelId = null;

        _ = Task.Run(async () =>
        {
            await Task.Delay(5000);
            _deletedMessageIds.Remove(messageId);
        });
    }

    public async Task HandleMessageDeletedAsync(BotMessageDeleted message)
    {
        if (message.GuildId != _guildDiscordId || _deletedMessageIds.Contains(message.MessageId))
            return;

        if (_viewMessageId != message.MessageId)
            return;

        _viewMessageId = null;
        _viewChannelId = null;
        await RerenderMusicViewAsync();
    }

    public async Task HandleMessageReceivedAsync(BotMessage message)
    {
        if (message.GuildId != _guildDiscordId)
            return;

        using IServiceScope scope = _serviceScopeFactory.CreateScope();
        IGuildRepository guildRepository = scope.ServiceProvider.GetRequiredService<IGuildRepository>();
        Entities.Models.Guild? guild = await guildRepository.GetByDiscordIdAsync(_guildDiscordId);

        if (guild?.Anchor != message.ChannelId)
            return;

        if (message.AuthorId == _connector.CurrentUserId)
        {
            if (message.Content == ".")
                await SetViewMessage(message);
            return;
        }

        await RerenderMusicViewAsync();
    }

    protected string RenderQueue()
    {
        if (_music_client.music_queue.Count == 0 && _music_client.current_song == null)
            return "";

        StringBuilder result = new();
        int offset = _music_client.on_repeat ? 1 : 0;
        List<(BotVoiceChannel, Song)> musicList = _music_client.music_queue.ToList();

        for (int q = musicList.Count - 1; q >= offset; q--)
            result.Append($"{q + 2 - offset} {musicList[q].Item2.Name}\n");

        if (_music_client.playbackStatus is PlaybackStatus.Playing or PlaybackStatus.Paused && _music_client.current_song != null)
            result.Append($"**1. {_music_client.current_song.Value.Item2.Name}**");

        return result.ToString();
    }

    protected string GetLikeButtonLabel()
    {
        if (GetLikeButtonDisabled())
            return "X";

        Song song = _music_client.current_song!.Value.Item2;
        return current_playlist.Songs.Select(s => s.Id).Contains(song.Id) ? "🤍" : "💚";
    }

    protected BotButtonStyle GetLikeButtonStyle()
    {
        if (GetLikeButtonDisabled())
            return BotButtonStyle.Secondary;

        Song song = _music_client.current_song!.Value.Item2;
        return current_playlist.Songs.Select(s => s.Id).Contains(song.Id)
            ? BotButtonStyle.Success
            : BotButtonStyle.Secondary;
    }

    protected bool GetLikeButtonDisabled() =>
        _music_client.current_song == null || current_playlist.Id is -1 or -2;

    protected string GetPauseUnpauseLabel() =>
        _music_client.playbackStatus == PlaybackStatus.Playing ? "||" : "▶";

    protected BotButtonStyle GetPauseUnpauseStyle() =>
        _music_client.playbackStatus == PlaybackStatus.Paused ? BotButtonStyle.Danger : BotButtonStyle.Primary;

    protected BotButtonStyle GetForwardButtonStyle() =>
        _music_client.music_queue.Count > 0 ? BotButtonStyle.Primary : BotButtonStyle.Secondary;

    protected BotButtonStyle GetRepeatButtonStyle() =>
        _music_client.on_repeat ? BotButtonStyle.Success : BotButtonStyle.Secondary;

    public void DisposeAutoUpdateTimer() => _autoUpdateTimer.Dispose();
}
