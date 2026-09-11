using Microsoft.Extensions.Configuration;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace CS_Discord_Bot.Commands;

/// <summary>
/// Common commands for bot
/// </summary>
public class CommonCommands(IConfiguration configuration) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("help", "Show help message")]
    public async Task HelpAsync()
    {
        var helpMessage = configuration.GetSection("help_client")["help_message"] ?? "Help message not configured";
        await RespondAsync(InteractionCallback.Message(helpMessage));
    }
}
