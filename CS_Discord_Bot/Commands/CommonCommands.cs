using Discord;
using Discord.Commands;
using Microsoft.Extensions.Configuration;

namespace CS_Discord_Bot.Commands;

public class CommonCommands : ModuleBase<SocketCommandContext>
{
    private readonly IConfiguration _configuration;

    public CommonCommands(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [Command("help", RunMode = RunMode.Async)]
    [Summary("")]
    [Alias("h", "р")]
    public async Task HelpAsync(/*[Remainder] string query*/)
    {
        Embed embed = new EmbedBuilder().WithDescription(_configuration.GetSection("help_client")["help_message"]).Build();
        await Context.Channel.SendMessageAsync(embed: embed);
    }
}

