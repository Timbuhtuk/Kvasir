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
            },
            cancellationToken: cancellationToken);

        await voiceClient.StartAsync(cancellationToken);
        await voiceClient.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone));
        return new NetCordVoiceConnection(voiceClient, gatewayClient, channel.GuildId);
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
            new MessageProperties
            {
                Content = message.ComponentsV2 ? null : message.Content,
                Components = ConvertComponents(message.Components),
                Flags = message.ComponentsV2 ? MessageFlags.IsComponentsV2 : null,
                AllowedMentions = AllowedMentionsProperties.None,
            },
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
            options.Content = message.ComponentsV2 ? null : message.Content;
            options.Components = components;
            options.AllowedMentions = AllowedMentionsProperties.None;
            if (message.ComponentsV2) options.Flags = MessageFlags.IsComponentsV2;
            else options.Embeds = embeds;
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

    public static List<IMessageComponentProperties> ConvertComponents(IReadOnlyCollection<BotComponent>? components)
    {
        List<IMessageComponentProperties> result = new();
        if (components == null)
            return result;

        foreach (BotComponent component in components)
        {
            switch (component)
            {
                case BotText text:
                    result.Add(new TextDisplayProperties(text.Text));
                    break;
                case BotSeparator:
                    result.Add(new ComponentSeparatorProperties());
                    break;
                case BotContainer container:
                    result.Add(new ComponentContainerProperties(ConvertComponents(container.Components).Cast<IComponentContainerComponentProperties>())
                    {
                        AccentColor = container.AccentColor.HasValue ? new Color(container.AccentColor.Value) : null,
                    });
                    break;
                case BotComponentRow row:
                    List<IActionRowComponentProperties> children = new();
                    foreach (BotComponent child in row.Components)
                        if (child is BotButton button) children.Add(ConvertButton(button));
                    result.Add(new ActionRowProperties(children));
                    break;
                case BotSelect select:
                    List<StringMenuSelectOptionProperties> options = select.Options
                        .Select(option => new StringMenuSelectOptionProperties(option.Label, option.Value) { Description = option.Description, Default = option.Default })
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

    private sealed class NetCordVoiceConnection(VoiceClient voiceClient, GatewayClient gateway, ulong guildId) : IBotVoiceConnection
    {
        private int _audioDisposed;
        public Stream CreatePcmStream()
        {
            Stream outputStream = voiceClient.CreateVoiceStream();
            return new OpusEncodeStream(
                outputStream,
                PcmFormat.Short,
                VoiceChannels.Stereo,
                OpusApplication.Audio);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (gateway.Status == WebSocketStatus.Ready)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await gateway.UpdateVoiceStateAsync(new(guildId, null), cancellationToken: timeout.Token);
                }
            }
            catch (InvalidOperationException) when (gateway.Status != WebSocketStatus.Ready) { }
            finally
            {
                if (Interlocked.Exchange(ref _audioDisposed, 1) == 0) voiceClient.Dispose();
            }
        }
    }
}
