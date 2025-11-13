using System.Collections.Concurrent;
using Entities.Models;

namespace Application.Interfaces;

/// <summary>
/// Service interface for audio downloading operations
/// </summary>
public interface IAudioDownloaderService
{
    /// <summary>
    /// Download audio from youtube video and convert to PCM file
    /// </summary>
    Task<Song?> Download(Song song, ISongRepository songRepository, string musicFolderPath, Application.Services.Provider provider = Application.Services.Provider.YoutubeExplode);

    /// <summary>
    /// Download audios from youtube video
    /// </summary>
    Task<List<Song>> Download(List<Song> songs, ISongRepository songRepository, string musicFolderPath);

    /// <summary>
    /// Download audios from youtube video in parallel and add to ConcurrentDictionary as they complete
    /// </summary>
    /// <param name="songs">List of songs to download</param>
    /// <param name="songRepository">Repository for songs</param>
    /// <param name="musicFolderPath">Path to music folder</param>
    /// <param name="downloadedSongs">ConcurrentDictionary where key is index in original list and value is downloaded song (null if failed)</param>
    Task DownloadAsync(List<Song> songs, ISongRepository songRepository, string musicFolderPath, ConcurrentDictionary<int, Song?> downloadedSongs);
}

