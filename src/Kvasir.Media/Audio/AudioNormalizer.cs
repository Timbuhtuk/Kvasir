using System.Diagnostics;

namespace Kvasir.Media;

public sealed class AudioNormalizer(MediaActivity? activity = null) : IAudioNormalizer
{
    private readonly MediaActivity _activity = activity ?? new();

    public async Task<bool> CanDecodeCompressedAsync(string source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(MediaTools.RuntimeFile("ffmpeg.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { "-nostdin", "-loglevel", "error", "-xerror", "-abort_on", "empty_output",
            "-i", source, "-map", "0:a:0", "-frames:a", "1", "-f", "null", "-" })
            start.ArgumentList.Add(argument);

        using Process process = Process.Start(start) ?? throw new IOException("FFmpeg не запустился.");
        Task<string> errors = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("FFmpeg не завершил проверку аудиофайла за 10 секунд.");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
            await errors;
        }
        return process.ExitCode == 0;
    }

    public Task NormalizeCompressedAsync(string source, string destination, CancellationToken cancellationToken) =>
        _activity.RunAsync(Path.GetFileName(source), MediaStage.Converting, async operation =>
        {
            if (await FfmpegInteractor.ConvertMp3ToPcm(source, destination, cancellationToken, operation) == null)
                throw new PCMConvertetionException();
            return true;
        });

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
