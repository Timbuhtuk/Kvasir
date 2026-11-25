using System.Diagnostics;

namespace Kvasir.Media;

public struct FfmpegInteractor
{
    /// <summary>
    /// Converts MP3 file to PCM bytes (s16le, 48000 Hz, stereo)
    /// </summary>
    public static async Task<byte[]?> ConvertMp3ToPcmBytes(string file_path) {
        if (!File.Exists(file_path)) {
            System.Diagnostics.Trace.WriteLine("File to convert not found!");
            return null;
        }

        string ffmpegPath = MediaTools.RuntimeFile("ffmpeg.exe");

        if (!File.Exists(ffmpegPath)) {
            System.Diagnostics.Trace.WriteLine("FFmpeg executable not found!");
            return null;
        }

        ProcessStartInfo processStartInfo = new() {
            FileName = ffmpegPath,
            Arguments = $"-nostdin -i \"{file_path}\" -f s16le -ar 48000 -ac 2 -",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };

        using Process ffmpegProcess = new Process { StartInfo = processStartInfo };
        using MemoryStream outputStream = new MemoryStream();

        ffmpegProcess.ErrorDataReceived += (sender, e) => {
        };

        if (!ffmpegProcess.Start()) {
            System.Diagnostics.Trace.WriteLine("FFMPEG STARTUP ERROR");
            return null;
        }

        ffmpegProcess.StandardInput.Close();
        ffmpegProcess.BeginErrorReadLine();

        byte[] buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = await ffmpegProcess.StandardOutput.BaseStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            await outputStream.WriteAsync(buffer, 0, bytesRead);

        await ffmpegProcess.WaitForExitAsync();

        if (ffmpegProcess.ExitCode != 0) {
            System.Diagnostics.Trace.WriteLine($"FFmpeg conversion failed with exit code {ffmpegProcess.ExitCode}");
            return null;
        }

        System.Diagnostics.Trace.WriteLine("FFMPEG - conversion completed");
        return outputStream.ToArray();
    }

    /// <summary>
    /// Converts MP3 file to PCM file (legacy method for backward compatibility)
    /// </summary>
    public static async Task<string?> ConvertMp3ToPcm(string file_path, string? output_file_path = null, CancellationToken cancellationToken = default, MediaOperation? operation = null) {
        if (string.IsNullOrWhiteSpace(file_path)) {
            System.Diagnostics.Trace.WriteLine("File path is null or empty!");
            return null;
        }

        // FFmpeg determines the input container; yt-dlp may return WebM or M4A.
        if (!File.Exists(file_path)) {
            System.Diagnostics.Trace.WriteLine("File to convert not found!");
            return null;
        }

        if (string.IsNullOrWhiteSpace(output_file_path))
        {
            output_file_path = Path.ChangeExtension(file_path, ".pcm");
        }

        if (string.IsNullOrWhiteSpace(output_file_path))
        {
            System.Diagnostics.Trace.WriteLine("Output file path is null or empty after processing!");
            return null;
        }

        string ffmpegPath = MediaTools.RuntimeFile("ffmpeg.exe");

        if (!File.Exists(ffmpegPath)) {
            if (operation != null) throw new FileNotFoundException("Не найден ffmpeg.exe.", ffmpegPath);
            System.Diagnostics.Trace.WriteLine("FFmpeg executable not found!");
            return null;
        }

        ProcessStartInfo processStartInfo = new() {
            FileName = ffmpegPath,
            Arguments = $"-nostdin -loglevel error -progress pipe:1 -nostats -y -i \"{file_path}\" -af loudnorm=I=-16:TP=-1.5:LRA=11 -f s16le -ar 48000 -ac 2 \"{output_file_path}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };

        using Process ffmpegProcess = new Process { StartInfo = processStartInfo };

        string? lastError = null;
        ffmpegProcess.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) lastError = e.Data;
        };

        if (!ffmpegProcess.Start()) {
            System.Diagnostics.Trace.WriteLine("FFMPEG STARTUP ERROR");
            return null;
        }

        ffmpegProcess.StandardInput.Close();
        ffmpegProcess.BeginErrorReadLine();

        ffmpegProcess.OutputDataReceived += (_, e) => ReportTime(operation, e.Data);
        ffmpegProcess.BeginOutputReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try { await ffmpegProcess.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new MediaSourceException("FFmpeg не завершил обработку аудио за пять минут.", "conversion-timeout"); }
        finally { if (!ffmpegProcess.HasExited) { ffmpegProcess.Kill(true); await ffmpegProcess.WaitForExitAsync(); } }

        if (ffmpegProcess.ExitCode != 0)
        {
            if (operation != null) throw new PCMConvertetionException($"FFmpeg ({ffmpegProcess.ExitCode}): {lastError}");
            System.Diagnostics.Trace.WriteLine($"FFmpeg conversion failed with exit code {ffmpegProcess.ExitCode}");
            return null;
        }

        System.Diagnostics.Trace.WriteLine("FFMPEG - conversion completed");
        return output_file_path;
    }

    internal static void ReportTime(MediaOperation? operation, string? line)
    {
        if (line?.StartsWith("out_time_us=", StringComparison.Ordinal) == true &&
            long.TryParse(line.AsSpan(12), out long microseconds) && microseconds >= 0)
            operation?.Report(MediaStage.Converting, "FFmpeg", processed: TimeSpan.FromMicroseconds(microseconds));
    }
}
