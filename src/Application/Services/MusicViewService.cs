using Application.Interfaces;
using Application.Models;
using Entities.Enums;
using Entities.Models;
using Logging;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;

namespace Application.Services;

public sealed class MusicViewService : IAsyncDisposable
{
    public const int PAGE_SIZE = 10;
    private readonly MusicClientService _music;
    private readonly IServiceScopeFactory _scopes;
    private readonly IBotConnector _connector;
    private readonly SemaphoreSlim _render = new(1, 1);
    private readonly ConcurrentDictionary<ulong, MusicPanelSession> _sessions = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _timer;
    private ulong? _messageId;
    private ulong? _channelId;
    public MusicViewService(MusicClientService music, IServiceScopeFactory scopes, IBotConnector connector)
    {
        _music = music; _scopes = scopes; _connector = connector; _timer = RefreshAsync();
    }
    public MusicPanelSession Session(ulong userId)
    {
        foreach (var entry in _sessions.Where(e => e.Value.TouchedUtc < DateTime.UtcNow.AddHours(-1))) _sessions.TryRemove(entry.Key, out _);
        var session = _sessions.GetOrAdd(userId, _ => new()); session.TouchedUtc = DateTime.UtcNow; return session;
    }
    public static string Text(string? value, int max = 160)
    {
        string text = (value ?? "").Replace("@", "＠").Replace("`", "ˋ").Replace("*", "∗").Replace("_", "＿").Replace("[", "［").Replace("]", "］").Replace("\n", " ").Replace("\r", " ");
        return text[..Math.Min(text.Length, max)];
    }
    private static BotButton B(string action, string label, bool primary = false, bool disabled = false) => new("kvb:" + action, label, primary ? BotButtonStyle.Primary : BotButtonStyle.Secondary, disabled);
    private static BotComponentRow Row(params BotComponent[] components) => new(components);
    private static BotSelect Select(string action, string title, IEnumerable<BotSelectOption> options) => new("kvs:" + action, title, options.ToArray());
    public static BotMessageContent Card(IEnumerable<BotComponent> children) => new("", Components: new BotComponent[] { new BotContainer(children.ToArray(), 0x5865F2) }, ComponentsV2: true);
    public static BotMessageContent Notice(string message) => Card(new BotComponent[] { new BotText(Text(message, 1800)) });

