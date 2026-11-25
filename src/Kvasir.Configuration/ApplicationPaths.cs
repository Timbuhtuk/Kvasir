namespace Kvasir.Configuration;

public static class ApplicationPaths
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kvasir");
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
