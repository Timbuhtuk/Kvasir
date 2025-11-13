using Application.Interfaces;
using Discord;
using Discord.WebSocket;
using Entities.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CS_Discord_Bot.music_parts;

/// <summary>
/// Class represents discord component creator for bot music player
/// </summary>
public class MusicView
{
    protected readonly MusicClient _music_client;
    protected readonly IServiceScopeFactory _serviceScopeFactory;
    protected readonly ulong _guildDiscordId;
    protected readonly IGuildRepository _guildRepository;
    protected readonly IPlaylistRepository _playlistRepository;
    protected Playlist current_playlist;

    public MusicView(
        MusicClient music_client, 
        IServiceScopeFactory serviceScopeFactory, 
        ulong guildDiscordId,
        IGuildRepository guildRepository,
        IPlaylistRepository playlistRepository)
    {
        _music_client = music_client;
        _serviceScopeFactory = serviceScopeFactory;
        _guildDiscordId = guildDiscordId;
        _guildRepository = guildRepository;
        _playlistRepository = playlistRepository;
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
            {
                current_playlist = loadedPlaylist;
            }
        }

        List<SelectMenuOptionBuilder> songs_list_items = new();
        if (current_playlist.Songs.Count > 0)
        {
            if (current_playlist.Id != -1 && current_playlist.Id != -2)
                songs_list_items.Add(new SelectMenuOptionBuilder("run all", "run all"));
            foreach (Song song in current_playlist.Songs)
            {
                songs_list_items.Add(new SelectMenuOptionBuilder(song.Name, song.Id.ToString()));
            }
        }
        if (current_playlist.Id != -1 && current_playlist.Id != -2)
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
        {
            return "X";
        }

        Song song = _music_client.current_song.Value.Value;
        if (!current_playlist.Songs.Select(s => s.Id).Contains(song.Id))
        {
            return "💚";
        }
        else
        {
            return "🤍";
        }
    }

    protected ButtonStyle GetLikeButtonStyle()
    {
        if (_music_client.current_song == null || !_music_client.current_song.HasValue)
        {
            return ButtonStyle.Secondary;
        }

        Song song = _music_client.current_song.Value.Value;
        if (!current_playlist.Songs.Select(s => s.Id).Contains(song.Id))
        {
            return ButtonStyle.Secondary;
        }
        else
        {
            return ButtonStyle.Success;
        }
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
                    {
                        current_playlist = updatedPlaylist;
                    }
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
                        {
                            current_playlist = guild.Playlists.First();
                        }
                        else
                        {
                            current_playlist = _music_client.playback_history;
                        }
                        break;
                    default:
                        await _music_client.PlayAsync((component.User as IGuildUser)?.VoiceChannel, song_id: int.Parse(component.Data.Values.First()));
                        break;
                }
                await _music_client.UpdateMusicViewAsync();
                break;
            case "PLAYLIST_MENU":
                await component.DeferAsync();
                int playlistId = int.Parse(component.Data.Values.First());
                
                // Проверяем виртуальные плейлисты
                if (playlistId == -1)
                {
                    current_playlist = _music_client.playback_history;
                }
                else if (playlistId == -2)
                {
                    current_playlist = _music_client.popular_songs;
                }
                else
                {
                    // Загружаем из БД
                    Playlist? playlist = await _playlistRepository.GetByIdAsync(playlistId);
                    if (playlist != null)
                    {
                        current_playlist = playlist;
                    }
                }

                break;
        }
        await _music_client.UpdateMusicViewAsync();
    }
}
