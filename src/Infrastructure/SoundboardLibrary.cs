using System.Text.Json;
using Application.Interfaces;
using Application.Models;
using Application.Services;
using Kvasir.Configuration;

namespace Infrastructure;

public sealed class SoundboardLibrary : ISoundboardLibrary
{
    private const int MaximumClips = 64, MaximumBytes = 128 * 1024 * 1024;
    private readonly ISoundEffectDecoder _decoder;
    private readonly string _root;
    private readonly SemaphoreSlim _edit = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<Guid, SoundEffectClip> _clips = [];
    private readonly Dictionary<Guid, SoundEffectInfo> _items = [];
    private bool _initialized;
    public SoundboardLibrary(ISoundEffectDecoder decoder) : this(decoder, Path.Combine(ApplicationPaths.DataDirectory, "soundboard")) { }
    public SoundboardLibrary(ISoundEffectDecoder decoder, string directory) { _decoder = decoder; _root = Path.GetFullPath(directory); }
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _edit.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            Directory.CreateDirectory(_root);
            if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0) throw new SoundboardException("Папка саундборда не должна быть ссылкой.");
            foreach (string manifest in Directory.EnumerateFiles(_root, "*.json").Take(MaximumClips))
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(manifest), "N", out Guid id)) continue;
                SoundEffectInfo? item = null;
                try
                {
                    item = JsonSerializer.Deserialize<SoundEffectInfo>(await File.ReadAllTextAsync(manifest, cancellationToken));
                    if (item == null || item.Id != id || string.IsNullOrWhiteSpace(item.Name)) continue;
                    string file = ClipPath(id);
                    var length = new FileInfo(file).Length;
                    if (length <= 0 || length % 4 != 0 || length > 30 * PcmEffectMixer.BytesPerSecond || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Invalid PCM");
                    lock (_sync) if (_clips.Values.Sum(c => (long)c.Pcm.Length) + length > MaximumBytes) throw new IOException("Memory limit");
                    byte[] pcm = await File.ReadAllBytesAsync(file, cancellationToken);
                    item = item with { Duration = pcm.Length / (double)PcmEffectMixer.BytesPerSecond, Ready = true, Problem = null };
                    lock (_sync) { _items[id] = item; _clips[id] = new(item, pcm); }
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                { lock (_sync) _items[id] = new(id, item?.Name ?? "Недоступный эффект", 0, false, "Файл эффекта повреждён или недоступен. Удалите карточку и добавьте файл заново."); }
            }
            _initialized = true;
        }
        finally { _edit.Release(); }
    }
    public IReadOnlyList<SoundEffectInfo> List() { lock (_sync) return _items.Values.OrderBy(c => c.Name).ToArray(); }
    public SoundEffectClip GetReady(Guid id)
    {
        lock (_sync) return _clips.GetValueOrDefault(id) ?? throw new SoundboardException("Эффект не подготовлен или удалён. Добавьте аудиофайл заново.");
    }
    public async Task<SoundEffectInfo> ImportAsync(string file, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        // Decode outside the catalog lock; existing effects remain playable while importing.
        byte[] pcm = await _decoder.DecodeAsync(file, cancellationToken);
        if (pcm.Length == 0 || pcm.Length % 4 != 0 || pcm.Length > 30 * PcmEffectMixer.BytesPerSecond) throw new SoundboardException("Эффект должен содержать от 0 до 30 секунд аудио.");
        string name = Path.GetFileNameWithoutExtension(file);
        if (string.IsNullOrWhiteSpace(name)) name = "Эффект";
        name = name[..Math.Min(name.Length, 80)];
        var item = new SoundEffectInfo(Guid.NewGuid(), name, pcm.Length / (double)PcmEffectMixer.BytesPerSecond);
        await _edit.WaitAsync(cancellationToken);
        string pcmPath = ClipPath(item.Id), manifest = Path.Combine(_root, item.Id.ToString("N") + ".json");
        try
        {
            lock (_sync)
                if (_items.Count >= MaximumClips || _clips.Values.Sum(c => (long)c.Pcm.Length) + pcm.Length > MaximumBytes)
                    throw new SoundboardException("Саундборд заполнен: до 64 эффектов и 128 МБ подготовленного аудио. Удалите ненужные эффекты.");
            await File.WriteAllBytesAsync(pcmPath, pcm, cancellationToken);
            await File.WriteAllTextAsync(manifest + ".tmp", JsonSerializer.Serialize(item), cancellationToken);
            File.Move(manifest + ".tmp", manifest);
            lock (_sync) { _items[item.Id] = item; _clips[item.Id] = new(item, pcm); }
            return item;
        }
        catch
        {
            if (File.Exists(pcmPath)) File.Delete(pcmPath);
            if (File.Exists(manifest + ".tmp")) File.Delete(manifest + ".tmp");
            throw;
        }
        finally { _edit.Release(); }
    }
    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        await _edit.WaitAsync(cancellationToken);
        try
        {
            // IDs generate the only managed paths; the original imported file is never removed.
            string manifest = Path.Combine(_root, id.ToString("N") + ".json");
            if (File.Exists(manifest)) File.Delete(manifest);
            lock (_sync) { _clips.Remove(id); _items.Remove(id); }
            string pcm = ClipPath(id);
            if (File.Exists(pcm)) File.Delete(pcm);
        }
        finally { _edit.Release(); }
    }
    private string ClipPath(Guid id) => Path.Combine(_root, id.ToString("N") + ".pcm");
}
