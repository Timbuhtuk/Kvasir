using Application;
using Application.Services;
using Logging;
using Discord.WebSocket;
using Entities.Enums;

namespace CS_Discord_Bot.Handlers;

[LogCategory(LogCategory.Component)]
public class ComponentHandler
{
    protected readonly DiscordSocketClient _client;
    protected readonly GuildService _guildService;

    public ComponentHandler(DiscordSocketClient client, GuildService guildService)
    {
        _client = client;
        _guildService = guildService;
    }

    public Task RegisterComponentsAsync()
    {
        _client.InteractionCreated += HandleComponentAsync;
        Logger.AddLog("Component handler registered");
        return Task.CompletedTask;
    }

    protected async Task HandleComponentAsync(SocketInteraction interaction)
    {
        // Устанавливаем контекст гильдии
        if (interaction.GuildId.HasValue)
        {
            var guild = _client.GetGuild(interaction.GuildId.Value);
            LogContext.SetGuild(interaction.GuildId.Value, guild?.Name);
        }

        try
        {
            if (interaction is SocketMessageComponent component)
            {
                MusicClientService? music_client = _guildService.GetMusicClient(interaction.GuildId ?? 0);
                if (music_client != null)
                {
                    music_client.music_view.HandleComponent(component);
                }
            }
        }
        finally
        {
            LogContext.Clear();
        }
    }
}
