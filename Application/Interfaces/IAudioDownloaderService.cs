using Entities.Models;

namespace Application.Interfaces;

public interface IAudioDownloaderService
{
    /// <summary>
    /// Downloads audio file from video link and saves it to the specified path.
    /// Automatically creates output directory if it doesn't exist.
    /// Uses fallback provider if primary provider fails.
    /// </summary>
    /// <param name="videoLink">URL to the video</param>
    /// <param name="outputFilePath">Full path where the PCM file should be saved</param>
    /// <returns>True if download succeeded, false otherwise</returns>
    /// <remarks>
    /// This method handles all exceptions internally and returns false on failure.
    /// If primary provider fails, automatically tries fallback provider.
    /// All exceptions are caught and logged, method never throws exceptions to caller.
    /// Invalid parameters (null or empty) result in false return value, not exceptions.
    /// </remarks>
    Task<bool> DownloadAsync(string videoLink, string outputFilePath);

    /// <summary>
    /// Downloads audio file for the given song (uses song.Link and song.FilePath).
    /// Automatically creates output directory if it doesn't exist.
    /// Uses fallback provider if primary provider fails.
    /// </summary>
    /// <param name="song">Song entity with filled Link and FilePath properties</param>
    /// <returns>True if download succeeded, false otherwise</returns>
    /// <exception cref="System.NullReferenceException">Thrown when song is null</exception>
    /// <remarks>
    /// This method handles download exceptions internally and returns false on failure.
    /// If primary provider fails, automatically tries fallback provider.
    /// Download exceptions are caught and logged, but NullReferenceException is not caught if song is null.
    /// </remarks>
    Task<bool> DownloadAsync(Song song);

    /// <summary>
    /// Downloads multiple songs in parallel with limited concurrency.
    /// Each song download is independent - failures don't stop other downloads.
    /// </summary>
    /// <param name="songs">List of songs to download (must have FilePath set)</param>
    /// <param name="maxConcurrency">Maximum number of concurrent downloads (default: 3)</param>
    /// <returns>List of download results (true for success, false for failure) or null if songs list is null or empty</returns>
    /// <remarks>
    /// Individual download failures are caught and logged, but don't affect other downloads.
    /// The returned list contains results in the same order as input songs list.
    /// Each download uses the same exception handling as DownloadAsync(Song).
    /// Returns null if songs is null or empty, does not throw exceptions for invalid parameters.
    /// </remarks>
    Task<List<bool>?> DownloadSongsAsync(List<Song> songs, int maxConcurrency = 3);
}

