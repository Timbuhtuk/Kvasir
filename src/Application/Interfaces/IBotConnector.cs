using Application.Models;

namespace Application.Interfaces;

/// <summary>
/// Boundary between bot logic and a concrete chat/voice library.
/// A library replacement should require a new implementation of this interface only.
/// </summary>
public interface IBotConnector
{
    IReadOnlyCollection<BotGuild> Guilds { get; }
    ulong CurrentUserId { get; }

    Task<IBotVoiceConnection> ConnectVoiceAsync(BotVoiceChannel channel, CancellationToken cancellationToken = default);
    Task<bool> IsTextChannelAsync(ulong guildId, ulong channelId, CancellationToken cancellationToken = default);
    Task<BotMessage> SendMessageAsync(ulong channelId, BotMessageContent message, CancellationToken cancellationToken = default);
    Task UpdateMessageAsync(ulong channelId, ulong messageId, BotMessageContent message, CancellationToken cancellationToken = default);
    Task DeleteMessageAsync(ulong channelId, ulong messageId, CancellationToken cancellationToken = default);
}