    public BotMessageContent PublicPanel()
    {
        QueueEntry? current = _music.Current; var queue = _music.Queue.Snapshot();
        var settings = _music.Settings;
        string status = _music.Status == PlaybackStatus.Paused ? "Пауза" : current == null ? "Готов к музыке" : _music.Status == PlaybackStatus.None ? "⏳ Подготовка аудио…" : "Сейчас играет";
        string track = current == null ? "Добавьте трек по названию или ссылке." : $"### {Text(current.Song.Name)}\n{Text(current.Song.AuthorName)}\n{Time(_music.PositionSeconds)} / {Time(current.Song.Duration ?? 0)} · заказал <@{current.RequestedBy}>";
        string next = queue.Count == 0 ? "Очередь пуста" : $"Далее: **{Text(queue[0].Song.Name, 80)}** · треков: {queue.Count}";
        return Card(new BotComponent[]
        {
            new BotText($"## Kvasir · {status}\n{track}"), new BotSeparator(), new BotText(next),
            Row(B("pause", _music.Status == PlaybackStatus.Paused ? "▶ Продолжить" : "⏸ Пауза", true, current == null), B("skip", "⏭ Пропуск / голос", disabled: current == null), B("player", "Воспроизведение")),
            Row(B("add", "+ Добавить"), B("queue", "Очередь"), B("library", "Плейлисты"), B("history", "История")),
            Row(B("server", "Режим компании"), B("diagnostics", "Диагностика")),
            new BotText($"-# Громкость {settings.Volume}% · повтор: {RepeatName(_music.Repeat)} · чередование: {(settings.FairQueue ? "вкл" : "выкл")}" + (_music.LastError == null ? "" : "\n⚠ " + _music.LastError))
        });
    }
    public static string Time(double seconds) => TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(seconds) ? seconds : 0)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    private static string RepeatName(string mode) => mode switch { "track" => "трек", "queue" => "очередь", _ => "выкл" };

    public async Task<BotMessageContent> PersonalPanelAsync(MusicActor actor, string? notice = null)
    {
        var session = Session(actor.UserId); List<BotComponent> c = new();
        using var scope = _scopes.CreateScope();
        var library = scope.ServiceProvider.GetRequiredService<IMusicLibrary>();
        switch (session.Screen)
        {
            case "queue":
                var entries = _music.Queue.Snapshot(); session.Page = Page(session.Page, entries.Count);
                var page = entries.Skip(session.Page * PAGE_SIZE).Take(PAGE_SIZE).ToList();
                c.Add(new BotText($"## Очередь · {entries.Count}\n" + (entries.Count == 0 ? "Пока пуста." : string.Join("\n", page.Select((e, q) => $"{session.Page * PAGE_SIZE + q + 1}. **{Text(e.Song.Name, 70)}** · <@{e.RequestedBy}> · ожидание ≈ {Time(WaitFor(entries, e.Id))}")))));
                if (page.Count > 0) c.Add(Select("entry", "Выбрать заказ", page.Select(e => new BotSelectOption(Text(e.Song.Name, 100), e.Id.ToString("N"), "Заказал " + e.RequestedBy, session.QueueEntryId == e.Id))));
                c.Add(Row(B("q-next", "Следующим"), B("q-up", "↑ Выше"), B("q-down", "↓ Ниже"), B("q-remove", "Убрать")));
                c.Add(Row(B("q-undo", "Отменить добавление"), B("q-shuffle", "Перемешать"), B("confirm-clear", "Очистить")));
                AddPages(c, session.Page, entries.Count); break;
            case "library":
                var lists = await library.ListAsync(_music.GuildId, actor, session.Search); session.Page = Page(session.Page, lists.Count);
                c.Add(new BotText($"## Плейлисты · {lists.Count}\n" + (session.Search.Length == 0 ? "Личные подборки и общая библиотека сервера." : "Поиск: " + Text(session.Search))));
                if (lists.Count > 0) c.Add(Select("playlist", "Открыть плейлист", lists.Skip(session.Page * PAGE_SIZE).Take(PAGE_SIZE).Select(p => new BotSelectOption(Text(p.Name, 100), p.Id.ToString(), $"{(p.IsPublic ? "Общий" : "Личный")} · треков: {p.Songs.Count}"))));
                c.Add(Row(B("create", "+ Создать"), B("find-library", "Найти"), B("import-progress", "Прогресс импорта")));
                AddPages(c, session.Page, lists.Count); break;
            case "playlist":
                Playlist? playlist = session.PlaylistId.HasValue ? await library.GetAsync(_music.GuildId, actor, session.PlaylistId.Value) : null;
                if (playlist == null) { c.Add(new BotText("Плейлист недоступен или удалён.")); break; }
                var songs = playlist.Songs.Where(s => s.Name.Contains(session.Search, StringComparison.OrdinalIgnoreCase) || s.AuthorName.Contains(session.Search, StringComparison.OrdinalIgnoreCase)).ToList(); session.Page = Page(session.Page, songs.Count);
                c.Add(new BotText($"## {Text(playlist.Name)}\n{(playlist.IsPublic ? "Общий" : "Личный")} · {songs.Count} треков · автор <@{playlist.AuthorId}>"));
                if (songs.Count > 0) c.Add(Select("song", "Выбрать трек", songs.Skip(session.Page * PAGE_SIZE).Take(PAGE_SIZE).Select(s => new BotSelectOption(Text(s.Name, 100), s.Id.ToString(), Text(s.AuthorName, 100), session.SongId == s.Id))));
                c.Add(Row(B("play-song", "Добавить выбранный"), B("play-list", "Добавить все"), B("find-library", "Найти трек")));
                if (playlist.AuthorId == actor.UserId || actor.IsManager)
                {
                    c.Add(Row(B("rename", "Переименовать"), B("share", playlist.IsPublic ? "Сделать личным" : "Сделать общим"), B("import", "Импорт"), B("confirm-delete", "Удалить")));
                    c.Add(Row(B("p-up", "↑ Трек"), B("p-down", "↓ Трек"), B("p-remove", "Убрать трек"), B("save-current", "+ Текущий трек")));
                }
                AddPages(c, session.Page, songs.Count); break;
            case "history":
                var history = _music.History; session.Page = Page(session.Page, history.Count);
                c.Add(new BotText("## История сервера\nПоследние 50 треков текущего запуска."));
                if (history.Count > 0) c.Add(Select("history-song", "Добавить трек из истории", history.Skip(session.Page * PAGE_SIZE).Take(PAGE_SIZE).Select(s => new BotSelectOption(Text(s.Name, 100), s.Id.ToString()))));
                AddPages(c, session.Page, history.Count); break;
            case "player":
                c.Add(new BotText("## Воспроизведение\nГромкость общая для слушателей. Выравнивание громкости применяется при подготовке кэша через FFmpeg loudnorm."));
                c.Add(Select("volume", "Громкость: " + _music.Settings.Volume + "%", new[] { 0, 25, 50, 75, 100 }.Select(v => new BotSelectOption(v + "%", v.ToString(), Default: v == _music.Settings.Volume))));
                c.Add(Select("repeat", "Режим повтора", new[] { "off", "track", "queue" }.Select(m => new BotSelectOption(RepeatName(m), m, Default: m == _music.Repeat))));
                c.Add(Row(B("back15", "−15 сек"), B("forward15", "+15 сек"), B("seek", "Ко времени…"), B("leave", "Отключить"))); break;
            case "server":
                var settings = _music.Settings;
                c.Add(new BotText($"## Режим компании\nГолосование за пропуск: {(settings.VoteSkip ? "вкл" : "выкл")}\nЧередование участников: {(settings.FairQueue ? "вкл" : "выкл")}\nЛимит участника: {settings.PerUserLimit}\nDJ: {(settings.DjRoleId.HasValue ? $"<@&{settings.DjRoleId}>" : "роль не назначена; управление доступно слушателям")}"));
                if (actor.IsManager)
                {
                    c.Add(Row(B("vote-toggle", "Голосование вкл/выкл"), B("fair-toggle", "Чередование вкл/выкл"), B("dj", "Роль DJ")));
                    c.Add(Select("limit", "Лимит заказов участника", new[] { 3, 5, 10, 25, 50, 1000 }.Select(n => new BotSelectOption(n.ToString(), n.ToString(), Default: n == settings.PerUserLimit))));
                }
                break;
            case "diagnostics":
                if (!actor.IsManager) { c.Add(new BotText("Диагностика доступна администратору сервера.")); break; }
                var checks = await scope.ServiceProvider.GetRequiredService<IMusicDiagnostics>().CheckAsync();
                CacheReport report = await scope.ServiceProvider.GetRequiredService<IMediaCache>().InspectAsync();
                c.Add(new BotText("## Диагностика\n" + string.Join("\n", checks.Select(check => $"{(check.Healthy ? "✓" : "⚠")} **{check.Name}**: {check.Detail}")) + $"\nГолос: {(_music.VoiceChannelId.HasValue ? "подключён" : "не подключён")}\nКэш: {report.Bytes / 1048576d:F1} / {report.LimitBytes / 1048576d:F0} МБ · файлов {report.Files} · повреждённых {report.InvalidFiles}"));
                c.Add(Row(B("diagnostics", "Обновить"), B("cache-clean", "Очистить старое / лимит"), B("cache-repair", "Проверить и восстановить"), B("logs", "Журнал сервера"))); break;
            case "logs":
                if (!actor.IsManager) { c.Add(new BotText("Журнал доступен администратору сервера.")); break; }
                LogCategory? category = Enum.TryParse(session.LogFilter, out LogCategory parsed) ? parsed : null;
                var logs = await Logger.GetGuildLogsAsync(_music.GuildId, category, DateTime.Now.AddDays(-7));
                c.Add(new BotText("## Журнал сервера\n" + (logs.Count == 0 ? "Нет сохранённых записей за 7 дней. Новые записи записываются с небольшой задержкой." : string.Join("\n", logs.TakeLast(8).Select(log => $"`{log.Timestamp:HH:mm:ss}` [{log.Level}] [{log.Category}] {Redact(log.Message)}")))));
                c.Add(Select("log-filter", "Категория журнала", new[] { "All" }.Concat(Enum.GetNames<LogCategory>()).Select(name => new BotSelectOption(name == "All" ? "Все категории" : name, name, Default: name == session.LogFilter))));
                c.Add(Row(B("logs", "Обновить журнал"), B("diagnostics", "Диагностика"))); break;
            case "import-progress":
                ImportProgress? progress = scope.ServiceProvider.GetRequiredService<PlaylistImportService>().GetImport(_music.GuildId, actor.UserId);
                c.Add(new BotText(ImportStatus(progress)));
                c.Add(Row(B("import-progress", "Обновить прогресс"))); break;
            case "confirm-clear": c.Add(new BotText("Очистить всю очередь и остановить текущий трек?")); c.Add(Row(B("clear", "Да, очистить"), B("cancel-clear", "Отмена"))); break;
            case "confirm-delete":
                Playlist? deleting = session.PlaylistId.HasValue ? await library.GetAsync(_music.GuildId, actor, session.PlaylistId.Value) : null;
                c.Add(new BotText(deleting == null ? "Плейлист уже недоступен." : $"Удалить **{Text(deleting.Name)}**? Аудиофайлы останутся в кэше."));
                c.Add(Row(B("delete:" + (session.PlaylistId ?? 0), "Да, удалить", disabled: deleting == null), B("playlist-back", "Отмена"))); break;
        }
        if (!string.IsNullOrWhiteSpace(notice)) c.Insert(0, new BotText("**" + Text(notice, 1500) + "**"));
        c.Add(Row(B("library", "Библиотека"), B("queue", "Очередь"), B("close", "Закрыть")));
        return Card(c);
    }
    private static string ImportStatus(ImportProgress? progress)
    {
        if (progress == null) return "Импорт ещё не запускался.";
        if (!progress.Finished && progress.Total == 0) return "## Импорт\n⏳ Ищем треки для импорта…";

        string status = $"## Импорт · {(progress.Finished ? "завершён" : "выполняется")}";
        if (progress.Total > 0)
        {
            const int width = 12;
            int filled = (int)(width * Math.Clamp((double)progress.Completed / progress.Total, 0, 1));
            status += $"\n`{new string('█', filled)}{new string('░', width - filled)}` · {progress.Completed}/{progress.Total} треков";
        }
        if (progress.Failures.Count > 0)
            status += $"\nОшибок: {progress.Failures.Count}\n" + string.Join("\n", progress.Failures.Take(10).Select(s => "• " + Text(s, 120)));
        return status;
    }
    private double WaitFor(IReadOnlyList<QueueEntry> entries, Guid id)
    {
        double wait = Math.Max(0, (_music.Current?.Song.Duration ?? 0) - _music.PositionSeconds);
        foreach (var entry in entries) { if (entry.Id == id) break; wait += entry.Song.Duration ?? 0; }
        return wait;
    }
    private static string Redact(string value) => Text(System.Text.RegularExpressions.Regex.Replace(value, @"(?i)(token|authorization|secret)\s*[:=]\s*\S+", "$1=[скрыто]"), 180);
    private static int Page(int page, int count) => Math.Clamp(page, 0, Math.Max(0, (count - 1) / PAGE_SIZE));
    private static void AddPages(List<BotComponent> c, int page, int count)
    {
        c.Add(new BotText($"-# Страница {page + 1} / {Math.Max(1, (count + PAGE_SIZE - 1) / PAGE_SIZE)}"));
        c.Add(Row(B("page-prev", "← Назад", disabled: page == 0), B("page-next", "Далее →", disabled: (page + 1) * PAGE_SIZE >= count)));
    }
    public async Task RerenderMusicViewAsync(ulong? channelId = null)
    {
        await _render.WaitAsync();
        try
        {
            if (channelId.HasValue && channelId != _channelId)
            {
                if (_messageId.HasValue && _channelId.HasValue) try { await _connector.DeleteMessageAsync(_channelId.Value, _messageId.Value); } catch (Exception) { }
                _messageId = null; _channelId = channelId;
            }
        }
        finally { _render.Release(); }
        await UpdateMusicViewAsync();
    }
    public async Task<bool> UpdateMusicViewAsync()
    {
        await _render.WaitAsync();
        try
        {
            if (!_channelId.HasValue)
            {
                using var scope = _scopes.CreateScope();
                _channelId = (await scope.ServiceProvider.GetRequiredService<IGuildRepository>().GetByDiscordIdAsync(_music.GuildId))?.Anchor;
            }
            if (!_channelId.HasValue) return false;
            if (!_messageId.HasValue) _messageId = (await _connector.SendMessageAsync(_channelId.Value, PublicPanel())).Id;
            else await _connector.UpdateMessageAsync(_channelId.Value, _messageId.Value, PublicPanel());
            return true;
        }
        catch (Exception ex) { await Logger.AddLog("Music panel update failed", exception: ex); return false; }
        finally { _render.Release(); }
    }
    public async Task HandleMessageDeletedAsync(BotMessageDeleted message)
    {
        if (message.MessageId != _messageId) return;
        _messageId = null; await UpdateMusicViewAsync();
    }
    public Task HandleMessageReceivedAsync(BotMessage message) => Task.CompletedTask;
    private async Task RefreshAsync()
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(20));
        try { while (await timer.WaitForNextTickAsync(_lifetime.Token)) if (_channelId.HasValue && _music.Current != null) await UpdateMusicViewAsync(); }
        catch (OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync() { _lifetime.Cancel(); await _timer; _lifetime.Dispose(); _render.Dispose(); }
}
