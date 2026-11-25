using System.Diagnostics;

namespace Kvasir.Media;

public sealed class AudioNormalizer(MediaActivity? activity = null) : IAudioNormalizer
{
    private readonly MediaActivity _activity = activity ?? new();

    public Task NormalizeAsync(string source, string destination, CancellationToken cancellationToken) =>
        _activity.RunAsync(Path.GetFileName(source), MediaStage.Converting, async operation =>
    {
        ProcessStartInfo info = new(MediaTools.RuntimeFile("ffmpeg.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (string argument in new[] { "-nostdin", "-progress", "pipe:1", "-nostats", "-y", "-f", "s16le", "-ar", "48000", "-ac", "2", "-i", source, "-af", "loudnorm=I=-16:TP=-1.5:LRA=11", "-f", "s16le", "-ar", "48000", "-ac", "2", destination }) info.ArgumentList.Add(argument);
        using Process process = Process.Start(info) ?? throw new IOException("FFmpeg не запустился.");
        process.StandardInput.Close();
        process.OutputDataReceived += (_, e) => FfmpegInteractor.ReportTime(operation, e.Data);
        process.BeginOutputReadLine();
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(timeout.Token); await error; if (process.ExitCode != 0) throw new IOException("FFmpeg не смог обработать аудио."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new MediaSourceException("FFmpeg не завершил обработку аудио за пять минут.", "conversion-timeout"); }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        return true;
    });
}
