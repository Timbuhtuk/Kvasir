using Application.Services;
using Application.Models;
using CS_Discord_Bot.Connectors;
using Logging;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ComponentInteractions;

namespace CS_Discord_Bot.Commands;

/// <summary>
/// Handles string menu interactions for music player
/// </summary>
public class MusicStringMenuInteractions(GuildService guildService) : ComponentInteractionModule<StringMenuInteractionContext>
{
    [ComponentInteraction("SONGS_MENU")]
    public async Task SongsMenuAsync()
    {
        if (Context.Guild == null)
            return;

        LogContext.SetGuild(Context.Guild.Id, Context.Guild.Name);

        var musicClient = await guildService.GetOrCreateAsync(Context.Guild.ToBotGuild());
        var musicView = musicClient.music_view;

        if (musicView == null)
            return;

        // Defer the response
        await Context.Interaction.SendResponseAsync(InteractionCallback.DeferredModifyMessage);

        var selectedValue = Context.SelectedValues.FirstOrDefault();
        if (string.IsNullOrEmpty(selectedValue))
        {
            await musicView.UpdateMusicViewAsync();
            return;
        }

        var currentPlaylist = musicView.current_playlist;

        switch (selectedValue)
        {
            case "run all":
                // Get user's voice channel
                BotVoiceChannel? voiceChannel = GetUserVoiceChannel(Context.Guild.Id, Context.User.Id);
                if (voiceChannel != null)
                {
                    await musicClient.PlayAsync(voiceChannel, playlistId: currentPlaylist.Id);
                }
                break;

            case "delete list":
                // TODO: Implement playlist deletion
                // using (IServiceScope scope = _serviceScopeFactory.CreateScope())
                // {
                //     IPlaylistRepository playlistRepository = scope.ServiceProvider.GetRequiredService<IPlaylistRepository>();
                //     IGuildRepository guildRepository = scope.ServiceProvider.GetRequiredService<IGuildRepository>();
                //     await playlistRepository.RemoveByIdAsync(currentPlaylist.Id);
                //     // Update current playlist
                // }
                break;

            default:
                if (int.TryParse(selectedValue, out int songId))
                {
                    BotVoiceChannel? voiceChannel2 = GetUserVoiceChannel(Context.Guild.Id, Context.User.Id);
                    if (voiceChannel2 != null)
                    {
                        await musicClient.PlayAsync(voiceChannel2, songId: songId);
                    }
                }
                break;
        }

        await musicView.UpdateMusicViewAsync();
    }

    [ComponentInteraction("PLAYLIST_MENU")]
    public async Task PlaylistMenuAsync()
    {
        if (Context.Guild == null)
            return;

        LogContext.SetGuild(Context.Guild.Id, Context.Guild.Name);

        var musicClient = await guildService.GetOrCreateAsync(Context.Guild.ToBotGuild());
        var musicView = musicClient.music_view;

        if (musicView == null)
            return;

        // Defer the response
        await Context.Interaction.SendResponseAsync(InteractionCallback.DeferredModifyMessage);

        var selectedValue = Context.SelectedValues.FirstOrDefault();
        if (string.IsNullOrEmpty(selectedValue))
        {
            await musicView.UpdateMusicViewAsync();
            return;
        }

        if (int.TryParse(selectedValue, out int playlistId))
        {
            if (playlistId == -1)
                musicView.current_playlist = musicClient.playback_history;
            else if (playlistId == -2)
                musicView.current_playlist = musicClient.popular_songs;
            else
            {
                // TODO: Load playlist from database
                // using (IServiceScope scope = _serviceScopeFactory.CreateScope())
                // {
                //     IPlaylistRepository playlistRepository = scope.ServiceProvider.GetRequiredService<IPlaylistRepository>();
                //     Playlist? playlist = await playlistRepository.GetByIdAsync(playlistId);
                //     if (playlist != null)
                //         musicView.current_playlist = playlist;
                // }
            }
        }

        await musicView.UpdateMusicViewAsync();
    }

    private BotVoiceChannel? GetUserVoiceChannel(ulong guildId, ulong userId)
    {
        if (Context.Guild == null)
            return null;

        // Get voice state from guild
        if (Context.Guild.VoiceStates.TryGetValue(userId, out var voiceState))
        {
            if (voiceState.ChannelId.HasValue)
            {
                // Get channel from guild channels
                if (Context.Guild.Channels.TryGetValue(voiceState.ChannelId.Value, out var channel) && channel is VoiceGuildChannel voiceChannel)
                {
                    return voiceChannel.ToBotVoiceChannel();
                }
            }
        }
        return null;
    }
}
