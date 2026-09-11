using Application.Services;
using CS_Discord_Bot.Connectors;
using Logging;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace CS_Discord_Bot.Commands;

/// <summary>
/// Class that represents Music commands for bot
/// </summary>
public class MusicCommands(GuildService guildService) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("play", "Play the selected song")]
    public async Task PlayAsync([SlashCommandParameter(Description = "Song name, URL or query to search")] string query)
    {
        if (Context.Guild == null)
        {
            await RespondAsync(InteractionCallback.Message(new InteractionMessageProperties().WithContent("This command can only be used in a server!").WithFlags(MessageFlags.Ephemeral)));
            return;
        }

        LogContext.SetGuild(Context.Guild.Id, Context.Guild.Name);

        // Get user's voice channel
        VoiceGuildChannel? voiceChannel = GetUserVoiceChannel(Context.Guild.Id, Context.User.Id);
        TextGuildChannel? textChannel = Context.Channel as TextGuildChannel;

        if (voiceChannel == null)
        {
            await RespondAsync(InteractionCallback.Message(new InteractionMessageProperties().WithContent("You must be connected to a voice channel!").WithFlags(MessageFlags.Ephemeral)));
            return;
        }

        // Defer response with ephemeral flag since this operation may take time
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));

        // Perform the operation
        MusicClientService musicClient = await guildService.GetOrCreateAsync(Context.Guild.ToBotGuild());
        await musicClient.PlayAsync(voiceChannel.ToBotVoiceChannel(), query, textChannel?.Id);

        // Update the deferred response with ephemeral message (will be visible only to the user)
        await Context.Interaction.ModifyResponseAsync(message => message.WithContent($"Searching for: {query}").WithFlags(MessageFlags.Ephemeral));

        // Delete the ephemeral message after 3 seconds to clean up
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            try
            {
                await Context.Client.Rest.DeleteInteractionResponseAsync(Context.Interaction.ApplicationId, Context.Interaction.Token);
            }
            catch
            {
                // Ignore errors when deleting (message might already be deleted or interaction expired)
            }
        });
    }

    [SlashCommand("pause", "Pause the current song")]
    public async Task PauseAsync()
    {
        if (Context.Guild == null)
        {
            await RespondAsync(InteractionCallback.Message(new InteractionMessageProperties().WithContent("This command can only be used in a server!").WithFlags(MessageFlags.Ephemeral)));
            return;
        }

        LogContext.SetGuild(Context.Guild.Id, Context.Guild.Name);

        MusicClientService musicClient = await guildService.GetOrCreateAsync(Context.Guild.ToBotGuild());
        await musicClient.TogglePauseAsync();

        await RespondAsync(InteractionCallback.Message("Playback paused"));
    }

    [SlashCommand("resume", "Resume the current song")]
    public async Task ResumeAsync()
    {
        if (Context.Guild == null)
        {
            await RespondAsync(InteractionCallback.Message(new InteractionMessageProperties().WithContent("This command can only be used in a server!").WithFlags(MessageFlags.Ephemeral)));
            return;
        }

        LogContext.SetGuild(Context.Guild.Id, Context.Guild.Name);

        MusicClientService musicClient = await guildService.GetOrCreateAsync(Context.Guild.ToBotGuild());
        await musicClient.TogglePauseAsync();

        await RespondAsync(InteractionCallback.Message("Playback resumed"));
    }

    [SlashCommand("skip", "Skip the current song")]
    public async Task SkipAsync()
    {
        if (Context.Guild == null)
        {
            await RespondAsync(InteractionCallback.Message(new InteractionMessageProperties().WithContent("This command can only be used in a server!").WithFlags(MessageFlags.Ephemeral)));
            return;
        }

        LogContext.SetGuild(Context.Guild.Id, Context.Guild.Name);

        MusicClientService musicClient = await guildService.GetOrCreateAsync(Context.Guild.ToBotGuild());
        await musicClient.SkipAsync();

        await RespondAsync(InteractionCallback.Message("Skipped current song"));
    }

    [SlashCommand("clear", "Clear the queue")]
    public async Task ClearAsync()
    {
        if (Context.Guild == null)
        {
            await RespondAsync(InteractionCallback.Message(new InteractionMessageProperties().WithContent("This command can only be used in a server!").WithFlags(MessageFlags.Ephemeral)));
            return;
        }

        LogContext.SetGuild(Context.Guild.Id, Context.Guild.Name);

        MusicClientService musicClient = await guildService.GetOrCreateAsync(Context.Guild.ToBotGuild());
        await musicClient.ClearAsync();

        await RespondAsync(InteractionCallback.Message("Queue cleared!"));
    }

    [SlashCommand("leave", "Disconnect bot from voice channel")]
    public async Task LeaveAsync()
    {
        if (Context.Guild == null)
        {
            await RespondAsync(InteractionCallback.Message(new InteractionMessageProperties().WithContent("This command can only be used in a server!").WithFlags(MessageFlags.Ephemeral)));
            return;
        }

        LogContext.SetGuild(Context.Guild.Id, Context.Guild.Name);

        MusicClientService musicClient = await guildService.GetOrCreateAsync(Context.Guild.ToBotGuild());
        await musicClient.LeaveAsync();

        await RespondAsync(InteractionCallback.Message("Left voice channel"));
    }

    [SlashCommand("anchor", "Set current text channel as primary for bot")]
    public async Task AnchorAsync()
    {
        if (Context.Guild == null)
        {
            await RespondAsync(InteractionCallback.Message(new InteractionMessageProperties().WithContent("This command can only be used in a server!").WithFlags(MessageFlags.Ephemeral)));
            return;
        }

        string channelName = (Context.Channel as TextGuildChannel)?.Name ?? Context.Channel.Id.ToString();
        await guildService.AnchorAsync(Context.Guild.ToBotGuild(), Context.Channel.Id, channelName);

        await RespondAsync(InteractionCallback.Message("Anchor channel set!"));
    }

    private VoiceGuildChannel? GetUserVoiceChannel(ulong guildId, ulong userId)
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
                    return voiceChannel;
                }
            }
        }
        return null;
    }
}
