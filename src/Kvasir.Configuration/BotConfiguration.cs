using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kvasir.Configuration;

public sealed class MissingBotTokenException() : InvalidOperationException("Для запуска нужен токен Discord-бота.");

public sealed class BotConfiguration(string? path = null)
{
    public string FilePath { get; } = path ?? ApplicationPaths.ConfigurationFile;
    private JsonObject Read()
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
        var root = Read();
        if (root["tokens"] is not JsonArray tokens) { tokens = []; root["tokens"] = tokens; }
        if (tokens.Count == 0) tokens.Add(token); else tokens[0] = token;
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
