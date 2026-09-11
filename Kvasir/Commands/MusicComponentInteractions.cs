using Application.Services;
using CS_Discord_Bot.Connectors;
using Logging;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ComponentInteractions;

namespace CS_Discord_Bot.Commands;

/// <summary>
/// Handles button interactions for music player
/// </summary>
public class MusicComponentInteractions(GuildService guildService) : ComponentInteractionModule<ButtonInteractionContext>
{
    [ComponentInteraction("LIKE")]
    public async Task LikeAsync()
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

        // Get current playlist from music view
        var currentPlaylist = musicView.current_playlist;

        await musicClient.ToggleMusicLikeAsync(currentPlaylist.Id);

        // Update the view
        await musicView.UpdateMusicViewAsync();
    }

    [ComponentInteraction("PAUSE_UNPAUSE")]
    public async Task PauseUnpauseAsync()
    {
        if (Context.Guild == null)
            return;

        LogContext.SetGuild(Context.Guild.Id, Context.Guild.Name);

        var musicClient = await guildService.GetOrCreateAsync(Context.Guild.ToBotGuild());
        var musicView = musicClient.music_view;

        // Defer the response
        await Context.Interaction.SendResponseAsync(InteractionCallback.DeferredModifyMessage);

        await musicClient.TogglePauseAsync();

        // Update the view
        if (musicView != null)
            await musicView.UpdateMusicViewAsync();
    }

    [ComponentInteraction("FORWARD")]
    public async Task ForwardAsync()
    {
        if (Context.Guild == null)
            return;

        LogContext.SetGuild(Context.Guild.Id, Context.Guild.Name);

        var musicClient = await guildService.GetOrCreateAsync(Context.Guild.ToBotGuild());
        var musicView = musicClient.music_view;

        // Defer the response
        await Context.Interaction.SendResponseAsync(InteractionCallback.DeferredModifyMessage);

        await musicClient.SkipAsync();

        // Update the view
        if (musicView != null)
            await musicView.UpdateMusicViewAsync();
    }

    [ComponentInteraction("REPEAT")]
    public async Task RepeatAsync()
    {
        if (Context.Guild == null)
            return;

        LogContext.SetGuild(Context.Guild.Id, Context.Guild.Name);

        var musicClient = await guildService.GetOrCreateAsync(Context.Guild.ToBotGuild());
        var musicView = musicClient.music_view;

        // Defer the response
        await Context.Interaction.SendResponseAsync(InteractionCallback.DeferredModifyMessage);

        await musicClient.ToggleRepeatAsync();

        // Update the view
        if (musicView != null)
            await musicView.UpdateMusicViewAsync();
    }

    [ComponentInteraction("ADDPLAYLIST")]
    public async Task AddPlaylistAsync()
    {
        if (Context.Guild == null)
            return;

        LogContext.SetGuild(Context.Guild.Id, Context.Guild.Name);

        // Create modal for adding playlist
        var modal = new ModalProperties("ADDPLAYLISTMODAL", "Add playlist")
            .AddComponents(new LabelProperties("Enter name for playlist or Url to playlist:",
                new TextInputProperties("playlist_name", TextInputStyle.Short)
                    .WithPlaceholder("playlist name or url")));

        await Context.Interaction.SendResponseAsync(InteractionCallback.Modal(modal));
    }
}
