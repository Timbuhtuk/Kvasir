using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Kvasir.Configuration;
using Kvasir.Desktop.Localization;

namespace Kvasir.Desktop.Services;

internal static class DesktopMaintenance
{
    internal const string UpdateReadyEventVariable = "KVASIR_UPDATE_READY_EVENT";
    private static readonly Assembly DesktopAssembly = typeof(DesktopMaintenance).Assembly;
    private static readonly string? Executable = Environment.ProcessPath;
    internal static bool CanReplaceExecutable { get; } =
        DesktopAssembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(metadata => metadata is { Key: "KvasirStandalone", Value: "true" }) &&
        Executable is not null && Path.GetExtension(Executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
        File.Exists(Executable) && FileVersionInfo.GetVersionInfo(Executable) is { ProductName: "Kvasir.Desktop", OriginalFilename: "Kvasir.Desktop.dll" };

    internal static void StartUpdate(string downloadedFile, GitHubUpdater.Package package) => StartHelper(downloadedFile, package);
    internal static void StartUninstall() => StartHelper(null, null);

    internal static void CheckPath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new IOException(UiText.Format("MaintenanceUnsafePathFormat", path));
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException(UiText.Format("MaintenanceUnsafePathFormat", current));
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void StartHelper(string? update, GitHubUpdater.Package? package)
    {
        if (!CanReplaceExecutable) throw new InvalidOperationException(UiText.Get("MaintenanceStandaloneOnly"));
        string target = Executable!;
        string dataDirectory = ApplicationPaths.DataDirectory;
        CheckPath(target);
        if (update is null) CheckPath(dataDirectory);
        else CheckPath(update);

        var exe = new FileInfo(target);
        if (exe.IsReadOnly) throw new IOException(UiText.Format("MaintenanceExecutableChangedFormat", target));
        string directory = Path.GetDirectoryName(target)!;
        string probe = Path.Combine(directory, $".Kvasir-write-check-{Guid.NewGuid():N}");
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }

        string bundleDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        string? bundleParent = Path.GetDirectoryName(bundleDirectory);
        string? bundleBase = string.Equals(Path.GetFileName(bundleParent), Path.GetFileNameWithoutExtension(target), StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(bundleParent!) : null;

        using var owner = Process.GetCurrentProcess();
        var plan = new
        {
            Executable = target,
            Update = update,
            Sha256 = package?.Sha256,
            Version = package?.Version.ToString(3),
            DataDirectory = dataDirectory,
            LocationFile = ApplicationPaths.Location.PointerFile,
            SessionFile = ApplicationPaths.Location.SessionFile,
            BundleBases = new[] { Path.Combine(Path.GetTempPath(), ".net"), bundleBase }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            OwnerId = owner.Id,
            OwnerStarted = owner.StartTime.ToUniversalTime().Ticks,
            Length = exe.Length,
            LastWrite = exe.LastWriteTimeUtc.Ticks,
            ErrorTitle = UiText.Get(update is null ? "UninstallFailed" : "UpdateFailed"),
            UnsafePath = UiText.Get("MaintenanceUnsafePathFormat"),
            CloseTimeout = UiText.Get("MaintenanceCloseTimeout"),
            OtherInstance = UiText.Get("MaintenanceOtherInstance"),
            AlreadyRunning = UiText.Get("MaintenanceAlreadyRunning"),
            ChangedExecutable = UiText.Get("MaintenanceExecutableChangedFormat"),
            BadUpdate = UiText.Get("UpdateChecksumFailed"),
            RestartFailed = UiText.Get("UpdateRestartFailed")
        };

        using var resource = DesktopAssembly.GetManifestResourceStream("Kvasir.Maintenance.ps1")!;
        using var reader = new StreamReader(resource);
        string script = Convert.ToBase64String(Encoding.Unicode.GetBytes(reader.ReadToEnd()));
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.SystemDirectory
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", script })
            start.ArgumentList.Add(argument);
        start.Environment["KVASIR_MAINTENANCE_PLAN"] = JsonSerializer.Serialize(plan);
        using var helper = Process.Start(start) ?? throw new IOException(UiText.Get("MaintenanceHelperFailed"));
    }
}
