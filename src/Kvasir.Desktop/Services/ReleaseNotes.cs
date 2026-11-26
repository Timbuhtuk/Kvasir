using System.IO;
using System.Text.Json;
using Kvasir.Configuration;
using Kvasir.Desktop.Localization;

namespace Kvasir.Desktop.Services;

internal sealed record ReleaseNote(string Version, Dictionary<string, string> Title, Dictionary<string, string[]> Notes)
{
    public string Heading => Title.GetValueOrDefault(UiText.Language) ?? Title["en"];
    public string[] Items => Notes.GetValueOrDefault(UiText.Language) ?? Notes["en"];
    public override string ToString() => $"v{Version}";
}

internal sealed class ReleaseNotes
{
    private static readonly Lazy<ReleaseNotes> Catalog = new(Load);
    private sealed record Document(ReleaseNote[] Releases);

    internal static ReleaseNotes Bundled => Catalog.Value;
    internal static Version InstalledVersion { get; } =
        Version.Parse(typeof(ReleaseNotes).Assembly.GetName().Version!.ToString(3));
    internal static bool IsReleaseBuild => InstalledVersion != new Version(0, 0, 0);
    internal IReadOnlyList<ReleaseNote> Entries { get; }
    internal ReleaseNote Current => Entries[0];

    private ReleaseNotes(IReadOnlyList<ReleaseNote> entries) => Entries = entries;

    private static ReleaseNotes Load()
    {
        using Stream stream = typeof(ReleaseNotes).Assembly.GetManifestResourceStream("Kvasir.ReleaseNotes.json")
            ?? throw new InvalidDataException("Bundled Kvasir release notes are missing.");
        using StreamReader reader = new(stream);
        return Parse(reader.ReadToEnd(), InstalledVersion);
    }

    internal static ReleaseNotes Parse(string json, Version installed)
    {
        var document = JsonSerializer.Deserialize<Document>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (document?.Releases is not { Length: > 0 } entries)
            throw new InvalidDataException("The release notes catalog is empty.");

        string current = installed.ToString(3);
        var versions = new HashSet<string>();
        foreach (ReleaseNote? entry in entries)
        {
            if (entry == null) throw new InvalidDataException("The release notes catalog contains an empty entry.");
            if (!Version.TryParse(entry.Version, out var version) || version.Build < 0 || version.Revision != -1 ||
                version.ToString(3) != entry.Version || !versions.Add(entry.Version) ||
                !HasTranslation(entry, "ru") || !HasTranslation(entry, "en"))
                throw new InvalidDataException($"Invalid or duplicate release notes for v{entry.Version}.");
        }
        if (installed != new Version(0, 0, 0) && entries[0].Version != current)
            throw new InvalidDataException("The current release note must match the installed version.");
        return new ReleaseNotes(entries);
    }

    private static bool HasTranslation(ReleaseNote entry, string language) =>
        entry.Title?.GetValueOrDefault(language) is { } title && !string.IsNullOrWhiteSpace(title) &&
        entry.Notes?.GetValueOrDefault(language) is { Length: > 0 } items &&
        items.All(item => !string.IsNullOrWhiteSpace(item));
}

internal sealed class ReleaseNotesState(string path)
{
    internal static ReleaseNotesState Local => new(Path.Combine(ApplicationPaths.DataDirectory, "release-notes-state.json"));

    internal bool HasSeen(string version) => Read().Contains(version);

    internal bool MarkSeen(string version)
    {
        var seen = Read();
        if (!seen.Add(version)) return true;

        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(seen));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private HashSet<string> Read()
    {
        try { return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(path)) ?? []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
}
