using Entities.Enums;
using Logging;
using Microsoft.Extensions.Configuration;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using System.Diagnostics;

namespace CS_Discord_Bot.Commands;

/// <summary>
/// Creator commands for bot
/// </summary>
public class CreatorCommands(IConfiguration configuration) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("reboot", "Reboot the bot (owner only)")]
    public async Task<string> RebootAsync()
    {
        string? ownerIdStr = configuration["owner_id"];
        if (ownerIdStr == null)
            return "Owner ID not configured";

        if (!ulong.TryParse(ownerIdStr, out ulong ownerId))
            return "Invalid owner ID configuration";

        if (Context.User.Id != ownerId)
            return "You don't have permission to use this command";

        // Defer response since we're restarting
        await Context.Interaction.SendResponseAsync(InteractionCallback.Message(new InteractionMessageProperties().WithContent("Restarting...")));

        // Restart application
        Program.RestartApplication();

        return "Restarting...";
    }

    [SlashCommand("execute", "Execute a command (owner only)")]
    public async Task<string> ExecuteAsync([SlashCommandParameter(Description = "Command to execute")] string command)
    {
        string? ownerIdStr = configuration["owner_id"];
        if (ownerIdStr == null)
            return "Owner ID not configured";

        if (!ulong.TryParse(ownerIdStr, out ulong ownerId))
            return "Invalid owner ID configuration";

        if (Context.User.Id != ownerId)
            return "You don't have permission to use this command";

        try
        {
            Process.Start(command);
            return $"Executed: {command}";
        }
        catch (Exception ex)
        {
            await Logger.AddLog(ex.Message, LogCategory.General, Microsoft.Extensions.Logging.LogLevel.Error, exception: ex);
            return $"Error executing command: {ex.Message}";
        }
    }
}
