using System.Diagnostics;

namespace Kvasir.Media;

internal static class YtDlpProcess
{
    public static async Task<string> RunAsync(IEnumerable<string> arguments, TimeSpan limit, CancellationToken cancellationToken = default, Action<string>? onOutput = null)
    {
        string executable = MediaTools.RuntimeFile("yt-dlp.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Не найден yt-dlp.exe.", executable);
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        info.Environment.Remove("KVASIR_CONTROL_STDIN");
        info.Environment.Remove("KVASIR_CONTROL_PIPE");
        info.Environment.Remove("KVASIR_CONTROL_SESSION");
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        process.Start();
        process.StandardInput.Close();
        Task<string> output = onOutput == null ? process.StandardOutput.ReadToEndAsync() : ReadProgressAsync(process.StandardOutput, onOutput);
        Task<string> errors = onOutput == null ? process.StandardError.ReadToEndAsync() : ReadProgressAsync(process.StandardError, onOutput, keepErrors: true);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(output, errors);
            cancellationToken.ThrowIfCancellationRequested();
            string diagnostic = errors.Result.Trim();
            if (diagnostic.Length > 1200) diagnostic = diagnostic[^1200..];
            throw new MediaSourceException("Источник не ответил за отведённое время. " + diagnostic, "source-timeout");
        }
        await Task.WhenAll(output, errors);
        if (process.ExitCode != 0)
        {
            string detail = string.Join(" | ", errors.Result.Split('\n').Where(s => s.Contains("ERROR", StringComparison.OrdinalIgnoreCase)).Take(3));
            if (string.IsNullOrWhiteSpace(detail)) detail = errors.Result.Trim()[..Math.Min(errors.Result.Trim().Length, 1200)];
            string code = detail.Contains("Sign in", StringComparison.OrdinalIgnoreCase) || detail.Contains("confirm you're not a bot", StringComparison.OrdinalIgnoreCase) ? "source-restricted" : "source";
            throw new MediaSourceException("Не удалось получить источник через yt-dlp. " + detail, code);
        }
        return output.Result;
    }

    private static async Task<string> ReadProgressAsync(StreamReader reader, Action<string> report, bool keepErrors = false)
    {
        var diagnostics = new Queue<string>();
        while (await reader.ReadLineAsync() is string line)
        {
            if (line.StartsWith("kvasir ", StringComparison.Ordinal)) report(line);
            else if (keepErrors)
            {
                diagnostics.Enqueue(line);
                if (diagnostics.Count > 12) diagnostics.Dequeue();
            }
        }
        return string.Join(Environment.NewLine, diagnostics);
    }
}
