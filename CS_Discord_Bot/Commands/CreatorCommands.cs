using Discord.Commands;
using Entities.Enums;
using Logging;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;

namespace CS_Discord_Bot.Commands;

public class CreatorСommands : ModuleBase<SocketCommandContext>
{
    private readonly IConfiguration _configuration;

    public CreatorСommands(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [Command("reboot", RunMode = RunMode.Sync)]
    [Summary("reboot all program")]
    public async Task Reboot()
    {
        await Context.Message.DeleteAsync();

        string? owner_id_str = _configuration["owner_id"];
        if (owner_id_str != null)
        {
            ulong owner_id = ulong.Parse(owner_id_str);

            if (Context.User.Id == owner_id)
                Program.RestartApplication();
        }
    }

    [Command("execute", RunMode = RunMode.Async)]
    [Summary("reboot all program")]
    public async Task Execute(params string[] values)
    {
        string? owner_id_str = _configuration["owner_id"];
        if (owner_id_str != null)
        {
            ulong owner_id = ulong.Parse(owner_id_str);


            string query = Context.Message.Content.Replace(_configuration["command_tag"]! + "execute ", "");
            if (Context.User.Id == owner_id)
            {
                try
                {
                    Process process = Process.Start(query);
                }
                catch (Exception ex)
                {
                    await Logger.AddLog(ex.Message, LogLevel.ERROR);
                }
            }
        }
    }
}
