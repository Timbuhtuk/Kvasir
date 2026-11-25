using Entities.Models;

namespace Application.Interfaces;

/// <summary>
/// Repository interface for Song entity
/// </summary>
public interface ISongRepository : IGenericRepository<Song>
{
    /// <summary>
    /// Gets a list of popular songs ordered by view count
    /// </summary>
    /// <param name="limit">Maximum number of songs to return</param>
    /// <returns>List of popular songs</returns>
    /// <exception cref="System.ArgumentException">Thrown when limit is less than 1</exception>
    /// <exception cref="Microsoft.EntityFrameworkCore.DbUpdateException">Thrown when database operation fails</exception>
    Task<List<Song>> GetPopularSongsListAsync(int limit = 10);

    /// <summary>
    /// Resolves song metadata from the catalog or an online source and saves new catalog entries.
    /// </summary>
    /// <param name="query">Song name, track link, or collection link</param>
    /// <param name="maxSongs">Maximum number of tracks that can be accepted from a collection</param>
    /// <returns>Matching songs ready to enqueue, or null if none were found</returns>
    /// <exception cref="System.ArgumentException">Thrown when the collection exceeds the available capacity</exception>
    /// <exception cref="Microsoft.EntityFrameworkCore.DbUpdateException">Thrown when database operation fails</exception>
    /// <exception cref="System.Net.Http.HttpRequestException">Thrown when online search fails</exception>
    Task<List<Song>?> GetByQueryAsync(string query, int maxSongs = 1000);

    /// <summary>
    /// Gets file stream for a song. Checks if song is downloaded and file exists.
    /// </summary>
    /// <param name="song">Song entity to get file stream for</param>
    /// <returns>File stream if song is downloaded and file exists, null otherwise</returns>
    /// <exception cref="System.ArgumentNullException">Thrown when song is null</exception>
    /// <exception cref="System.IO.FileNotFoundException">Thrown when file does not exist</exception>
    /// <exception cref="System.IO.IOException">Thrown when file cannot be opened</exception>
    /// <exception cref="System.UnauthorizedAccessException">Thrown when access to file is denied</exception>
    Task<Stream?> GetFileStreamAsync(Song song);

    /// <summary>
    /// Increments the view count for a song by 1
    /// </summary>
    /// <param name="song">Song entity to increment views for</param>
    /// <returns>True if song was found and updated, false otherwise</returns>
    /// <exception cref="System.ArgumentNullException">Thrown when song is null</exception>
    /// <exception cref="Microsoft.EntityFrameworkCore.DbUpdateException">Thrown when database operation fails</exception>
    Task<bool> IncrementViewsAsync(Song song);
}

