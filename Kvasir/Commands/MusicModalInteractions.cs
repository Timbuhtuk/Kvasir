using Application.Services;
using CS_Discord_Bot.Connectors;
using Logging;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ComponentInteractions;

namespace CS_Discord_Bot.Commands;

/// <summary>
/// Handles modal interactions for music player
/// </summary>
public class MusicModalInteractions(GuildService guildService) : ComponentInteractionModule<ModalInteractionContext>
{
    [ComponentInteraction("ADDPLAYLISTMODAL")]
    public async Task AddPlaylistModalAsync()
    {
        if (Context.Guild == null)
            return;

        LogContext.SetGuild(Context.Guild.Id, Context.Guild.Name);

        // Get playlist name from modal components
        // Components are wrapped in Label, so we need to access the inner component
        string? playlistName = null;
        foreach (var component in Context.Components)
        {
            if (component is Label label && label.Component is IInteractiveComponent interactiveComponent)
            {
                if (interactiveComponent.CustomId == "playlist_name")
                {
                    // TextInput components have a Value property
                    var valueProperty = interactiveComponent.GetType().GetProperty("Value");
                    if (valueProperty != null)
                    {
                        playlistName = valueProperty.GetValue(interactiveComponent) as string;
                    }
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(playlistName))
        {
            await Context.Interaction.SendResponseAsync(InteractionCallback.Message(
                new InteractionMessageProperties()
                    .WithContent("Playlist name cannot be empty!")
                    .WithFlags(MessageFlags.Ephemeral)));
            return;
        }

        // Defer response since adding playlist may take time
        await Context.Interaction.SendResponseAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));

        // Get music client and add playlist
        var musicClient = await guildService.GetOrCreateAsync(Context.Guild.ToBotGuild());
        bool success = await musicClient.AddPlaylistAsync(playlistName, Context.User.Id);

        if (success)
        {
            await Context.Interaction.ModifyResponseAsync(message =>
                message.WithContent($"Playlist '{playlistName}' is being added...")
                       .WithFlags(MessageFlags.Ephemeral));
        }
        else
        {
            await Context.Interaction.ModifyResponseAsync(message =>
                message.WithContent("Failed to add playlist. It may already exist or the limit has been reached.")
                       .WithFlags(MessageFlags.Ephemeral));
        }
    }
}
