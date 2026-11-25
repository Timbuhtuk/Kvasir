namespace Kvasir.Media;

internal static class MediaTools
{
    public static string RuntimeFile(string name)
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "appdata", name);
        string native = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(bundled) ? bundled : File.Exists(native) ? native : bundled;
    }
}
