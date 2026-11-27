using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kvasir.Configuration;
using Kvasir.Desktop.Localization;

namespace Kvasir.Desktop.Services;

internal static class DataDirectoryMigration
{
    internal static void Restart(string source, string destination)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(App).Assembly.Location);
        start.WorkingDirectory = Environment.SystemDirectory;
        foreach (string arg in new[] { "--move-data", Environment.ProcessId.ToString(), source, destination })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException(UiText.Get("MaintenanceHelperFailed"));
    }

    internal static void Validate(string source, string destination)
    {
        DesktopMaintenance.CheckPath(source);
        DesktopMaintenance.CheckPath(destination);
        if (!Path.GetFileName(destination).Equals("Kvasir", StringComparison.OrdinalIgnoreCase)
            || Inside(source, destination) || Inside(destination, source) || File.Exists(destination) || Directory.Exists(destination))
            throw new IOException(UiText.Get("MoveDataDestinationInvalid"));
        if (Inside(source, AppContext.BaseDirectory) || Inside(source, Environment.ProcessPath!))
            throw new IOException(UiText.Get("MoveDataContainsApplication"));
    }

    private static bool Inside(string directory, string path)
    {
        string relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    // The exclusive session spans copying, switching the pointer and retiring the old tree.
    internal static bool Move(DataLocation location, string source, string destination, IProgress<int> progress)
    {
        using var session = location.OpenSession(true);
        if (!source.Equals(location.Resolve(), StringComparison.OrdinalIgnoreCase))
            throw new IOException(UiText.Get("MoveDataSourceChanged"));
        Validate(source, destination);
        var directories = new List<string> { source };
        var files = new List<FileInfo>();
        for (int i = 0; i < directories.Count; i++)
        {
            foreach (var entry in new DirectoryInfo(directories[i]).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException(UiText.Format("MaintenanceUnsafePathFormat", entry.FullName));
                if (entry is DirectoryInfo subdirectory) directories.Add(subdirectory.FullName);
                else files.Add((FileInfo)entry);
            }
        }

        string staging = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string working = staging;
        long total = files.Sum(f => f.Length), copied = 0;
        int lastPercent = -1;
        try
        {
            foreach (string directory in directories)
                Directory.CreateDirectory(Path.Combine(staging, Path.GetRelativePath(source, directory)));
            byte[] buffer = new byte[1024 * 1024];
            foreach (FileInfo file in files)
            {
                string target = Path.Combine(staging, Path.GetRelativePath(source, file.FullName));
                using (var input = file.OpenRead())
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    int count;
                    while ((count = input.Read(buffer)) > 0)
                    {
                        output.Write(buffer, 0, count);
                        copied += count;
                        int percent = (int)(100d * copied / Math.Max(1, total));
                        if (percent != lastPercent) { progress.Report(percent); lastPercent = percent; }
                    }
                    output.Flush(true);
                }
                File.SetLastWriteTimeUtc(target, file.LastWriteTimeUtc);
            }

            RebaseConfiguration(staging, source, destination);
            string marker = Path.Combine(staging, ApplicationPaths.PreviousDirectoriesFile);
            List<string> previous = File.Exists(marker) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(marker))
                ?? throw new InvalidDataException("Invalid data migration record.") : [];
            if (!previous.Contains(source, StringComparer.OrdinalIgnoreCase)) previous.Add(source);
            File.WriteAllText(marker, JsonSerializer.Serialize(previous));
            Directory.Move(staging, destination);
            working = destination;
            location.Select(destination);
        }
        catch
        {
            if (Directory.Exists(working)) Directory.Delete(working, true);
            throw;
        }

        try
        {
            foreach (var file in files)
            {
                var current = new FileInfo(file.FullName);
                if (current.Length != file.Length || current.LastWriteTimeUtc != file.LastWriteTimeUtc) continue;
                current.Delete();
            }
            for (int i = directories.Count - 1; i >= 0; i--) Directory.Delete(directories[i]);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void RebaseConfiguration(string staging, string source, string destination)
    {
        string Rebase(string path) => Path.IsPathFullyQualified(path) && Inside(source, path)
            ? Path.GetFullPath(Path.Combine(destination, Path.GetRelativePath(source, path))) : path;

        var store = new BotConfiguration(Path.Combine(staging, "configuration.json"));
        var root = store.Read();
        if (root["music_client"] is JsonObject music)
            foreach (string key in new[] { "music_folder", "preferences_file" })
                if (music[key]?.GetValue<string>() is string path && Rebase(path) != path) music[key] = Rebase(path);
        if (root["connection_string"]?.GetValue<string>() is string connection)
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connection };
            foreach (string key in new[] { "Data Source", "DataSource", "Filename" })
                if (builder.TryGetValue(key, out var value) && value is string path && Rebase(path) != path)
                {
                    builder[key] = Rebase(path);
                    root["connection_string"] = builder.ConnectionString;
                    break;
                }
        }
        store.Save(root);
    }
}
