using Application.Interfaces;
using Application.Models;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace Infrastructure;

public sealed class MusicPreferences : IMusicPreferences
{
    public sealed class PreferencesData
    {
        public Dictionary<ulong, GuildMusicSettings> Guilds { get; set; } = new();
        public Dictionary<int, List<int>> PlaylistOrder { get; set; } = new();
    }
    private readonly object _sync = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly string _path;
    private PreferencesData _data;
    public MusicPreferences(IConfiguration configuration)
    {
        _path = Path.GetFullPath(configuration["music_client:preferences_file"] ?? "appdata/music-preferences.json");
        _data = File.Exists(_path) ? JsonSerializer.Deserialize<PreferencesData>(File.ReadAllText(_path)) ?? new() : new();
    }
    private static GuildMusicSettings Copy(GuildMusicSettings s) => new() { DjRoleId = s.DjRoleId, VoteSkip = s.VoteSkip, FairQueue = s.FairQueue, PerUserLimit = s.PerUserLimit, Volume = s.Volume, EffectsVolume = s.EffectsVolume, DuckMusic = s.DuckMusic };
    public GuildMusicSettings GetSettings(ulong guildId) { lock (_sync) return Copy(_data.Guilds.GetValueOrDefault(guildId) ?? new()); }
    public IReadOnlyList<int> GetOrder(int playlistId) { lock (_sync) return _data.PlaylistOrder.TryGetValue(playlistId, out var order) ? order.ToArray() : Array.Empty<int>(); }
    public Task SaveSettingsAsync(ulong guildId, GuildMusicSettings settings) => WriteAsync(data => data.Guilds[guildId] = Copy(settings));
    public Task SaveOrderAsync(int playlistId, IReadOnlyList<int> songIds) => WriteAsync(data => data.PlaylistOrder[playlistId] = songIds.Distinct().ToList());
    private async Task WriteAsync(Action<PreferencesData> change)
    {
        await _write.WaitAsync();
        try
        {
            PreferencesData next;
            lock (_sync) next = JsonSerializer.Deserialize<PreferencesData>(JsonSerializer.Serialize(_data))!;
            change(next);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temporary = _path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _path, true);
            lock (_sync) _data = next;
        }
        finally { _write.Release(); }
    }
}
