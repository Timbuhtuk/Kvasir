using Application.Interfaces;
using Application.Models;
using Application.Services;
using CS_Discord_Bot.Connectors;
using Entities.Models;
using Logging;
using Microsoft.Extensions.Configuration;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ComponentInteractions;

namespace CS_Discord_Bot.Commands;

public sealed class MusicPanelActions(GuildService guilds, IMusicLibrary library, PlaylistImportService imports, IMediaCache cache, IConfiguration configuration, IBotConnector connector, RestClient rest)
{
    public MusicActor Actor(Interaction interaction)
    {
        var guild = interaction.Guild;
        if (guild != null) LogContext.SetGuild(guild.Id, guild.Name);
        ulong userId = interaction.User.Id;
        var member = interaction.User as GuildUser;
        IReadOnlyCollection<ulong> roles = member?.RoleIds.ToArray() ?? Array.Empty<ulong>();
        Permissions permissions = 0;
        if (guild != null)
            foreach (ulong id in roles.Append(guild.Id))
                if (guild.Roles.TryGetValue(id, out Role? role)) permissions |= role.Permissions;
        bool manager = guild?.OwnerId == userId || ulong.TryParse(configuration["owner_id"], out ulong owner) && owner == userId
            || (permissions & (Permissions.Administrator | Permissions.ManageGuild)) != 0;
        ulong? channel = guild != null && guild.VoiceStates.TryGetValue(userId, out var voice) ? voice.ChannelId : null;
        return new(userId, channel, manager, roles);
    }
    public static BotVoiceChannel? Voice(Interaction interaction, MusicActor actor)
    {
        if (actor.VoiceChannelId.HasValue && interaction.Guild?.Channels.TryGetValue(actor.VoiceChannelId.Value, out var channel) == true && channel is VoiceGuildChannel voice) return voice.ToBotVoiceChannel();
        return null;
    }
    public async Task<IReadOnlyCollection<ulong>> ListenersAsync(Interaction interaction, MusicActor actor)
    {
        if (interaction.Guild == null) return Array.Empty<ulong>();
        List<ulong> listeners = new();
        foreach (ulong id in interaction.Guild.VoiceStates.Where(v => v.Value.ChannelId == actor.VoiceChannelId && v.Key != connector.CurrentUserId).Select(v => v.Key))
        {
            GuildUser member = interaction.Guild.Users.TryGetValue(id, out GuildUser? cached) ? cached : await rest.GetGuildUserAsync(interaction.Guild.Id, id);
            if (!member.IsBot) listeners.Add(id);
        }
        return listeners;
    }
    public static void Apply(MessageOptions options, BotMessageContent content)
    {
        options.Flags = MessageFlags.IsComponentsV2 | MessageFlags.Ephemeral;
        options.Components = NetCordBotConnector.ConvertComponents(content.Components);
        options.AllowedMentions = AllowedMentionsProperties.None;
    }
    public static Task ReplyAsync(Interaction interaction, BotMessageContent content) => interaction.ModifyResponseAsync(options => Apply(options, content));
    private static ModalProperties Form(string id, string title, params (string Id, string Label, string Placeholder, bool Required)[] fields)
    {
        var modal = new ModalProperties("kvm:" + id, title);
        foreach (var field in fields)
            modal.AddComponents(new LabelProperties(field.Label, new TextInputProperties(field.Id, TextInputStyle.Short).WithPlaceholder(field.Placeholder).WithRequired(field.Required)));
        return modal;
    }
    public async Task ButtonAsync(ButtonInteraction interaction, string action)
    {
        if (interaction.Guild == null) { await interaction.SendResponseAsync(InteractionCallback.Message(new InteractionMessageProperties().WithContent("Доступно на сервере.").WithFlags(MessageFlags.Ephemeral))); return; }
        var music = await guilds.GetOrCreateAsync(interaction.Guild.ToBotGuild()); var actor = Actor(interaction); var view = music.music_view; var session = view.Session(actor.UserId);
        string[] actionParts = action.Split(':');
        action = actionParts[0];
        int? boundPlaylist = action == "delete" && int.TryParse(actionParts.ElementAtOrDefault(1), out int bound) ? bound : null;
        string suffix = session.PlaylistId?.ToString() ?? "0";
        ModalProperties? modal = action switch
        {
            "add" => Form("add", "Добавить музыку", ("query", "Название или ссылка", "Исполнитель — название", true)),
            "create" => Form("create", "Новый плейлист", ("name", "Название", "Моя подборка", true), ("access", "Доступ: личный или общий", "личный", false)),
            "rename" => Form("rename:" + suffix, "Название плейлиста", ("name", "Новое название", "Название", true)),
            "import" => Form("import:" + suffix, "Импорт музыки", ("query", "Ссылка на плейлист или трек", "https://…", true)),
            "find-library" => Form("find:" + (session.Screen == "playlist" ? suffix : "0"), "Поиск в библиотеке", ("query", "Название / исполнитель; пусто — всё", "Поиск", false)),
            "seek" => Form("seek", "Позиция воспроизведения", ("time", "Время от начала", "2:30 или 150", true)),
            "dj" when actor.IsManager => Form("dj", "Роль DJ", ("role", "ID роли; 0 — убрать ограничение", "ID роли из настроек Discord", true)),
            _ => null,
        };
        if (modal != null) { await interaction.SendResponseAsync(InteractionCallback.Modal(modal)); return; }
        bool personal = (interaction.Message.Flags & MessageFlags.Ephemeral) != 0;
        await interaction.SendResponseAsync(personal ? InteractionCallback.DeferredModifyMessage : InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        try
        {
            if (action == "close") { await interaction.DeleteResponseAsync(); return; }
            if (personal)
            {
                string? pending = action switch
                {
                    "play-song" or "play-list" => "Готовим очередь воспроизведения…",
                    "diagnostics" => "Запускаем диагностику…",
                    "cache-clean" when actor.IsManager => "Очищаем кэш аудио…",
                    "cache-repair" when actor.IsManager => "Восстанавливаем кэш аудио…",
                    "logs" when actor.IsManager => "Загружаем журнал…",
                    _ => null,
                };
                if (pending != null) await ReplyAsync(interaction, MusicViewService.Notice("⏳ " + pending));
            }
            OperationResult? result = null;
            if (action is "queue" or "library" or "history" or "player" or "server" or "diagnostics" or "logs" or "import-progress" or "confirm-clear" or "confirm-delete")
            { session.Screen = action; session.Page = 0; session.Search = ""; }
            else switch (action)
            {
                case "page-prev": session.Page--; break;
                case "page-next": session.Page++; break;
                case "playlist-back": session.Screen = "playlist"; break;
                case "cancel-clear": session.Screen = "queue"; break;
                case "pause": result = await music.PauseAsync(actor); break;
                case "skip": result = await music.SkipAsync(actor, await ListenersAsync(interaction, actor)); break;
                case "clear": result = await music.StopAsync(actor); session.Screen = "queue"; break;
                case "leave": result = await music.StopAsync(actor, true); break;
                case "back15": case "forward15": result = await music.SeekAsync(actor, Math.Max(0, music.PositionSeconds + (action == "back15" ? -15 : 15))); break;
                case "q-next": case "q-up": case "q-down": case "q-remove": case "q-undo": case "q-shuffle": result = await music.QueueActionAsync(actor, action[2..], session.QueueEntryId); break;
                case "vote-toggle": result = await music.ConfigureAsync(actor, s => s.VoteSkip = !s.VoteSkip); break;
                case "fair-toggle": result = await music.ConfigureAsync(actor, s => s.FairQueue = !s.FairQueue); break;
                case "play-song": case "play-list":
                    Playlist? playlist = session.PlaylistId.HasValue ? await library.GetAsync(music.GuildId, actor, session.PlaylistId.Value) : null;
                    var voice = Voice(interaction, actor);
                    if (playlist == null || voice == null) result = OperationResult.Fail("Выберите плейлист и войдите в голосовой канал.");
                    else result = await music.EnqueueAsync(voice, actor, songs: playlist.Songs.Where(s => action == "play-list" || s.Id == session.SongId).ToList());
                    break;
                case "share": case "delete": case "p-up": case "p-down": case "p-remove": case "save-current":
                    int? targetPlaylist = action == "delete" ? boundPlaylist : session.PlaylistId;
                    if (!targetPlaylist.HasValue) result = OperationResult.Fail("Выберите плейлист.");
                    else result = await library.EditAsync(music.GuildId, actor, targetPlaylist.Value, action == "save-current" ? "add" : action.StartsWith("p-") ? action[2..] : action, songId: action == "save-current" ? music.Current?.Song.Id : session.SongId);
                    if (action == "delete" && result.Success) { session.Screen = "library"; session.PlaylistId = null; }
                    break;
                case "cache-clean": case "cache-repair":
                    if (!actor.IsManager) result = OperationResult.Fail("Нужны права администратора сервера.");
                    else { using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3)); CacheReport report = await cache.MaintainAsync(action == "cache-repair", timeout.Token); result = OperationResult.Ok($"Кэш: удалено {report.Removed}, восстановлено {report.Repaired}, не удалось восстановить: {report.FailedRepairs}. Открытые файлы сохранены."); }
                    session.Screen = "diagnostics"; break;
                default: result = OperationResult.Fail("Эта кнопка устарела. Откройте панель заново."); break;
            }
            await ReplyAsync(interaction, await view.PersonalPanelAsync(actor, result?.Message));
        }
        catch (Exception ex) { await FailureAsync(interaction, ex); }
    }
    public async Task SelectAsync(StringMenuInteraction interaction, string action)
    {
        await interaction.SendResponseAsync(InteractionCallback.DeferredModifyMessage);
        try
        {
            if (interaction.Guild == null) return;
            var music = await guilds.GetOrCreateAsync(interaction.Guild.ToBotGuild()); var actor = Actor(interaction); var session = music.music_view.Session(actor.UserId);
            string? value = interaction.Data.SelectedValues.FirstOrDefault(); OperationResult? result = null;
            switch (action)
            {
                case "entry": if (Guid.TryParse(value, out Guid entry)) session.QueueEntryId = entry; break;
                case "playlist": if (int.TryParse(value, out int id)) { session.PlaylistId = id; session.Screen = "playlist"; session.Page = 0; session.Search = ""; session.SongId = null; } break;
                case "song": if (int.TryParse(value, out int song)) session.SongId = song; break;
                case "history-song":
                    var voice = Voice(interaction, actor); Song? selected = music.History.FirstOrDefault(s => s.Id.ToString() == value);
                    if (voice != null && selected != null)
                    {
                        await ReplyAsync(interaction, MusicViewService.Notice("⏳ Готовим очередь воспроизведения…"));
                        result = await music.EnqueueAsync(voice, actor, songs: new[] { selected });
                    }
                    else result = OperationResult.Fail("Войдите в голосовой канал.");
                    break;
                case "volume": if (int.TryParse(value, out int volume) && volume is >= 0 and <= 100) result = await music.ConfigureAsync(actor, s => s.Volume = volume, false); break;
                case "repeat": result = await music.SetRepeatAsync(actor, value ?? "off"); break;
                case "limit": if (int.TryParse(value, out int limit) && limit is >= 1 and <= 1000) result = await music.ConfigureAsync(actor, s => s.PerUserLimit = limit); break;
                case "log-filter":
                    if (value == "All" || Enum.TryParse<Entities.Enums.LogCategory>(value, out _)) session.LogFilter = value!;
                    if (actor.IsManager) await ReplyAsync(interaction, MusicViewService.Notice("⏳ Загружаем журнал…"));
                    break;
            }
            await ReplyAsync(interaction, await music.music_view.PersonalPanelAsync(actor, result?.Message));
        }
        catch (Exception ex) { await FailureAsync(interaction, ex); }
    }
    public async Task ModalAsync(ModalInteraction interaction, string operation)
    {
        await interaction.SendResponseAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        try
        {
            if (interaction.Guild == null) return;
            var music = await guilds.GetOrCreateAsync(interaction.Guild.ToBotGuild()); var actor = Actor(interaction); var session = music.music_view.Session(actor.UserId);
            Dictionary<string, string> fields = new();
            foreach (var component in interaction.Data.Components)
                if (component is Label label && label.Component is TextInput input) fields[input.CustomId] = input.Value;
            string Field(string id) => fields.GetValueOrDefault(id, "").Trim();
            string[] parts = operation.Split(':'); string action = parts[0]; int.TryParse(parts.ElementAtOrDefault(1), out int playlistId);
            OperationResult result = OperationResult.Ok("");
            switch (action)
            {
                case "add":
                    var voice = Voice(interaction, actor); result = voice == null ? OperationResult.Fail("Войдите в голосовой канал.") : await music.EnqueueAsync(voice, actor, Field("query")); session.Screen = "queue"; break;
                case "create":
                    Playlist playlist = await library.CreateAsync(music.GuildId, actor, Field("name"), Field("access").Equals("общий", StringComparison.OrdinalIgnoreCase));
                    session.PlaylistId = playlist.Id; session.Screen = "playlist"; session.Page = 0; session.Search = ""; result = OperationResult.Ok("Плейлист создан."); break;
                case "rename": result = await library.EditAsync(music.GuildId, actor, playlistId, "rename", Field("name")); session.PlaylistId = playlistId; session.Screen = "playlist"; break;
                case "import":
                    Playlist? target = await library.GetAsync(music.GuildId, actor, playlistId);
                    result = target != null && (target.AuthorId == actor.UserId || actor.IsManager) ? imports.StartImport(music.GuildId, actor, playlistId, Field("query")) : OperationResult.Fail("Плейлист недоступен для изменения."); session.Screen = "import-progress"; break;
                case "find": session.Search = Field("query"); session.Page = 0; session.Screen = playlistId > 0 ? "playlist" : "library"; if (playlistId > 0) session.PlaylistId = playlistId; break;
                case "seek":
                    string input = Field("time"); double seconds;
                    if (double.TryParse(input, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out seconds)) result = await music.SeekAsync(actor, seconds);
                    else if (TimeSpan.TryParseExact(input, new[] { @"m\:ss", @"mm\:ss", @"h\:mm\:ss", @"hh\:mm\:ss" }, System.Globalization.CultureInfo.InvariantCulture, out TimeSpan time)) result = await music.SeekAsync(actor, time.TotalSeconds);
                    else result = OperationResult.Fail("Формат времени: 2:30 или количество секунд."); session.Screen = "player"; break;
                case "dj":
                    if (!ulong.TryParse(Field("role").Trim('<', '>', '@', '&'), out ulong role) || role != 0 && !interaction.Guild.Roles.ContainsKey(role)) result = OperationResult.Fail("Роль не найдена на этом сервере.");
                    else result = await music.ConfigureAsync(actor, s => s.DjRoleId = role == 0 ? null : role); session.Screen = "server"; break;
            }
            await ReplyAsync(interaction, await music.music_view.PersonalPanelAsync(actor, result.Message));
        }
        catch (Exception ex) { await FailureAsync(interaction, ex); }
    }
    private static async Task FailureAsync(Interaction interaction, Exception ex)
    {
        await Logger.AddLog("Music interaction failed", exception: ex);
        string message = ex is ArgumentException ? ex.Message : ex is OperationCanceledException ? "Операция прервана по времени. Обновите диагностику и повторите действие." : "Операция не выполнена. Проверьте диагностику и попробуйте ещё раз.";
        await ReplyAsync(interaction, MusicViewService.Notice(message));
    }
}

public sealed class MusicButtons(MusicPanelActions actions) : ComponentInteractionModule<ButtonInteractionContext>
{
    [ComponentInteraction("kvb")]
    public Task HandleAsync(string action) => actions.ButtonAsync(Context.Interaction, action);
}
public sealed class MusicSelects(MusicPanelActions actions) : ComponentInteractionModule<StringMenuInteractionContext>
{
    [ComponentInteraction("kvs")]
    public Task HandleAsync(string action) => actions.SelectAsync(Context.Interaction, action);
}
public sealed class MusicModals(MusicPanelActions actions) : ComponentInteractionModule<ModalInteractionContext>
{
    [ComponentInteraction("kvm")]
    public Task HandleAsync(string action) => actions.ModalAsync(Context.Interaction, action);
}
