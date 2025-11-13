using Application.Interfaces;
using CS_Discord_Bot;
using Entities.Enums;
using Entities.Models;
using Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using YoutubeDLSharp;
using YoutubeDLSharp.Options;
using YoutubeExplode;

namespace Application.Services;

/// <summary>
/// Enum with names of libs for downloading
/// </summary>
public enum Provider
{
    YoutubeExplode,
    YoutubeDLSharp
}

[LogCategory(LogCategory.AudioDownload)]
public class AudioDownloaderService : IAudioDownloaderService
{
    private readonly YoutubeDL _ytdl;
    private readonly YoutubeClient _youtube;
    private const string YoutubeDLPath = "appdata\\yt-dlp.exe";
    private const Provider current_working_provider = Provider.YoutubeExplode;

    public AudioDownloaderService()
    {
        _ytdl = new YoutubeDL();
        _youtube = new YoutubeClient();
    }

    /// <summary>
    /// Download audio from youtube video and convert to PCM file
    /// </summary>
    /// <param name="song">song object instance</param>
    /// <param name="songRepository">repository for songs</param>
    /// <param name="musicFolderPath">path to music folder</param>
    /// <param name="provider">determinates witch library use to download the audio</param>
    /// <returns>Song object instance</returns>
    public async Task<Song?> Download(Song song, ISongRepository songRepository, string musicFolderPath, Provider provider = current_working_provider)
    {
        if (song.Link == null)
        {
            await Logger.AddLog("song link was null", Microsoft.Extensions.Logging.LogLevel.Error);
            return null;
        }

        // Проверяем, есть ли уже PCM файл
        if (!string.IsNullOrEmpty(song.FilePath) && File.Exists(song.FilePath))
        {
            await Logger.AddLog($"PCM file already exists: {song.FilePath}");
            return song;
        }

        // Try primary provider first
        string? pcmFilePath = await TryDownloadWithProvider(song.Link, song, musicFolderPath, provider);
        if (pcmFilePath == null)
        {
            // If primary provider failed, try fallback provider
            Provider fallbackProvider = provider == Provider.YoutubeExplode ? Provider.YoutubeDLSharp : Provider.YoutubeExplode;
            await Logger.AddLog($"Primary provider failed, trying fallback provider: {fallbackProvider}", Microsoft.Extensions.Logging.LogLevel.Warning);
            pcmFilePath = await TryDownloadWithProvider(song.Link, song, musicFolderPath, fallbackProvider);
        }

        if (pcmFilePath == null)
        {
            await Logger.AddLog("Failed to download audio from all providers", Microsoft.Extensions.Logging.LogLevel.Error);
            return null;
        }

        // Проверяем, что файл действительно был создан
        if (!File.Exists(pcmFilePath))
        {
            await Logger.AddLog($"PCM file was not created: {pcmFilePath}", Microsoft.Extensions.Logging.LogLevel.Error);
            return null;
        }

        // Обновляем путь к файлу в БД
        song.FilePath = pcmFilePath;
        await songRepository.UpdateAsync(song);

        await Logger.AddLog($"PCM file saved: {pcmFilePath}");
        return song;
    }

