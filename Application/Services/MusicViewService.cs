using Application.Interfaces;
using Discord;
using Discord.WebSocket;
using Entities.Models;
using Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Application.Services;

/// <summary>
/// Class represents discord component creator for bot music player
/// </summary>
public class MusicViewService
{
    protected readonly MusicClientService _music_client;
    protected readonly IServiceScopeFactory _serviceScopeFactory;
    protected readonly ulong _guildDiscordId;
    protected readonly IGuildRepository _guildRepository;
    protected readonly IPlaylistRepository _playlistRepository;
    protected readonly DiscordSocketClient _discordClient;
    protected Playlist current_playlist;
    public IMessage? view_message;
    protected readonly SemaphoreSlim _semaphoreSlim = new(1, 1);
    private readonly HashSet<ulong> _deletedMessageIds = new();

    public MusicViewService(
        MusicClientService music_client,
        IServiceScopeFactory serviceScopeFactory,
        ulong guildDiscordId,
        IGuildRepository guildRepository,
        IPlaylistRepository playlistRepository,
        DiscordSocketClient discordClient)
    {
        _music_client = music_client;
        _serviceScopeFactory = serviceScopeFactory;
        _guildDiscordId = guildDiscordId;
        _guildRepository = guildRepository;
        _playlistRepository = playlistRepository;
        _discordClient = discordClient;
        // По умолчанию устанавливаем History плейлист
        current_playlist = music_client.playback_history;
    }


    public async Task<MessageComponent> CreateComponent()
    {
        ComponentBuilder builder = new();

        // Загружаем текущий плейлист с песнями, если это не виртуальный плейлист
        if (current_playlist.Id > 0)
        {
            Playlist? loadedPlaylist = await _playlistRepository.GetByIdAsync(current_playlist.Id);
            if (loadedPlaylist != null)
                current_playlist = loadedPlaylist;
        }

        List<SelectMenuOptionBuilder> songs_list_items = new();
        if (current_playlist.Songs.Count > 0)
        {
            if (current_playlist.Id is not (-1) and not (-2))
                songs_list_items.Add(new SelectMenuOptionBuilder("run all", "run all"));
            foreach (Song song in current_playlist.Songs)
            {
                songs_list_items.Add(new SelectMenuOptionBuilder(song.Name, song.Id.ToString()));
            }
        }
        if (current_playlist.Id is not (-1) and not (-2))
            songs_list_items.Add(new SelectMenuOptionBuilder("delete list", "delete list"));

        // Получаем плейлисты из БД
        List<SelectMenuOptionBuilder> playlists_list_items = new();
        Guild? guild = await _guildRepository.GetByDiscordIdWithPlaylistsAsync(_guildDiscordId);

        if (guild != null)
        {
            List<Playlist> playlists = guild.Playlists.ToList();
            // Добавляем виртуальные плейлисты
            playlists.Add(_music_client.playback_history);
            playlists.Add(_music_client.popular_songs);

            for (int q = playlists.Count - 1; q >= 0; q--)
            {
                playlists_list_items.Add(new SelectMenuOptionBuilder(playlists[q].Name, playlists[q].Id.ToString()));
            }
        }
        else
        {
            // Если гильдия не найдена, добавляем только виртуальные плейлисты
            playlists_list_items.Add(new SelectMenuOptionBuilder(_music_client.playback_history.Name, _music_client.playback_history.Id.ToString()));
            playlists_list_items.Add(new SelectMenuOptionBuilder(_music_client.popular_songs.Name, _music_client.popular_songs.Id.ToString()));
        }



        builder.WithButton(GetLikeButtonLabel(), "LIKE", GetLikeButtonStyle(), disabled: _music_client.current_song == null || !_music_client.current_song.HasValue || current_playlist.Id == -1);
        builder.WithButton(GetPauseUnpauseLabel(), "PAUSE_UNPAUSE", GetPauseUnpauseStyle());
        builder.WithButton("▶▶|", "FORWARD", GetForwardButtonStyle());
        builder.WithButton(GetRepeatButtonLabel(), "REPEAT", GetRepeatButtonStyle());
        builder.WithButton(GetAddPlaylistlabel(), "ADDPLAYLIST", GetAddPlaylistStyle());




        if (playlists_list_items.Count > 0)
            builder.WithSelectMenu("PLAYLIST_MENU", playlists_list_items, row: 1, placeholder: current_playlist.Name);

        if (songs_list_items.Count > 0)
            builder.WithSelectMenu("SONGS_MENU", songs_list_items, row: 2, placeholder: "playlist content");


        return builder.Build();

    }

