using Application.Models;
using Entities.Models;

namespace Application.Services;

public sealed class MusicQueue
{
    private readonly object _sync = new();
    private readonly List<QueueEntry> _entries = new();
    private Guid? _nextOverride;
    public IReadOnlyList<QueueEntry> Snapshot() { lock (_sync) return _entries.ToArray(); }

    public OperationResult Add(BotVoiceChannel channel, IEnumerable<Song> songs, MusicActor actor, GuildMusicSettings settings)
    {
        lock (_sync)
        {
            List<Song> list = songs.ToList();
            if (_entries.Count + list.Count > 1000) return OperationResult.Fail("В очереди может быть до 1000 треков.");
            if (!actor.IsManager && _entries.Count(e => e.RequestedBy == actor.UserId) + list.Count > settings.PerUserLimit)
                return OperationResult.Fail($"Ваш лимит: {settings.PerUserLimit} треков в очереди. Добавьте меньшую подборку.");
            Guid batch = Guid.NewGuid();
            foreach (Song song in list)
                _entries.Add(new(Guid.NewGuid(), batch, channel, song, actor.UserId, DateTime.UtcNow));
            if (settings.FairQueue) Balance();
            return OperationResult.Ok($"Добавлено треков: {list.Count}.", "TracksAdded", list.Count.ToString());
        }
    }

    public QueueEntry? Take(bool fair, ulong previousUser = 0)
    {
        lock (_sync)
        {
            if (_entries.Count == 0) return null;
            int selected = _nextOverride.HasValue ? _entries.FindIndex(e => e.Id == _nextOverride) : -1;
            _nextOverride = null;
            if (selected < 0) { if (fair) Balance(previousUser); selected = 0; }
            QueueEntry entry = _entries[selected];
            _entries.RemoveAt(selected);
            return entry;
        }
    }

    public OperationResult Edit(Guid id, string action, MusicActor actor, bool isDj)
    {
        lock (_sync)
        {
            int q = _entries.FindIndex(e => e.Id == id);
            if (q < 0) return OperationResult.Fail("Трек уже вышел из очереди. Обновите список.");
            if (!isDj && _entries[q].RequestedBy != actor.UserId) return OperationResult.Fail("Можно менять свои заказы. Чужие доступны DJ.");
            if (action == "remove") _entries.RemoveAt(q);
            else
            {
                if (!isDj) return OperationResult.Fail("Менять общий порядок может DJ.");
                int target = action switch { "next" => 0, "up" => Math.Max(0, q - 1), "down" => Math.Min(_entries.Count - 1, q + 1), _ => q };
                QueueEntry entry = _entries[q]; _entries.RemoveAt(q); _entries.Insert(target, entry);
                if (action == "next") _nextOverride = entry.Id;
            }
            return OperationResult.Ok("Очередь обновлена.");
        }
    }

    public OperationResult Undo(ulong userId)
    {
        lock (_sync)
        {
            QueueEntry? last = _entries.Where(e => e.RequestedBy == userId).MaxBy(e => e.AddedUtc);
            if (last == null) return OperationResult.Fail("Ваших заказов в очереди нет.");
            _entries.RemoveAll(e => e.BatchId == last.BatchId);
            return OperationResult.Ok("Последнее добавление отменено.");
        }
    }
    public void Clear() { lock (_sync) { _entries.Clear(); _nextOverride = null; } }
    public void Shuffle() { lock (_sync) Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_entries)); }
    public void Append(QueueEntry entry) { lock (_sync) _entries.Add(entry with { Id = Guid.NewGuid() }); }

    private void Balance(ulong previousUser = 0)
    {
        List<IGrouping<ulong, QueueEntry>> groups = _entries.GroupBy(e => e.RequestedBy).ToList();
        if (groups.Count > 1 && groups[0].Key == previousUser) { var first = groups[0]; groups.RemoveAt(0); groups.Add(first); }
        List<Queue<QueueEntry>> lanes = groups.Select(g => new Queue<QueueEntry>(g)).ToList();
        _entries.Clear();
        while (lanes.Any(g => g.Count > 0))
            foreach (var lane in lanes)
                if (lane.TryDequeue(out QueueEntry? entry)) _entries.Add(entry);
    }
}
