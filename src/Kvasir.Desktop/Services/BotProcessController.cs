using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Kvasir.Configuration;
using Kvasir.Desktop.Localization;

namespace Kvasir.Desktop.Services;

public sealed class BotProcessController
{
    private Process? _process;
    private string? _redactedToken;
    private bool _stopRequested;
    public event Action<string>? Log;
    public event Action<int, bool>? Exited;
    public bool Running => _process is { HasExited: false };
    public string ConfigurationPath => ApplicationPaths.ConfigurationFile;
    public string PipeName { get; private set; } = "";
    public string Session { get; private set; } = "";

    public void Start()
    {
        if (Running) return;
        _stopRequested = false;
        ApplicationPaths.EnsureConfiguration();
        var launch = FindBot() ?? throw new FileNotFoundException(UiText.Get("BotExecutableMissing"));
        PipeName = "Kvasir-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        Session = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        ProcessStartInfo info = new(launch.Executable)
        {
            WorkingDirectory = ApplicationPaths.DataDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        info.Environment["KVASIR_CONTROL_STDIN"] = "1";
        info.Environment["KVASIR_CONTROL_PIPE"] = PipeName;
        info.Environment["KVASIR_CONTROL_SESSION"] = Session;
        string? resolvedToken = new BotConfiguration().ReadToken();
        if (string.IsNullOrWhiteSpace(resolvedToken))
            throw new MissingBotTokenException();
        _redactedToken = resolvedToken.Trim();
        // The bot reads the same file itself; an inherited token must not override it.
        info.Environment.Remove("KVASIR_DISCORD_TOKEN");
        Process process = new() { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Receive("OUT", e.Data);
        process.ErrorDataReceived += (_, e) => Receive("ERR", e.Data);
        process.Exited += (_, _) => Exited?.Invoke(process.ExitCode, _stopRequested);
        try
        {
            if (!process.Start()) throw new InvalidOperationException(UiText.Get("BotProcessStartFailed"));
            _process = process;
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
        }
        catch
        {
            // If output subscription failed after launch, retain ownership so Stop can still finish the child.
            if (!ReferenceEquals(_process, process)) { process.Dispose(); _redactedToken = null; }
            throw;
        }
        finally
        {
            info.Environment.Remove("KVASIR_DISCORD_TOKEN");
            info.Environment.Remove("KVASIR_CONTROL_SESSION");
        }
    }

    public async Task StopAsync()
    {
        Process? process = _process;
        if (process == null) return;
        if (!process.HasExited)
        {
            _stopRequested = true;
            try
            {
                await process.StandardInput.WriteLineAsync("stop");
                await process.StandardInput.FlushAsync();
            }
            catch (IOException) { }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                // Keep the process and window alive: force kill could corrupt an active cache/import.
                throw new TimeoutException(UiText.Get("BotStopTimeout"));
            }
        }
        process.WaitForExit();
        process.Dispose(); _process = null; _redactedToken = null; Session = "";
    }

    private void Receive(string source, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (!string.IsNullOrEmpty(_redactedToken)) line = line.Replace(_redactedToken, "[TOKEN]", StringComparison.Ordinal);
        if (line.Length > 8192) line = line[..8192] + "…";
        Log?.Invoke($"[{source}] {line}");
    }

    private static (string Executable, string Directory)? FindBot()
    {
        string[] paths = [Path.Combine(AppContext.BaseDirectory, "bot", "Kvasir.exe"), Path.Combine(AppContext.BaseDirectory, "Kvasir.exe")];
        string? executable = paths.FirstOrDefault(File.Exists);
        return executable == null ? null : (executable, Path.GetDirectoryName(executable)!);
    }
}