    protected string GetAddPlaylistlabel()
    {
        return "+new playlist";
    }
    protected ButtonStyle GetAddPlaylistStyle()
    {
        return ButtonStyle.Success;
    }

    protected string GetLikeButtonLabel()
    {
        if (_music_client.current_song == null || !_music_client.current_song.HasValue)
            return "X";

        Song song = _music_client.current_song.Value.Value;
        if (!current_playlist.Songs.Select(s => s.Id).Contains(song.Id))
            return "💚";
        else
            return "🤍";
    }
    protected ButtonStyle GetLikeButtonStyle()
    {
        if (_music_client.current_song == null || !_music_client.current_song.HasValue)
            return ButtonStyle.Secondary;

        Song song = _music_client.current_song.Value.Value;
        if (!current_playlist.Songs.Select(s => s.Id).Contains(song.Id))
            return ButtonStyle.Secondary;
        else
            return ButtonStyle.Success;
    }

    protected string GetPauseUnpauseLabel()
    {
        return _music_client.is_playing ? "||" : "▶";
    }
    protected ButtonStyle GetPauseUnpauseStyle()
    {
        return _music_client.is_paused ? ButtonStyle.Danger : ButtonStyle.Primary;
    }

    protected ButtonStyle GetForwardButtonStyle()
    {
        return _music_client.music_queue.Count > 0 ? ButtonStyle.Primary : ButtonStyle.Secondary;
    }

    protected string GetRepeatButtonLabel()
    {
        return "⟳";
    }
    protected ButtonStyle GetRepeatButtonStyle()
    {
        return _music_client.on_repeat ? ButtonStyle.Success : ButtonStyle.Secondary;
    }


    public async Task HandleComponent(SocketMessageComponent component)
    {
        switch (component.Data.CustomId)
        {
            case "LIKE":
                await component.DeferAsync();
                bool toggle_result = await _music_client.ToggleMusicLikeAsync(current_playlist.Id);
                if (!toggle_result)
                {
                    await component.FollowupAsync(embed: new EmbedBuilder().WithDescription("limit of saved music is 23 tracks").WithColor(Color.Orange).Build(), ephemeral: true);
                }
                else
                {
                    await _music_client.UpdateLikedMusicAsync();
                    // Загружаем обновленный плейлист из БД
                    Playlist? updatedPlaylist = await _playlistRepository.GetByIdAsync(current_playlist.Id);
                    if (updatedPlaylist != null)
                        current_playlist = updatedPlaylist;
                }


                break;
            case "ADDPLAYLIST":
                Modal mb = new ModalBuilder()
                .WithTitle("Add playlist")
                .WithCustomId("ADDPLAYLISTMODAL")
                .AddTextInput(label: "Enter name for playlist or Url to playlist:", customId: "playlist_name", placeholder: "playlist name or url")
                .Build();
                await component.RespondWithModalAsync(mb);
                break;
            case "PAUSE_UNPAUSE":
                await component.DeferAsync();
                await _music_client.TogglePauseAsync();

                break;
            case "FORWARD":
                await component.DeferAsync();
                await _music_client.SkipAsync();

                break;
            case "REPEAT":
                await component.DeferAsync();
                await _music_client.ToggleRepeatAsync();

                break;
            case "SONGS_MENU":
                await component.DeferAsync();
                switch (component.Data.Values.First())
                {
                    case "run all":
                        await _music_client.PlayAsync((component.User as IGuildUser)?.VoiceChannel, playlist_id: current_playlist.Id);
                        break;
                    case "delete list":
                        await _music_client.RemovePlaylistAsync(current_playlist.Id);
                        // Устанавливаем первый доступный плейлист
                        Guild? guild = await _guildRepository.GetByDiscordIdWithPlaylistsAsync(_guildDiscordId);
                        if (guild != null && guild.Playlists.Any())
                            current_playlist = guild.Playlists.First();
                        else
                            current_playlist = _music_client.playback_history;
                        break;
                    default:
                        await _music_client.PlayAsync((component.User as IGuildUser)?.VoiceChannel, song_id: int.Parse(component.Data.Values.First()));
                        break;
                }
                await UpdateMusicViewAsync();
                break;
            case "PLAYLIST_MENU":
                await component.DeferAsync();
                int playlistId = int.Parse(component.Data.Values.First());

                // Проверяем виртуальные плейлисты
                if (playlistId == -1)
                    current_playlist = _music_client.playback_history;
                else if (playlistId == -2)
                    current_playlist = _music_client.popular_songs;
                else
                {
                    // Загружаем из БД
                    Playlist? playlist = await _playlistRepository.GetByIdAsync(playlistId);
                    if (playlist != null)
                        current_playlist = playlist;
                }

                break;
        }
        await UpdateMusicViewAsync();
    }

