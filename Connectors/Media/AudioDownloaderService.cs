using Application.Interfaces;
using Entities.Enums;
using Entities.Models;
using Logging;
using System.Diagnostics;
using YoutubeDLSharp;
using YoutubeDLSharp.Options;
using YoutubeExplode;

namespace Connectors.Media;

public enum Provider
{
    YoutubeExplode,
    YoutubeDLSharp
}

[LogCategory(LogCategory.AudioDownload)]
public class AudioDownloaderService : IAudioDownloaderService
{
    private readonly YoutubeDL _ytdl = new();
    private readonly YoutubeClient _youtube = new();
    private const string YoutubeDLPath = "appdata\\yt-dlp.exe";
    private const Provider current_working_provider = Provider.YoutubeExplode;


    public Task<bool> DownloadAsync(string? videoLink, string? outputFilePath) =>
        DownloadAsync(videoLink, outputFilePath, current_working_provider);

    private async Task<bool> DownloadAsync(string? videoLink, string? outputFilePath, Provider provider)
    {
        if (string.IsNullOrWhiteSpace(videoLink))
        {
            await Logger.AddLog("Video link is null or empty", Microsoft.Extensions.Logging.LogLevel.Error);
            return false;
        }

        if (string.IsNullOrWhiteSpace(outputFilePath))
        {
            await Logger.AddLog("Output file path is null or empty", Microsoft.Extensions.Logging.LogLevel.Error);
            return false;
        }



        return await DownloadBody(videoLink, outputFilePath, provider);

        async Task<bool> DownloadBody(string videoLink, string outputFilePath, Provider provider)
        {
            try
            {
                string? directory = Path.GetDirectoryName(outputFilePath);
                if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                await TryDownloadWithProvider(videoLink, outputFilePath, provider);
                await Logger.AddLog($"PCM file saved: {outputFilePath}");
                return true;
            }
            catch (PCMConvertetionException)
            {
                await Logger.AddLog($"PCM convertation lethal error", Microsoft.Extensions.Logging.LogLevel.Error);
            }
            catch (AudioDownloadException ex)
            {
                await Logger.AddLog($"Download failed with {provider}: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error);
                Provider? fallbackProvider = current_working_provider == provider ? provider == Provider.YoutubeExplode ? Provider.YoutubeDLSharp : Provider.YoutubeExplode : null;
                if (fallbackProvider != null) return await DownloadBody(videoLink, outputFilePath, fallbackProvider.Value);
            }
            catch (Exception ex)
            {
                await Logger.AddLog($"{ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, exception: ex);
            }
            return false;
        }
    }

    public Task<bool> DownloadAsync(Song song) => DownloadAsync(song.Link, song.FilePath);

    private Task<bool> DownloadAsync(Song song, Provider provider) => DownloadAsync(song.Link, song.FilePath, provider);

    private async Task<bool> TryDownloadWithProvider(string videoLink, string outputFilePath, Provider provider)
    {
        if (provider == Provider.YoutubeExplode)
            return await DownloadWithYoutubeExplode(videoLink, outputFilePath);
        else if (provider == Provider.YoutubeDLSharp)
            return await DownloadWithYoutubeDLSharp(videoLink, outputFilePath);
        return false;
    }

    private async Task<bool> DownloadWithYoutubeExplode(string videoLink, string outputFilePath)
    {
        YoutubeExplode.Videos.Video video = await _youtube.Videos.GetAsync(videoLink);
        YoutubeExplode.Videos.Streams.StreamManifest streamManifest;
        IReadOnlyList<YoutubeExplode.Videos.Streams.IAudioStreamInfo> audioStreams;
        await Logger.AddLog($"Video found: {video.Title}");
        try
        {
            streamManifest = await _youtube.Videos.Streams.GetManifestAsync(video.Id);
            audioStreams = streamManifest.GetAudioOnlyStreams().ToList();
            if (!audioStreams.Any())
                throw new AudioDownloadException("No audio stream available");
        }
        catch (Exception ex)
        {
            throw new AudioDownloadException(ex.Message);
        }

        YoutubeExplode.Videos.Streams.IAudioStreamInfo audioStreamInfo = audioStreams.Where(S => S.Bitrate.BitsPerSecond == audioStreams.Max(s => s.Bitrate.BitsPerSecond)).First();

        Stopwatch watch = new();
        watch.Start();

        string tempMp3Path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mp3");
        try
        {
            await _youtube.Videos.Streams.DownloadAsync(audioStreamInfo, tempMp3Path);
            watch.Stop();
            await Logger.AddLog($"Download completed in {watch.Elapsed}");

            string? convertedPath = await FfmpegInteractor.ConvertMp3ToPcm(tempMp3Path, outputFilePath);

            if (convertedPath == null || !File.Exists(convertedPath))
                throw new PCMConvertetionException();

            return true;
        }
        finally
        {
            if (File.Exists(tempMp3Path))
                try { File.Delete(tempMp3Path); } catch { }
        }
    }

    private async Task<bool> DownloadWithYoutubeDLSharp(string videoLink, string outputFilePath)
    {
        _ytdl.YoutubeDLPath = YoutubeDLPath;

        string tempMp3Path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mp3");

        OptionSet options = new()
        {
            Output = tempMp3Path,
            Format = "bestaudio"
        };

        Stopwatch watch = new();
        watch.Start();
        RunResult<string> result = await _ytdl.RunVideoDownload(videoLink, overrideOptions: options);
        watch.Stop();

        try
        {
            if (result.Success)
            {
                await Logger.AddLog($"download video taken {watch.Elapsed}");

                string? convertedPath = await FfmpegInteractor.ConvertMp3ToPcm(tempMp3Path, outputFilePath);

                if (convertedPath == null || !File.Exists(convertedPath))
                    throw new PCMConvertetionException();

                return true;
            }
            else
            {
                List<string> errors = result.ErrorOutput?
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList() ?? new List<string>();

                List<string> actualErrors = errors
                    .Where(s => s.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                string errorMessage = actualErrors.Count > 0
                    ? string.Join(" | ", actualErrors)
                    : errors.LastOrDefault() ?? "Unknown error";
                throw new AudioDownloadException($"YoutubeDLSharp download failed: {errorMessage}");
            }
        }
        finally
        {
            if (File.Exists(tempMp3Path))
                try { File.Delete(tempMp3Path); } catch { }
        }
    }

    public async Task<List<bool>?> DownloadSongsAsync(List<Song> songs, int maxConcurrency = 5)
    {
        if (songs == null || songs.Count == 0)
            return null;

        using SemaphoreSlim semaphore = new(maxConcurrency, maxConcurrency);
        List<Task<bool>> downloadTasks = new();

        foreach (Song song in songs)
        {
            downloadTasks.Add(DownloadSongWithSemaphoreAsync(song, semaphore, current_working_provider));
        }

        bool[] results = await Task.WhenAll(downloadTasks);
        return results.ToList();
    }

    private async Task<bool> DownloadSongWithSemaphoreAsync(Song song, SemaphoreSlim semaphore, Provider provider)
    {
        await semaphore.WaitAsync();
        try
        {
            return await DownloadAsync(song, provider);
        }
        catch (Exception ex)
        {
            await Logger.AddLog($"Error downloading song {song.Name}: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, exception: ex);
        }
        finally
        {
            semaphore.Release();
        }
        return false;
    }

}
