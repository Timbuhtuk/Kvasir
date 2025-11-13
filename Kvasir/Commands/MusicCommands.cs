using Application.Services;
using Discord.Commands;


namespace CS_Discord_Bot.Commands;

/// <summary>
/// Class that represents Music commands for bot
/// </summary>
public class MusicCommands : ModuleBase<SocketCommandContext>
{
        public Application.Services.GuildService manager;

        public MusicCommands(Application.Services.GuildService manager)
        {
            this.manager = manager;
        }
        [Command("play", RunMode = RunMode.Async)]
        [Summary("Play the selected song from YouTube")]
        [Alias("p", "з")]
        public async Task PlayAsync([Remainder] string query)
        {
            await manager.PlayAsync(Context, query);
        }
        [Command("play", RunMode = RunMode.Async)]
        [Summary("Play the selected song from YouTube")]
        [Alias("p", "з")]
        public async Task PlayAsync()
        {
            await manager.PlayAsync(Context);
        }
        [Command("pause")]
        [Summary("Pause the current song")]
        [Alias("pa", "зф")]
        public async Task PauseAsync()
        {
            await manager.PauseAsync(Context);
        }
        [Command("resume")]
        [Summary("Resume the current song")]
        [Alias("r", "к")]
        public async Task ResumeAsync()
        {
            await manager.ResumeAsync(Context);
        }
        [Command("skip")]
        [Summary("Skip the current song")]
        [Alias("s", "ы")]
        public async Task SkipAsync()
        {
            await manager.SkipAsync(Context);
        }
        [Command("clear")]
        [Summary("Clear the queue")]
        [Alias("c", "с")]
        public async Task ClearAsync()
        {
            await manager.ClearAsync(Context);
        }
        [Command("leave")]
        [Summary("Disconnect bot from channel")]
        [Alias("l", "д")]
        public async Task LeaveAsync()
        {
            await manager.LeaveAsync(Context);
        }
        [Command("anchor")]
        [Summary("Set current text chat as primary for bot")]
        [Alias("фтсрщк")]
        public async Task AnchorAsync()
        {
            await manager.AnchorAsync(Context);
        }
}