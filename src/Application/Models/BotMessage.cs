namespace Application.Models;

public sealed record BotMessage(
    ulong Id,
    ulong ChannelId,
    ulong? GuildId,
    ulong AuthorId,
    string Content);

public sealed record BotMessageDeleted(ulong MessageId, ulong ChannelId, ulong? GuildId);