    public async Task RerenderMusicViewAsync(IMessageChannel? channel = null, IMessage? new_msg = null)
    {
        await Logger.AddLog($"[RerenderMusicViewAsync] Method called. channel: {channel?.Id}, new_msg: {new_msg?.Id}", LogLevel.Debug);

        await _semaphoreSlim.WaitAsync();
        await Logger.AddLog("[RerenderMusicViewAsync] Semaphore acquired", LogLevel.Debug);

        try
        {
            if (view_message != null)
            {
                await Logger.AddLog($"[RerenderMusicViewAsync] Existing view_message found. ID: {view_message.Id}, Channel: {view_message.Channel.Id}", LogLevel.Debug);

                ulong messageId = view_message.Id;
                IMessageChannel messageChannel = view_message.Channel;

                try
                {
                    IMessage? existingMessage = await messageChannel.GetMessageAsync(messageId);
                    if (existingMessage != null)
                    {
                        _deletedMessageIds.Add(messageId);
                        await view_message.DeleteAsync();
                        await Logger.AddLog("[RerenderMusicViewAsync] View message deleted successfully", LogLevel.Debug);
                        
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(5000);
                            _deletedMessageIds.Remove(messageId);
                        });
                    }
                    else
                    {
                        await Logger.AddLog($"[RerenderMusicViewAsync] View message {messageId} no longer exists, skipping deletion", LogLevel.Debug);
                    }
                }
                catch (Discord.Net.HttpException httpEx) when (httpEx.DiscordCode == DiscordErrorCode.UnknownMessage) // Unknown Message
                {
                    await Logger.AddLog($"[RerenderMusicViewAsync] View message {messageId} already deleted (Unknown Message), skipping", LogLevel.Debug);
                }
                catch (Exception e)
                {
                    await Logger.AddLog($"[RerenderMusicViewAsync] Failed to delete view message: {e.Message}", LogLevel.Warning);
                }
                
                view_message = null;
                await Logger.AddLog("[RerenderMusicViewAsync] view_message set to null", LogLevel.Debug);
            }
            else
            {
                await Logger.AddLog("[RerenderMusicViewAsync] No existing view_message found", LogLevel.Debug);
            }

            await Logger.AddLog($"[RerenderMusicViewAsync] Fetching guild from DB. GuildDiscordId: {_guildDiscordId}", LogLevel.Debug);
            Guild? guild = await _guildRepository.GetByDiscordIdAsync(_guildDiscordId);

