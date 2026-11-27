using System.Text;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace CS_Discord_Bot.Commands;

public class CommonCommands(ApplicationCommandService<ApplicationCommandContext> commands) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("help", "Показать список команд бота")]
    public async Task HelpAsync()
    {
        bool first = true;
        foreach (string page in HelpPages(commands))
        {
            if (first) await RespondAsync(InteractionCallback.Message(page));
            else await FollowupAsync(new InteractionMessageProperties().WithContent(page));
            first = false;
        }
    }

    internal static IEnumerable<string> HelpPages(ApplicationCommandService<ApplicationCommandContext> service)
    {
        var lines = new List<string>();
        foreach (var command in service.GetCommands().Where(command => command.Register))
        {
            if (command is SlashCommandInfo<ApplicationCommandContext> slash)
                lines.Add($"/{slash.Name} — {slash.Description}");
            else if (command is SlashCommandGroupInfo<ApplicationCommandContext> group)
                AddSubcommands(lines, "/" + group.Name, group.SubCommands.Values);
        }

        var page = new StringBuilder();
        foreach (string line in lines.Order(StringComparer.Ordinal))
        {
            if (page.Length + 1 + line.Length > 2000)
            {
                yield return page.ToString();
                page.Clear();
            }
            if (page.Length > 0) page.Append('\n');
            page.Append(line);
        }
        if (page.Length > 0) yield return page.ToString();
    }

    private static void AddSubcommands(List<string> lines, string prefix, IEnumerable<ISubSlashCommandInfo<ApplicationCommandContext>> commands)
    {
        foreach (var command in commands)
        {
            string path = $"{prefix} {command.Name}";
            if (command is SubSlashCommandGroupInfo<ApplicationCommandContext> group)
                AddSubcommands(lines, path, group.SubCommands.Values);
            else
                lines.Add($"{path} — {command.Description}");
        }
    }
}
