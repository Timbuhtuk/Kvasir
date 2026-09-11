using Application.Interfaces;
using Application.Models;
using NetCord;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using NetCord.Logging;
using NetCord.Rest;

namespace CS_Discord_Bot.Connectors;

/// <summary>
/// The only place where application ports are translated to NetCord.
/// </summary>
public sealed class NetCordBotConnector(GatewayClient gatewayClient, RestClient restClient) : IBotConnector
{
    public IReadOnlyCollection<BotGuild> Guilds => gatewayClient.Cache.Guilds.Values
        .Select(guild => new BotGuild(guild.Id, guild.Name))
        .ToList();

    public ulong CurrentUserId => gatewayClient.Cache.User?.Id ?? 0;

    public async Task<IBotVoiceConnection> ConnectVoiceAsync(
        BotVoiceChannel channel,
        CancellationToken cancellationToken = default)
    {
        VoiceClient voiceClient = await gatewayClient.JoinVoiceChannelAsync(
            channel.GuildId,
            channel.Id,
            new VoiceClientConfiguration
            {
                Logger = new ConsoleLogger(),
            });

        await voiceClient.StartAsync(cancellationToken);
        await voiceClient.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone));
        return new NetCordVoiceConnection(voiceClient);
    }

    public async Task<bool> IsTextChannelAsync(
        ulong guildId,
        ulong channelId,
        CancellationToken cancellationToken = default)
    {
        Channel channel = await restClient.GetChannelAsync(channelId, cancellationToken: cancellationToken);
        return channel is TextGuildChannel textChannel && textChannel.GuildId == guildId;
    }

    public async Task<BotMessage> SendMessageAsync(
        ulong channelId,
        BotMessageContent message,
        CancellationToken cancellationToken = default)
    {
        Channel channel = await restClient.GetChannelAsync(channelId, cancellationToken: cancellationToken);
        if (channel is not TextGuildChannel textChannel)
            throw new InvalidOperationException($"Channel {channelId} is not a guild text channel");

        RestMessage created = await textChannel.SendMessageAsync(
            new MessageProperties().WithContent(message.Content),
            cancellationToken: cancellationToken);

        return new BotMessage(created.Id, channelId, textChannel.GuildId, created.Author.Id, created.Content);
    }

    public async Task UpdateMessageAsync(
        ulong channelId,
        ulong messageId,
        BotMessageContent message,
        CancellationToken cancellationToken = default)
    {
        List<IMessageComponentProperties> components = ConvertComponents(message.Components);
        EmbedProperties[] embeds = message.Embed == null
            ? Array.Empty<EmbedProperties>()
            : new[] { ConvertEmbed(message.Embed) };

        await restClient.ModifyMessageAsync(channelId, messageId, options =>
        {
            options.Content = message.Content;
            options.Components = components;
            options.Embeds = embeds;
        }, cancellationToken: cancellationToken);
    }

    public Task DeleteMessageAsync(
        ulong channelId,
        ulong messageId,
        CancellationToken cancellationToken = default) =>
        restClient.DeleteMessageAsync(channelId, messageId, cancellationToken: cancellationToken);

    private static EmbedProperties ConvertEmbed(BotEmbed embed) =>
        new EmbedProperties()
            .WithDescription(embed.Description)
            .WithColor(new Color(embed.Color))
            .WithAuthor(new EmbedAuthorProperties()
                .WithName(embed.AuthorName)
                .WithIconUrl(embed.AuthorIconUrl ?? ""));

    private static List<IMessageComponentProperties> ConvertComponents(IReadOnlyCollection<BotComponent>? components)
    {
        List<IMessageComponentProperties> result = new();
        if (components == null)
            return result;

        foreach (BotComponent component in components)
        {
            switch (component)
            {
                case BotComponentRow row:
                    List<ButtonProperties> buttons = row.Components
                        .OfType<BotButton>()
                        .Select(ConvertButton)
                        .ToList();
                    result.Add(new ActionRowProperties(buttons));
                    break;
                case BotSelect select:
                    List<StringMenuSelectOptionProperties> options = select.Options
                        .Select(option => new StringMenuSelectOptionProperties(option.Label, option.Value))
                        .ToList();
                    result.Add(new StringMenuProperties(select.Id, options) { Placeholder = select.Placeholder });
                    break;
            }
        }

        return result;
    }

    private static ButtonProperties ConvertButton(BotButton button) =>
        new(button.Id, button.Label, ConvertButtonStyle(button.Style))
        {
            Disabled = button.Disabled,
        };

    private static ButtonStyle ConvertButtonStyle(BotButtonStyle style) => style switch
    {
        BotButtonStyle.Primary => ButtonStyle.Primary,
        BotButtonStyle.Success => ButtonStyle.Success,
        BotButtonStyle.Danger => ButtonStyle.Danger,
        _ => ButtonStyle.Secondary,
    };

    private sealed class NetCordVoiceConnection(VoiceClient voiceClient) : IBotVoiceConnection
    {
        public Stream CreatePcmStream()
        {
            Stream outputStream = voiceClient.CreateVoiceStream();
            return new OpusEncodeStream(
                outputStream,
                PcmFormat.Short,
                VoiceChannels.Stereo,
                OpusApplication.Audio);
        }

        public ValueTask DisposeAsync()
        {
            voiceClient.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