            if (guild?.Anchor != null)
            {
                await Logger.AddLog($"[RerenderMusicViewAsync] Guild found with Anchor: {guild.Anchor.Value}", LogLevel.Debug);
                IMessageChannel current_channel = (await _discordClient.GetChannelAsync(guild.Anchor.Value) as IMessageChannel)!;
                await Logger.AddLog($"[RerenderMusicViewAsync] Anchor channel retrieved. Channel ID: {current_channel.Id}", LogLevel.Debug);
                view_message = await current_channel.SendMessageAsync(text: ".", allowedMentions: AllowedMentions.None);
                await Logger.AddLog($"[RerenderMusicViewAsync] New view message created in anchor channel. Message ID: {view_message.Id}", LogLevel.Debug);
            }
            else if (channel != null)
            {
                await Logger.AddLog($"[RerenderMusicViewAsync] No anchor found, using provided channel. Channel ID: {channel.Id}", LogLevel.Debug);
                view_message = await channel.SendMessageAsync(text: ".", allowedMentions: AllowedMentions.None);
                await Logger.AddLog($"[RerenderMusicViewAsync] New view message created in provided channel. Message ID: {view_message.Id}", LogLevel.Debug);
            }
            else
            {
                await Logger.AddLog("[RerenderMusicViewAsync] No anchor and no channel provided. Cannot create view message", LogLevel.Warning);
            }
        }
        catch (Exception e)
        {
            await Logger.AddLog($"[RerenderMusicViewAsync] Exception occurred: {e.Message}\nStackTrace: {e.StackTrace}", LogLevel.Error);
        }
        finally
        {
            _semaphoreSlim.Release();
            await Logger.AddLog("[RerenderMusicViewAsync] Semaphore released", LogLevel.Debug);

            await Logger.AddLog("[RerenderMusicViewAsync] Calling UpdateMusicViewAsync", LogLevel.Debug);
            await UpdateMusicViewAsync();
            await Logger.AddLog("[RerenderMusicViewAsync] UpdateMusicViewAsync completed", LogLevel.Debug);
        }
    }
    public async Task<bool> UpdateMusicViewAsync()
    {
        await _semaphoreSlim.WaitAsync();
        await Logger.AddLog("View update called");

        Discord.Embed? embed = null;
        if (_music_client.music_queue.Count > 0 || _music_client.current_song != null)
        {
            embed = new EmbedBuilder()
                .WithDescription(RenderQueue())
                .WithColor(Color.Orange)
                .WithAuthor("---LINE--------------------------------------------------",
                _music_client.is_playing ? "https://media0.giphy.com/media/v1.Y2lkPTc5MGI3NjExMHpuNm8ycXdiaTRkem81ZHN6M2w5MDdibnVrNmZ3MHhxNjIwa3VyNCZlcD12MV9pbnRlcm5hbF9naWZfYnlfaWQmY3Q9cw/vJHNq9tziq9C3HMrIB/giphy.gif" : "")
                .Build();
        }

        MessageComponent component = await CreateComponent();
        try
        {
            if (view_message == null)
                return false;
            await (view_message as IUserMessage)!.ModifyAsync(msg => { msg.Components = component; msg.Embed = embed; msg.Content = ""; });
        }
        catch (Exception)
        {
            await Logger.AddLog("View update failure", LogLevel.Warning);
        }
        finally
        {
            _semaphoreSlim.Release();
        }
        return true;
    }
    public async Task SetViewMessage(SocketMessage? msg)
    {
        view_message = msg;
        return;
    }
    public async Task DeleteViewMessageAsync()
    {
        if (view_message != null)
        {
            ulong messageId = view_message.Id;
            _deletedMessageIds.Add(messageId);
            await view_message.DeleteAsync();
            view_message = null;

            _ = Task.Run(async () =>
            {
                await Task.Delay(5000);
                _deletedMessageIds.Remove(messageId);
            });
        }
    }


    /// <summary>
    /// Обрабатывает событие удаления сообщения. Решает, нужно ли ререндерить вьюшку.
    /// </summary>
    public async Task HandleMessageDeletedAsync(Cacheable<IMessage, ulong> cacheableMessage, Cacheable<IMessageChannel, ulong> cacheableChannel, ulong botUserId)
    {
        var channel = await cacheableChannel.GetOrDownloadAsync() as IGuildChannel;
        if (channel?.GuildId == null)
            return;

        // Проверяем, является ли канал anchor каналом для нашей гильдии
        Guild? guild = await _guildRepository.GetByDiscordIdAsync(_guildDiscordId);
        if (guild?.Anchor != channel.Id)
            return;

        // Получаем удаленное сообщение, если оно доступно
        IMessage? deletedMessage = null;
        try
        {
            deletedMessage = await cacheableMessage.GetOrDownloadAsync();
        }
        catch
        {
            // Сообщение уже удалено и недоступно - это нормально
        }

        // Проверяем, удаляем ли мы это сообщение сами - если да, игнорируем
        if (_deletedMessageIds.Contains(cacheableMessage.Id))
        {
            await Logger.AddLog($"[MusicViewService] Ignoring deletion of message we're deleting ourselves. Message ID: {cacheableMessage.Id}", LogLevel.Debug);
            return;
        }

        // Проверяем, является ли удаленное сообщение нашей вьюшкой (от бота с точкой)
        bool isOurViewMessage = false;
        
        if (deletedMessage != null)
        {
            // Если сообщение доступно - проверяем автора и содержимое
            isOurViewMessage = deletedMessage.Author.Id == botUserId && deletedMessage.Content == ".";
            await Logger.AddLog($"[MusicViewService] Deleted message available. Author: {deletedMessage.Author.Id}, Content: '{deletedMessage.Content}', Is our view: {isOurViewMessage}", LogLevel.Debug);
        }
        else
        {
            // Если сообщение недоступно, проверяем, является ли оно текущей вьюшкой
            // Это важно для случая, когда администратор удалил вьюшку вручную
            isOurViewMessage = view_message?.Id == cacheableMessage.Id;
            await Logger.AddLog($"[MusicViewService] Deleted message unavailable. Checking if it's current view. Deleted ID: {cacheableMessage.Id}, Current view ID: {view_message?.Id}, Is our view: {isOurViewMessage}", LogLevel.Debug);
        }

        if (isOurViewMessage)
        {
            // Удалена наша вьюшка (либо мы сами, либо администратор) - ререндерим
            await Logger.AddLog($"[MusicViewService] Our view message was deleted. Message ID: {cacheableMessage.Id}, Current view message ID: {view_message?.Id}. Re-rendering...", LogLevel.Debug);
            
            // Очищаем ссылку на удаленное сообщение
            if (view_message?.Id == cacheableMessage.Id)
                await SetViewMessage(null);
            
            await RerenderMusicViewAsync();
        }
        else
            // Удалено не наше сообщение - игнорируем
            await Logger.AddLog($"[MusicViewService] Ignoring deletion of non-view message in anchor channel. Deleted message ID: {cacheableMessage.Id}", LogLevel.Debug);
    }

    /// <summary>
    /// Обрабатывает событие получения сообщения. Решает, нужно ли ререндерить вьюшку.
    /// </summary>
    public async Task HandleMessageReceivedAsync(SocketMessage message, ulong botUserId, string commandTag)
    {
        if (message == null)
            return;

        var guildChannel = message.Channel as IGuildChannel;
        if (guildChannel?.GuildId == null)
            return;

        // Проверяем, является ли канал anchor каналом для нашей гильдии
        Guild? guild = await _guildRepository.GetByDiscordIdAsync(_guildDiscordId);
        if (guild?.Anchor != guildChannel.Id)
            return;

        // Игнорируем команды
        if (message.Content.StartsWith(commandTag))
            return;

        // Если это сообщение от бота с точкой - это наше сообщение вьюшки, просто устанавливаем его
        if (message.Author.Id == botUserId && message.Content == ".")
        {
            await SetViewMessage(message);
            return;
        }

        // Игнорируем все остальные сообщения от бота
        if (message.Author.Id == botUserId)
            return;

        // Если сообщение от пользователя в anchor канале - ререндерим
        await Logger.AddLog($"[MusicViewService] User message received in anchor channel. Re-rendering...", LogLevel.Debug);
        await RerenderMusicViewAsync();
    }

    protected string RenderQueue()
    {
        if (!_music_client.music_queue.IsEmpty || _music_client.current_song != null || (_music_client.music_queue.Count == 1 && _music_client.on_repeat))
        {
            StringBuilder result = new();
            int offset = _music_client.on_repeat ? 1 : 0;
            List<KeyValuePair<IVoiceChannel, Song>> music_list = _music_client.music_queue.ToList();

            for (int q = music_list.Count - 1; q >= offset; q--)
            {
                string temp = music_list[q].Value.Name;
                result.Append($"{(q + 2 - offset).ToString()} {temp}\n");
            }

            if ((_music_client.is_playing || _music_client.is_paused) && _music_client.current_song != null)
            {
                result.Append($"**1. {_music_client.current_song.Value.Value.Name}**");
            }

            return result.ToString();
        }
        return "";
    }
}
