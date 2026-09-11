using Application.Models;
using NetCord;
using NetCord.Gateway;

namespace CS_Discord_Bot.Connectors;

public static class NetCordModelMapper
{
    public static BotGuild ToBotGuild(this Guild guild) => new(guild.Id, guild.Name);

    public static BotVoiceChannel ToBotVoiceChannel(this VoiceGuildChannel channel) =>
        new(channel.Id, channel.GuildId, channel.Name);

    public static BotMessage ToBotMessage(this Message message) =>
        new(message.Id, message.ChannelId, message.GuildId, message.Author.Id, message.Content);

    public static BotMessageDeleted ToBotMessageDeleted(this MessageDeleteEventArgs message) =>
        new(message.MessageId, message.ChannelId, message.GuildId);
}
