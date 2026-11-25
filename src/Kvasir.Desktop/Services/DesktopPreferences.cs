using System.IO;
using System.Text.Json;
using Kvasir.Configuration;
using Tessera.WpfStyles.Controls;
using Tessera.WpfStyles.Themes;

namespace Kvasir.Desktop.Services;

public sealed record DesktopPreferences
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public TesseraTheme Theme { get; init; } = TesseraTheme.Dark;
    public string Language { get; init; } = "ru";
    public NotificationPlacement NotificationPosition { get; init; } = NotificationPlacement.TopRight;

    public static DesktopPreferences Load(string? path = null)
    {
        try
        {
            using var file = File.OpenRead(path ?? ApplicationPaths.DesktopSettingsFile);
            using var document = JsonDocument.Parse(file);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return new();
            var settings = document.RootElement;
            return new()
            {
                Theme = ReadEnum(settings, "theme", TesseraTheme.Dark),
                Language = ReadString(settings, "language") == "en" ? "en" : "ru",
                NotificationPosition = ReadEnum(settings, "notificationPosition", NotificationPlacement.TopRight)
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(string? path = null)
    {
        path ??= ApplicationPaths.DesktopSettingsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new
            {
                theme = Theme.ToString(),
                language = Language,
                notificationPosition = NotificationPosition.ToString()
            }, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string? ReadString(JsonElement settings, string name) =>
        settings.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static T ReadEnum<T>(JsonElement settings, string name, T fallback) where T : struct, Enum =>
        Enum.TryParse(ReadString(settings, name), out T value) && Enum.IsDefined(value) ? value : fallback;
}