    /// <summary>
    /// Attempts to download audio using specified provider
    /// </summary>
    private async Task<string?> TryDownloadWithProvider(string url, Song song, string musicFolderPath, Provider provider)
    {
        try
        {
            if (provider == Provider.YoutubeExplode)
            {
                return await DownloadWithYoutubeExplode(url, song, musicFolderPath);
            }
            else if (provider == Provider.YoutubeDLSharp)
            {
                return await DownloadWithYoutubeDLSharp(url, song, musicFolderPath);
            }
        }
        catch (Exception ex)
        {
            await Logger.AddLog($"Download failed with {provider}: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error);
        }
        return null;
    }

    /// <summary>
    /// Downloads audio using YoutubeExplode provider and converts to PCM file
    /// </summary>
    private async Task<string?> DownloadWithYoutubeExplode(string url, Song song, string musicFolderPath)
    {
        YoutubeExplode.Videos.Video video = await _youtube.Videos.GetAsync(url);
        await Logger.AddLog($"Video found: {video.Title}");

        YoutubeExplode.Videos.Streams.StreamManifest streamManifest = await _youtube.Videos.Streams.GetManifestAsync(video.Id);
        IReadOnlyList<YoutubeExplode.Videos.Streams.IAudioStreamInfo> audioStreams = streamManifest.GetAudioOnlyStreams().ToList();

        if (!audioStreams.Any())
        {
            await Logger.AddLog($"No audio stream available", Microsoft.Extensions.Logging.LogLevel.Error);
            throw new Exception("No audio stream available");
        }

        YoutubeExplode.Videos.Streams.IAudioStreamInfo audioStreamInfo = audioStreams.Where(S => S.Bitrate.BitsPerSecond == audioStreams.Max(s => s.Bitrate.BitsPerSecond)).First();

        Stopwatch watch = new();
        watch.Start();

        // Скачиваем во временный файл
        string tempMp3Path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mp3");
        try
        {
            await _youtube.Videos.Streams.DownloadAsync(audioStreamInfo, tempMp3Path);
            watch.Stop();
            await Logger.AddLog($"Download completed in {watch.Elapsed}");

            // Создаем папку для музыки, если её нет
            if (!Directory.Exists(musicFolderPath))
            {
                Directory.CreateDirectory(musicFolderPath);
            }

            // Генерируем имя файла
            string fileName = $"{song.Id}.pcm";
            string pcmFilePath = Path.Combine(musicFolderPath, fileName);

            // Конвертируем в PCM файл
            string? convertedPath = await FfmpegInteractor.ConvertMp3ToPcm(tempMp3Path, pcmFilePath);
            
            if (convertedPath == null || !File.Exists(convertedPath))
            {
                await Logger.AddLog($"FFmpeg conversion failed or file was not created: {convertedPath}", Microsoft.Extensions.Logging.LogLevel.Error);
                return null;
            }

            return convertedPath;
        }
        finally
        {
            // Удаляем временный MP3 файл
            if (File.Exists(tempMp3Path))
            {
                try { File.Delete(tempMp3Path); } catch { }
            }
        }
    }

    /// <summary>
    /// Downloads audio using YoutubeDLSharp provider and converts to PCM file
    /// </summary>
    private async Task<string?> DownloadWithYoutubeDLSharp(string url, Song song, string musicFolderPath)
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
        RunResult<string> result = await _ytdl.RunVideoDownload(url, overrideOptions: options);
        watch.Stop();
        await Logger.AddLog($"download video taken {watch.Elapsed}");

        try
        {
            if (result.Success)
            {
                await Logger.AddLog("Download completed successfully!");

                // Создаем папку для музыки, если её нет
                if (!Directory.Exists(musicFolderPath))
                {
                    Directory.CreateDirectory(musicFolderPath);
                }

                // Генерируем имя файла
                string fileName = $"{song.Id}.pcm";
                string pcmFilePath = Path.Combine(musicFolderPath, fileName);

                // Конвертируем в PCM файл
                string? convertedPath = await FfmpegInteractor.ConvertMp3ToPcm(tempMp3Path, pcmFilePath);
                
                if (convertedPath == null)
                {
                    await Logger.AddLog("FFmpeg conversion failed", Microsoft.Extensions.Logging.LogLevel.Error);
                    return null;
                }

                return convertedPath;
            }
            else
            {
                string errorMessage = result.ErrorOutput != null && result.ErrorOutput.Count() > 0
                    ? result.ErrorOutput[0]
                    : "Unknown error";
                await Logger.AddLog($"Download failed: {errorMessage}", Microsoft.Extensions.Logging.LogLevel.Error);
                throw new Exception($"YoutubeDLSharp download failed: {errorMessage}");
            }
        }
        finally
        {
            // Удаляем временный MP3 файл
            if (File.Exists(tempMp3Path))
            {
                try { File.Delete(tempMp3Path); } catch { }
            }
        }
    }

    /// <summary>
    /// Download audios from youtube video
    /// </summary>
    /// <param name="songs">list of song object instances</param>
    /// <param name="songRepository">repository for songs</param>
    /// <param name="musicFolderPath">path to music folder</param>
    /// <returns>song object instances</returns>
    public async Task<List<Song>> Download(List<Song> songs, ISongRepository songRepository, string musicFolderPath)
    {
        List<Song> result = new();
        foreach (Song song in songs)
        {
            Song? downloaded = await Download(song, songRepository, musicFolderPath);
            if (downloaded != null)
                result.Add(downloaded);
        }
        return result;
    }

    /// <summary>
    /// Download audios from youtube video in parallel and add to ConcurrentDictionary as they complete
    /// </summary>
    /// <param name="songs">List of songs to download</param>
    /// <param name="songRepository">Repository for songs</param>
    /// <param name="musicFolderPath">Path to music folder</param>
    /// <param name="downloadedSongs">ConcurrentDictionary where key is index in original list and value is downloaded song (null if failed)</param>
    public async Task DownloadAsync(List<Song> songs, ISongRepository songRepository, string musicFolderPath, ConcurrentDictionary<int, Song?> downloadedSongs)
    {
        // Ограничиваем количество одновременных загрузок для избежания перегрузки
        const int maxConcurrentDownloads = 5;
        SemaphoreSlim semaphore = new(maxConcurrentDownloads, maxConcurrentDownloads);

        List<Task> downloadTasks = new();
        
        for (int i = 0; i < songs.Count; i++)
        {
            int index = i; // Захватываем индекс для замыкания
            Song song = songs[i];
            
            // Если файл уже существует, сразу добавляем в словарь
            if (!string.IsNullOrEmpty(song.FilePath) && File.Exists(song.FilePath))
            {
                downloadedSongs[index] = song;
                continue;
            }
            
            Task downloadTask = Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    Song? downloaded = await Download(song, songRepository, musicFolderPath);
                    downloadedSongs[index] = downloaded;
                }
                catch (Exception ex)
                {
                    await Logger.AddLog($"Failed to download song at index {index}: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error);
                    downloadedSongs[index] = null;
                }
                finally
                {
                    semaphore.Release();
                }
            });
            
            downloadTasks.Add(downloadTask);
        }

        // Ждем завершения всех задач
        await Task.WhenAll(downloadTasks);
    }
}

