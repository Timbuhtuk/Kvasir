namespace Kvasir.Configuration;

public static class ApplicationPaths
{
    public const string PreviousDirectoriesFile = ".kvasir-previous-directories.json";
    internal static DataLocation Location { get; } = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kvasir"));
    public static string DataDirectory => Location.Resolve();
    public static IDisposable OpenDataSession() => Location.OpenSession(false);
    public static string ConfigurationFile => Path.Combine(DataDirectory, "configuration.json");
    public static string DesktopSettingsFile => Path.Combine(DataDirectory, "desktop-settings.json");

    public static void EnsureConfiguration()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(Path.Combine(DataDirectory, "appdata"));
        if (File.Exists(ConfigurationFile)) return;
        string template = Path.Combine(AppContext.BaseDirectory, "Defaults", "configuration.json");
        if (!File.Exists(template)) throw new FileNotFoundException("В комплекте отсутствует шаблон конфигурации.", template);
        try { File.Copy(template, ConfigurationFile, false); }
        catch (IOException) when (File.Exists(ConfigurationFile)) { }
    }

    public static string RuntimeFile(string name)
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "appdata", name);
        string native = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(bundled) ? bundled : File.Exists(native) ? native : bundled;
    }
}

internal sealed class DataLocation(string defaultDirectory)
{
    internal string PointerFile => defaultDirectory + ".location";
    internal string SessionFile => defaultDirectory + ".lock";

    internal FileStream OpenSession(bool exclusive) => new(SessionFile, FileMode.OpenOrCreate, FileAccess.Read,
        exclusive ? FileShare.None : FileShare.Read);

    internal string Resolve()
    {
        string path;
        try { path = File.ReadAllText(PointerFile); }
        catch (FileNotFoundException) { return defaultDirectory; }
        if (!Path.IsPathFullyQualified(path) || !Path.GetFileName(path).Equals("Kvasir", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(path, "configuration.json")))
            throw new IOException("The selected Kvasir data directory is unavailable.");
        return Path.GetFullPath(path);
    }

    internal void Select(string path)
    {
        if (path.Equals(defaultDirectory, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(PointerFile);
            return;
        }
        string temporary = PointerFile + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, path);
            File.Move(temporary, PointerFile, true);
        }
        finally { File.Delete(temporary); }
    }
}
