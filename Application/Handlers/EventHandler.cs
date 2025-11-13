using Application.Interfaces;
using Application.Services;
using Discord;
using Discord.WebSocket;
using Entities.Enums;
using Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CS_Discord_Bot.Handlers;

[LogCategory(LogCategory.Event)]
public class EventHandler
{
    protected readonly DiscordSocketClient _client;
    protected readonly GuildService _guildService;
    protected readonly IConfiguration _configuration;
    protected readonly IServiceProvider _serviceProvider;

    public EventHandler(DiscordSocketClient client, GuildService guildService, IConfiguration configuration, IServiceProvider serviceProvider)
    {
        _client = client;
        _guildService = guildService;
        _configuration = configuration;
        _serviceProvider = serviceProvider;
    }

    public async Task RegisterEventsAsync()
    {
        _client.MessageDeleted += MessageDeleted;
        _client.MessageReceived += MessageReceived;
        _client.ModalSubmitted += HandleModalAsync;

        await Logger.AddLog("Event handler registered");
    }
    protected async Task HandleModalAsync(SocketModal modal)
    {
        // Устанавливаем контекст гильдии
        if (modal.GuildId.HasValue)
        {
            var guild = _client.GetGuild(modal.GuildId.Value);
            LogContext.SetGuild(modal.GuildId.Value, guild?.Name);
        }

        try
        {
            switch (modal.Data.CustomId)
            {
                case "ADDPLAYLISTMODAL":
                    await modal.DeferAsync();
                    List<SocketMessageComponentData> components = modal.Data.Components.ToList();
                    string playlist_name = components.First(x => x.CustomId == "playlist_name").Value;
                    MusicClientService? musicClient = _guildService.GetMusicClient(modal.GuildId ?? 0);
                    if (musicClient != null)
                    {
                        _ = Task.Run(() => musicClient.AddPlaylistAsync(playlist_name, modal.User.Id));
                    }
                    break;
            }
        }
        finally
        {
            LogContext.Clear();
        }
    }
    protected async Task MessageDeleted(Cacheable<IMessage, ulong> cacheable1, Cacheable<IMessageChannel, ulong> cacheable2)
    {
        var channel = await cacheable2.GetOrDownloadAsync() as IGuildChannel;
        if (channel != null)
        {
            LogContext.SetGuild(channel.GuildId, null);
        }

        try
        {
            // Делегируем обработку в MusicViewService для каждой гильдии
            if (channel?.GuildId != null)
            {
                MusicClientService? music_client = _guildService.GetMusicClient(channel.GuildId);
                if (music_client?.music_view != null)
                {
                    await music_client.music_view.HandleMessageDeletedAsync(cacheable1, cacheable2, _client.CurrentUser.Id);
                }
            }
        }
        finally
        {
            LogContext.Clear();
        }
    }

    protected async Task MessageReceived(SocketMessage message)
    {
        if (message == null) return;

        var guildChannel = message.Channel as IGuildChannel;
        if (guildChannel != null)
        {
            LogContext.SetGuild(guildChannel.GuildId, null);
        }

        try
        {
            // Делегируем обработку в MusicViewService для каждой гильдии
            if (guildChannel?.GuildId != null)
            {
                MusicClientService? music_client = _guildService.GetMusicClient(guildChannel.GuildId);
                if (music_client?.music_view != null)
                {
                    await music_client.music_view.HandleMessageReceivedAsync(message, _client.CurrentUser.Id, _configuration["command_tag"]!);
                }
            }
        }
        finally
        {
            LogContext.Clear();
        }
    }

}
