using Logging;
using Entities.Enums;
using System.Diagnostics;

namespace Connectors.Media;

[LogCategory(LogCategory.FFmpeg)]
public struct FfmpegInteractor
{
    /// <summary>
    /// Converts MP3 file to PCM bytes (s16le, 48000 Hz, stereo)
    /// </summary>
    public static async Task<byte[]?> ConvertMp3ToPcmBytes(string file_path) {
        if (!File.Exists(file_path)) {
            await Logger.AddLog("File to convert not found!", Microsoft.Extensions.Logging.LogLevel.Error);
            return null;
        }

        string ffmpegPath = Path.Combine(Environment.CurrentDirectory, "appdata", "ffmpeg.exe");

        if (!File.Exists(ffmpegPath)) {
            await Logger.AddLog("FFmpeg executable not found!", Microsoft.Extensions.Logging.LogLevel.Error);
            return null;
        }

        ProcessStartInfo processStartInfo = new() {
            FileName = ffmpegPath,
            Arguments = $"-i \"{file_path}\" -f s16le -ar 48000 -ac 2 -",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using Process ffmpegProcess = new Process { StartInfo = processStartInfo };
        using MemoryStream outputStream = new MemoryStream();

        ffmpegProcess.ErrorDataReceived += (sender, e) => {
        };

        if (!ffmpegProcess.Start()) {
            await Logger.AddLog("FFMPEG STARTUP ERROR", Microsoft.Extensions.Logging.LogLevel.Error);
            return null;
        }

        ffmpegProcess.BeginErrorReadLine();

        byte[] buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = await ffmpegProcess.StandardOutput.BaseStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            await outputStream.WriteAsync(buffer, 0, bytesRead);

        await ffmpegProcess.WaitForExitAsync();

        if (ffmpegProcess.ExitCode != 0) {
            await Logger.AddLog($"FFmpeg conversion failed with exit code {ffmpegProcess.ExitCode}", Microsoft.Extensions.Logging.LogLevel.Error);
            return null;
        }

        await Logger.AddLog("FFMPEG - conversion completed");
        return outputStream.ToArray();
    }

    /// <summary>
    /// Converts MP3 file to PCM file (legacy method for backward compatibility)
    /// </summary>
    public static async Task<string?> ConvertMp3ToPcm(string file_path, string? output_file_path = null) {
        if (string.IsNullOrWhiteSpace(file_path)) {
            await Logger.AddLog("File path is null or empty!", Microsoft.Extensions.Logging.LogLevel.Error);
            return null;
        }

        // Проверяем расширение файла
        if (!file_path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) {
            await Logger.AddLog($"File path does not end with .mp3: {file_path}", Microsoft.Extensions.Logging.LogLevel.Error);
            return null;
        }

        if (!File.Exists(file_path)) {
            await Logger.AddLog("File to convert not found!", Microsoft.Extensions.Logging.LogLevel.Error);
            return null;
        }

        if (string.IsNullOrWhiteSpace(output_file_path))
        {
            output_file_path = file_path.Replace(".mp3", ".pcm", StringComparison.OrdinalIgnoreCase);
        }

        if (string.IsNullOrWhiteSpace(output_file_path))
        {
            await Logger.AddLog("Output file path is null or empty after processing!", Microsoft.Extensions.Logging.LogLevel.Error);
            return null;
        }

        string ffmpegPath = Path.Combine(Environment.CurrentDirectory, "appdata", "ffmpeg.exe");

        if (!File.Exists(ffmpegPath)) {
            await Logger.AddLog("FFmpeg executable not found!", Microsoft.Extensions.Logging.LogLevel.Error);
            return null;
        }

        ProcessStartInfo processStartInfo = new() {
            FileName = ffmpegPath,
            Arguments = $"-i \"{file_path}\" -f s16le -ar 48000 -ac 2 \"{output_file_path}\"",
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using Process ffmpegProcess = new Process { StartInfo = processStartInfo };

        ffmpegProcess.ErrorDataReceived += (sender, e) => {
        };

        if (!ffmpegProcess.Start()) {
            await Logger.AddLog("FFMPEG STARTUP ERROR", Microsoft.Extensions.Logging.LogLevel.Error);
            return null;
        }

        ffmpegProcess.BeginErrorReadLine();

        await ffmpegProcess.WaitForExitAsync();

        if (ffmpegProcess.ExitCode != 0)
            await Logger.AddLog($"FFmpeg conversion failed with exit code {ffmpegProcess.ExitCode}", Microsoft.Extensions.Logging.LogLevel.Error);

        await Logger.AddLog("FFMPEG - conversion completed");
        return output_file_path;
    }
}
