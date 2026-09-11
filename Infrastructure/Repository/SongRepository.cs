using Application.Interfaces;
using Entities.Models;
using Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Repository;

/// <summary>
/// Repository implementation for Song entity
/// </summary>
public class SongRepository(
    DiscordMusicDBContext context,
    IVideoFinderService videoFinder,
    IAudioDownloaderService audioDownloader,
    IConfiguration configuration) : GenericRepository<Song>(context), ISongRepository
{
    private SemaphoreSlim SongDownloadCompletedSemaphore = new(1, 1);

    private async Task OnSongDownloadCompletedAsync(Song song)
    {
        await SongDownloadCompletedSemaphore.WaitAsync();
        try
        {
            Song? dbEntity = await GetByIdAsync(song.Id);
            if (dbEntity == null) throw new NullReferenceException("OnSongDownloadCompleted called for entity which no exist in DB");
            dbEntity.IsDownloaded = true;
            await UpdateAsync(dbEntity);

            await Logger.AddLog($"{song.Name} - marked as downloaded", Microsoft.Extensions.Logging.LogLevel.Information);
        }
        catch (Exception ex)
        {
            await Logger.AddLog($"Error handling song download completion for {song.Name}: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, exception: ex);
        }
        finally { SongDownloadCompletedSemaphore.Release(); }
    }

    public override async Task<Song?> GetByIdAsync(int? id)
    {
        if (id == null)
            return null;

        return await context.Songs
            .Include(s => s.Playlists)
            .FirstOrDefaultAsync(s => s.Id == id);
    }

    public async Task<List<Song>> GetPopularSongsListAsync(int limit = 10) =>
        await context.Songs
            .OrderByDescending(s => s.Views)
            .Take(limit)
            .ToListAsync();

    private async Task<Song?> Find(FinderSongDTO dto)
        => await context.Songs.FirstOrDefaultAsync(s => (s.Name.ToLower().Trim() == dto.Name.ToLower().Trim() && s.AuthorName == dto.AuthorName) || s.Link == dto.Link);


    public async Task<List<Song>?> GetByQueryAsync(string query)
    {
        //
        //            I
        //
        Song? existingSong = await context.Songs.FirstOrDefaultAsync(s => s.Name.ToLower().Trim() == query.ToLower().Trim() || s.Link == query);
        if (existingSong != null)
        {
            await Logger.AddLog("used saved track instead downloading");
            return new() { existingSong };
        }
        List<Song>? existingPlaylist = (await context.Playlists.FirstOrDefaultAsync(s => s.Name.ToLower().Trim() == query.ToLower().Trim()))?.Songs.ToList();
        if (existingPlaylist != null)
        {
            await Logger.AddLog("used saved tracks instead downloading");
            return existingPlaylist;
        }
        //
        //            II
        //
        List<Song>? result = new();
        List<FinderSongDTO>? foundSongs = await videoFinder.Find(query, true);

        if (foundSongs == null) return null;

        for (int q = 0; q < foundSongs.Count;)
        {
            Song? dbEntity = await Find(foundSongs[q]);
            if (dbEntity != null)
            {
                foundSongs.RemoveAt(q);
                result.Add(dbEntity);
            }
            else
                ++q;
        }

        if (foundSongs.Count == 0)
            return result;

        //
        //           III
        //
        List<Song> songsToDownload = new();

        string musicFolder = configuration["music_client:music_folder"] ?? throw new Exception("Music folder does not set in configuration.");

        foreach (var songDto in foundSongs)
        {
            string fileName = $"{Guid.NewGuid()}.pcm";
            string filePath = Path.Combine(musicFolder, fileName);

            Song newSong = new()
            {
                AuthorName = songDto.AuthorName,
                Duration = songDto.Duration,
                Link = songDto.Link,
                Name = songDto.Name,
                FilePath = filePath
            };

            Song addedSong = await AddAsync(newSong);
            songsToDownload.Add(addedSong);
            result.Add(addedSong);
        }

        List<bool>? downloadResults = await audioDownloader.DownloadSongsAsync(songsToDownload, maxConcurrency: 3);
        for (int q = 0; q < songsToDownload.Count; q++)
        {
            Song song = songsToDownload[q];
            bool isDownloaded = downloadResults != null && q < downloadResults.Count && downloadResults[q];

            if (isDownloaded)
            {
                await OnSongDownloadCompletedAsync(song);
                continue;
            }

            await RemoveAsync(song);
            result.RemoveAll(s => s.Id == song.Id);
            await Logger.AddLog($"Download failed for song {song.Name}, removed from DB");
        }

        return result;
    }

    public async Task<Stream?> GetFileStreamAsync(Song song)
    {
        if (song == null) throw new ArgumentNullException(nameof(song));

        Song? dbSong = await GetByIdAsync(song.Id);
        if (dbSong == null) return null;

        if (!dbSong.IsDownloaded)
        {
            await Logger.AddLog($"Song {dbSong.Name} is not marked as downloaded", Microsoft.Extensions.Logging.LogLevel.Warning);
            return null;
        }


        if (string.IsNullOrWhiteSpace(dbSong.FilePath))
        {
            await Logger.AddLog($"Song {dbSong.Name} has no file path", Microsoft.Extensions.Logging.LogLevel.Warning);
            return null;
        }

        // Check if file exists
        if (!File.Exists(dbSong.FilePath))
        {
            await Logger.AddLog($"File not found for song {dbSong.Name}: {dbSong.FilePath}", Microsoft.Extensions.Logging.LogLevel.Warning);

            dbSong.IsDownloaded = false;
            await UpdateAsync(dbSong);
            return null;
        }

        try
        {
            return new FileStream(dbSong.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex)
        {
            await Logger.AddLog($"Error opening file stream for song {dbSong.Name}: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, exception: ex);
            throw;
        }
    }

    public async Task<bool> IncrementViewsAsync(Song song)
    {
        if (song == null)
            throw new ArgumentNullException(nameof(song));

        Song? dbSong = await GetByIdAsync(song.Id);
        if (dbSong == null)
            return false;

        dbSong.Views++;
        await UpdateAsync(dbSong);
        return true;
    }
}
