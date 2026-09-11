using Application.Services;
using CS_Discord_Bot.Connectors;
using Logging;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;

namespace CS_Discord_Bot.Handlers;

/// <summary>
/// Handles message create and delete events for music view re-rendering
/// </summary>
public class MessageEventHandler(GuildService guildService) : IMessageCreateGatewayHandler, IMessageDeleteGatewayHandler
{
    public async ValueTask HandleAsync(Message message)
    {
        // Only handle guild messages
        if (message.GuildId == null)
            return;

        // Get music client for this guild
        var musicClient = guildService.GetMusicClient(message.GuildId.Value);
        if (musicClient?.music_view == null)
            return;

        // Handle message received
        await musicClient.music_view.HandleMessageReceivedAsync(message.ToBotMessage());
    }

    public async ValueTask HandleAsync(MessageDeleteEventArgs args)
    {
        // Only handle guild messages
        if (args.GuildId == null)
            return;

        // Get music client for this guild
        var musicClient = guildService.GetMusicClient(args.GuildId.Value);
        if (musicClient?.music_view == null)
            return;

        // Handle message deleted
        await musicClient.music_view.HandleMessageDeletedAsync(args.ToBotMessageDeleted());
    }
}
