using Application;
using Application.Services;
using Logging;
using CS_Discord_Bot.music_parts;
using Discord;
using Discord.WebSocket;
using Entities.Enums;
using Microsoft.Extensions.Configuration;

namespace CS_Discord_Bot.Handlers;

[LogCategory(LogCategory.Event)]
public class EventHandler
{
    protected readonly DiscordSocketClient _client;
    protected readonly GuildService _guildService;
    protected readonly IConfiguration _configuration;

    public EventHandler(DiscordSocketClient client, GuildService guildService, IConfiguration configuration)
    {
        _client = client;
        _guildService = guildService;
        _configuration = configuration;
    }

    public Task RegisterEventsAsync()
    {
        _client.MessageDeleted += MessageDeleted;
        _client.MessageReceived += MessageReceived;
        _client.ModalSubmitted += HandleModalAsync;

        Logger.AddLog("Event handler registered");
        return Task.CompletedTask;

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
                    MusicClient? musicClient = _guildService.GetMusicClient(modal.GuildId ?? 0);
                    if (musicClient != null)
                    {
                        Task.Run(() => musicClient.AddPlaylistAsync(playlist_name, modal.User.Id));
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
            MusicClient? music_client = _guildService.GetMusicClient(channel?.GuildId ?? 0);
            IMessage message = await cacheable1.GetOrDownloadAsync();
            if (music_client != null)
            {
                if (message != null && message.Id == music_client.view_message?.Id)
                {
                    await music_client.SetViewMessage(null);
                    await music_client.RerenderMusicViewAsync(new_msg: message);
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
            MusicClient? music_client = _guildService.GetMusicClient(guildChannel?.GuildId ?? 0);
            if (music_client != null)
            {
                if (message.Content.StartsWith(_configuration["command_tag"]!))
                {
                    return;
                }
                if (message.Author.Id == _client.CurrentUser.Id && message.Content == ".")
                {
                    await music_client.SetViewMessage(message);
                }
                else
                {
                    await music_client.RerenderMusicViewAsync(new_msg: message);
                }
            }
        }
        finally
        {
            LogContext.Clear();
        }
    }
}
