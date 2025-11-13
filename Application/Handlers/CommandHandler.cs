using Logging;
using Discord.Commands;
using Discord.WebSocket;
using Entities.Enums;
using Microsoft.Extensions.Configuration;
using System.Reflection;

namespace CS_Discord_Bot.Handlers;

[LogCategory(LogCategory.Command)]
public class CommandHandler
{
    protected readonly CommandService _commands;
    protected readonly DiscordSocketClient _client;
    protected readonly IServiceProvider _service_provider;
    protected readonly IConfiguration _configuration;

    public CommandHandler(CommandService commands, DiscordSocketClient client, IServiceProvider provider, IConfiguration configuration)
    {
        _commands = commands;
        _client = client;
        _service_provider = provider;
        _configuration = configuration;
    }

    public async Task RegisterCommandsAsync()
    {
        _client.MessageReceived += HandleCommandAsync;
        IReadOnlyList<ModuleInfo> modules_info = (await _commands.AddModulesAsync(Assembly.GetEntryAssembly(), _service_provider)).ToList();

        await Logger.AddLog($"Modules registered: {string.Join(", ", modules_info.Select(x => x.Name))}");
        await Logger.AddLog($"With commands: {string.Join(", ", modules_info.Select(x => string.Join(", ", x.Commands.Select(y => y.Name))))}");
        await Logger.AddLog("Command handler registered");
    }

    protected async Task HandleCommandAsync(SocketMessage messageParam)
    {
        SocketUserMessage? message = messageParam as SocketUserMessage;
        SocketCommandContext context = new(_client, message);

        if (message == null || message.Author.IsBot) return;

        // Устанавливаем контекст гильдии для всех логов в этом запросе
        var guildChannel = message.Channel as SocketGuildChannel;
        if (guildChannel != null)
        {
            LogContext.SetGuild(guildChannel.Guild.Id, guildChannel.Guild.Name);
        }

        try
        {
            int argPos = 0;
            if (message.HasStringPrefix(_configuration["command_tag"], ref argPos) || message.HasMentionPrefix(_client.CurrentUser, ref argPos))
            {
                IResult result = await _commands.ExecuteAsync(context, argPos, _service_provider);
                if (!result.IsSuccess)
                {
                    // guildId и guildName теперь автоматически берутся из LogContext
                    await Logger.AddLog(result.ErrorReason, LogLevel.ERROR);
                }
                await message.DeleteAsync();
            }
        }
        finally
        {
            // Очищаем контекст после обработки
            LogContext.Clear();
        }
    }
}