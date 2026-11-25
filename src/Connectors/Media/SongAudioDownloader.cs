using Application.Interfaces;
using Entities.Models;
using Kvasir.Media;

namespace Connectors.Media;

public sealed class SongAudioDownloader(MediaActivity activity) : AudioDownloaderService(activity), IAudioDownloaderService
{
    public Task<bool> DownloadAsync(Song song, CancellationToken cancellationToken = default) =>
        DownloadAsync(song.Link, song.FilePath, cancellationToken);

    public async Task<List<bool>?> DownloadSongsAsync(List<Song> songs, int maxConcurrency = 5)
    {
        if (songs == null || songs.Count == 0)
            return null;

        using SemaphoreSlim semaphore = new(maxConcurrency, maxConcurrency);
        List<Task<bool>> downloadTasks = new();

        foreach (Song song in songs)
        {
            downloadTasks.Add(DownloadSongWithSemaphoreAsync(song, semaphore));
        }

        bool[] results = await Task.WhenAll(downloadTasks);
        return results.ToList();
    }

    private async Task<bool> DownloadSongWithSemaphoreAsync(Song song, SemaphoreSlim semaphore)
    {
        await semaphore.WaitAsync();
        try
        {
            return await DownloadAsync(song);
        }
        catch (Exception ex)
        {
            await Logging.Logger.AddLog($"Error downloading song {song.Name}: {ex.Message}", exception: ex);
        }
        finally
        {
            semaphore.Release();
        }
        return false;
    }

}
