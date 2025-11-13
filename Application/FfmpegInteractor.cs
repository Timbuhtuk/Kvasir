using Logging;
using Entities.Enums;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CS_Discord_Bot;

[LogCategory(LogCategory.FFmpeg)]
public struct FfmpegInteractor
{
    /// <summary>
    /// Конвертирует MP3 файл в PCM байты (s16le, 48000 Hz, stereo)
    /// </summary>
    public static async Task<byte[]?> ConvertMp3ToPcmBytes(string file_path)
    {
        if (!File.Exists(file_path))
        {
            await Logger.AddLog("File to convert not found!", Microsoft.Extensions.Logging.LogLevel.Error);
            return null;
        }

        string ffmpegPath = Path.Combine(Environment.CurrentDirectory, "appdata", "ffmpeg.exe");

        if (!File.Exists(ffmpegPath))
        {
            await Logger.AddLog("FFmpeg executable not found!", Microsoft.Extensions.Logging.LogLevel.Error);
            return null;
        }

        ProcessStartInfo processStartInfo = new()
        {
            FileName = ffmpegPath,
            Arguments = $"-i \"{file_path}\" -f s16le -ar 48000 -ac 2 -",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var ffmpegProcess = new Process { StartInfo = processStartInfo };
        using var outputStream = new MemoryStream();

        ffmpegProcess.ErrorDataReceived += (sender, e) =>
        {
            //if (!string.IsNullOrEmpty(e.Data))
            //Logger.AddLog($"FFMPEG Error: {e.Data}").Wait();
        };

        if (!ffmpegProcess.Start())
        {
            await Logger.AddLog("FFMPEG STARTUP ERROR", Microsoft.Extensions.Logging.LogLevel.Error);
            return null;
        }

        ffmpegProcess.BeginErrorReadLine();

        // Читаем выходной поток FFmpeg
        byte[] buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = await ffmpegProcess.StandardOutput.BaseStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await outputStream.WriteAsync(buffer, 0, bytesRead);
        }

        await ffmpegProcess.WaitForExitAsync();

        if (ffmpegProcess.ExitCode != 0)
        {
            await Logger.AddLog($"FFmpeg conversion failed with exit code {ffmpegProcess.ExitCode}", Microsoft.Extensions.Logging.LogLevel.Error);
            return null;
        }

        await Logger.AddLog("FFMPEG - conversion completed");
        return outputStream.ToArray();
    }

    /// <summary>
    /// Конвертирует MP3 файл в PCM файл (legacy метод для обратной совместимости)
    /// </summary>
    public static async Task<string?> ConvertMp3ToPcm(string file_path, string? output_file_path = null)
    {
        if (!Regex.Match(file_path, @"^[A-Z]:(?:\\{1,2}[^\\/:*?\""<>|]+)*\.mp3$").Success)
            return null;
        if (!File.Exists(file_path))
        {
            await Logger.AddLog("File to convert not found!", Microsoft.Extensions.Logging.LogLevel.Error);
            return null;
        }

        output_file_path = output_file_path == null ? file_path.Replace("mp3", "pcm") : output_file_path;

        string ffmpegPath = Path.Combine(Environment.CurrentDirectory, "appdata", "ffmpeg.exe");

        if (!File.Exists(ffmpegPath))
        {
            await Logger.AddLog("FFmpeg executable not found!", Microsoft.Extensions.Logging.LogLevel.Error);
            return null;
        }

        ProcessStartInfo processStartInfo = new()
        {
            FileName = ffmpegPath,
            Arguments = $"-i \"{file_path}\" -f s16le -ar 48000 -ac 2 \"{output_file_path}\"",
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var ffmpegProcess = new Process { StartInfo = processStartInfo };

        ffmpegProcess.ErrorDataReceived += (sender, e) =>
        {
            //if (!string.IsNullOrEmpty(e.Data))
            //Logger.AddLog($"FFMPEG Error: {e.Data}").Wait();
        };

        if (!ffmpegProcess.Start())
        {
            await Logger.AddLog("FFMPEG STARTUP ERROR", Microsoft.Extensions.Logging.LogLevel.Error);
            return null;
        }

        ffmpegProcess.BeginErrorReadLine();

        await ffmpegProcess.WaitForExitAsync();

        if (ffmpegProcess.ExitCode != 0)
        {
            await Logger.AddLog($"FFmpeg conversion failed with exit code {ffmpegProcess.ExitCode}", Microsoft.Extensions.Logging.LogLevel.Error);
        }

        await Logger.AddLog("FFMPEG - conversion completed");
        return output_file_path;
    }
}
