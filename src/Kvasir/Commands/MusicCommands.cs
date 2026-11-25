using Application.Models;
using Application.Services;
using CS_Discord_Bot.Connectors;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace CS_Discord_Bot.Commands;

public sealed class MusicCommands(GuildService guilds, MusicPanelActions actions) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("play", "Добавить музыку по названию или ссылке")]
    public async Task PlayAsync([SlashCommandParameter(Description = "Название или ссылка")] string query) => await RunAsync("play", query);
    [SlashCommand("pause", "Приостановить воспроизведение")]
    public async Task PauseAsync() => await RunAsync("pause");
    [SlashCommand("resume", "Продолжить воспроизведение")]
    public async Task ResumeAsync() => await RunAsync("resume");
    [SlashCommand("skip", "Голос за пропуск / пропуск DJ")]
    public async Task SkipAsync() => await RunAsync("skip");
    [SlashCommand("clear", "Очистить очередь и остановить трек")]
    public async Task ClearAsync() => await RunAsync("clear");
    [SlashCommand("leave", "Отключиться от голосового канала")]
    public async Task LeaveAsync() => await RunAsync("leave");
    [SlashCommand("anchor", "Разместить музыкальную панель в этом канале")]
    public async Task AnchorAsync() => await RunAsync("anchor");
    [SlashCommand("panel", "Открыть личную музыкальную панель")]
    public async Task PanelAsync() => await RunAsync("panel");
    private async Task RunAsync(string command, string? query = null)
    {
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        if (Context.Guild == null) { await MusicPanelActions.ReplyAsync(Context.Interaction, MusicViewService.Notice("Доступно на сервере.")); return; }
        try
        {
            var music = await guilds.GetOrCreateAsync(Context.Guild.ToBotGuild()); MusicActor actor = actions.Actor(Context.Interaction);
            OperationResult result = OperationResult.Ok("");
            switch (command)
            {
                case "anchor":
                    if (!actor.IsManager) result = OperationResult.Fail("Размещать общую панель может администратор сервера.");
                    else { await guilds.AnchorAsync(Context.Guild.ToBotGuild(), Context.Channel.Id, "музыкальная панель"); result = OperationResult.Ok("Панель размещена."); } break;
                case "play":
                    var voice = MusicPanelActions.Voice(Context.Interaction, actor);
                    result = voice == null ? OperationResult.Fail("Войдите в голосовой канал.") : await music.EnqueueAsync(voice, actor, query); break;
                case "pause": result = await music.PauseAsync(actor, true); break;
                case "resume": result = await music.PauseAsync(actor, false); break;
                case "skip": result = await music.SkipAsync(actor, await actions.ListenersAsync(Context.Interaction, actor)); break;
                case "clear": result = await music.StopAsync(actor); break;
                case "leave": result = await music.StopAsync(actor, true); break;
            }
            await MusicPanelActions.ReplyAsync(Context.Interaction, command == "panel" ? await music.music_view.PersonalPanelAsync(actor) : MusicViewService.Notice(result.Message));
        }
        catch (Exception ex) { await Logging.Logger.AddLog("Music command failed", exception: ex); await MusicPanelActions.ReplyAsync(Context.Interaction, MusicViewService.Notice(ex is ArgumentException ? ex.Message : "Операция не выполнена. Проверьте диагностику через /panel.")); }
    }
}
