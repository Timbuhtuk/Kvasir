using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kvasir.Configuration;

public sealed class MissingBotTokenException() : InvalidOperationException("Для запуска нужен токен Discord-бота.");

public sealed record BotSettings(string OwnerId, int CacheLimitMb, int CacheAgeDays, bool SpotifyEnabled,
    string SpotifyClientId, string SpotifySecret, string LogLevel, string DatabaseLogLevel, bool FileLogging, bool ConsoleLogging)
{
    public static IReadOnlyList<string> LogLevels { get; } = ["Trace", "Debug", "Information", "Warning", "Error", "Critical", "None"];
}

public sealed class BotConfiguration(string? path = null)
{
    public const string DecodeOnPlaybackKey = "decode_on_playback";

    public string FilePath { get; } = path ?? ApplicationPaths.ConfigurationFile;
    internal JsonObject Read()
    {
        string json = File.ReadAllText(FilePath);
        return JsonNode.Parse(json, new JsonNodeOptions { PropertyNameCaseInsensitive = true },
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? throw new JsonException("Корень конфигурации должен быть объектом.");
    }
    public string? ReadToken()
    {
        var root = Read();
        if (root["tokens"] is JsonArray tokens && tokens.FirstOrDefault() is JsonValue first && first.TryGetValue<string>(out var token) && !string.IsNullOrWhiteSpace(token))
            return token.Trim();
        if (root["KVASIR_DISCORD_TOKEN"] is JsonValue fallback && fallback.TryGetValue<string>(out var value) && !string.IsNullOrWhiteSpace(value))
            return value.Trim();
        return null;
    }
    public void SaveToken(string token)
    {
        token = token.Trim();
        if (token.StartsWith("Bot ", StringComparison.OrdinalIgnoreCase)) token = token[4..].Trim();
        if (token.Length is < 20 or > 512 || token.Any(char.IsWhiteSpace))
            throw new ArgumentException("Вставьте полный токен бота без пробелов и переносов строки.");
        Update(root =>
        {
            if (root["tokens"] is not JsonArray tokens) { tokens = []; root["tokens"] = tokens; }
            if (tokens.Count == 0) tokens.Add(token); else tokens[0] = token;
        });
    }

    public bool ReadDecodeOnPlayback() =>
        Read()["music_client"] is JsonObject music &&
        music[DecodeOnPlaybackKey] is JsonValue setting &&
        bool.TryParse(setting.ToString(), out bool enabled) && enabled;

    public void SaveDecodeOnPlayback(bool enabled) =>
        Update(root => Section(root, "music_client")[DecodeOnPlaybackKey] = enabled);

    public BotSettings ReadSettings()
    {
        var root = Read();
        var music = Section(root, "music_client");
        var spotify = Section(root, "spotify_settings");
        var logging = Section(root, "logging");
        return new(root["owner_id"]?.ToString() ?? "",
            Number(music["cache_limit_mb"], 2048, 64, 1048576), Number(music["cache_max_age_days"], 30, 1, 3650),
            Flag(spotify["SPOTIFY_ENABLED"], false), spotify["SPOTIFY_CLIENT_ID"]?.ToString() ?? "",
            spotify["SPOTIFY_CLIENT_SECRET"]?.ToString() ?? "", Level(logging["level"], "Information"),
            Level(logging["dbLogLevel"], "None"), Flag(logging["enableFileLogging"], true), Flag(logging["enableConsoleLogging"], true));
    }

    public void SaveSettings(BotSettings settings) => Update(root =>
    {
        var music = Section(root, "music_client");
        var spotify = Section(root, "spotify_settings");
        var logging = Section(root, "logging");
        root["owner_id"] = settings.OwnerId;
        music["cache_limit_mb"] = settings.CacheLimitMb;
        music["cache_max_age_days"] = settings.CacheAgeDays;
        spotify["SPOTIFY_ENABLED"] = settings.SpotifyEnabled;
        spotify["SPOTIFY_CLIENT_ID"] = settings.SpotifyClientId;
        spotify["SPOTIFY_CLIENT_SECRET"] = settings.SpotifySecret;
        logging["level"] = settings.LogLevel;
        logging["dbLogLevel"] = settings.DatabaseLogLevel;
        logging["enableFileLogging"] = settings.FileLogging;
        logging["enableConsoleLogging"] = settings.ConsoleLogging;
    });

    private static JsonObject Section(JsonObject root, string name)
    {
        if (root[name] == null) root[name] = new JsonObject();
        return root[name] as JsonObject ?? throw new JsonException($"Invalid configuration section: {name}");
    }

    private static int Number(JsonNode? node, int fallback, int min, int max) =>
        long.TryParse(node?.ToString(), out long n) ? (int)Math.Clamp(n, min, max) : fallback;
    private static bool Flag(JsonNode? node, bool fallback) => bool.TryParse(node?.ToString(), out bool value) ? value : fallback;
    private static string Level(JsonNode? node, string fallback) =>
        BotSettings.LogLevels.FirstOrDefault(level => string.Equals(level, node?.ToString(), StringComparison.OrdinalIgnoreCase)) ?? fallback;

    private void Update(Action<JsonObject> edit)
    {
        using var mutex = new Mutex(false, "Kvasir.BotConfiguration");
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(3))) throw new IOException("Configuration is busy.");
        }
        catch (AbandonedMutexException) { }
        try
        {
            var root = Read();
            edit(root);
            Save(root);
        }
        finally { mutex.ReleaseMutex(); }
    }

    internal void Save(JsonObject root)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
        string temporary = Path.Combine(directory, ".configuration-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, FilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
