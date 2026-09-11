using Entities.Models;

namespace Application.Interfaces;

/// <summary>
/// Repository interface for Playlist entity
/// </summary>
public interface IPlaylistRepository : IGenericRepository<Playlist>
{
    /// <summary>
    /// Gets a playlist by its name (case-insensitive)
    /// </summary>
    /// <param name="name">Playlist name</param>
    /// <returns>Playlist if found, null otherwise</returns>
    /// <exception cref="System.ArgumentNullException">Thrown when name is null</exception>
    /// <exception cref="System.ArgumentException">Thrown when name is empty</exception>
    /// <exception cref="Microsoft.EntityFrameworkCore.DbUpdateException">Thrown when database operation fails</exception>
    Task<Playlist?> GetByNameAsync(string name);

    /// <summary>
    /// Gets all public playlists as a queryable collection
    /// </summary>
    /// <returns>Queryable collection of public playlists</returns>
    /// <exception cref="Microsoft.EntityFrameworkCore.DbUpdateException">Thrown when database operation fails</exception>
    Task<IQueryable<Playlist>> GetPublicPlaylistsAsync();

    /// <summary>
    /// Gets a playlist by ID with related songs loaded
    /// </summary>
    /// <param name="id">Playlist ID</param>
    /// <returns>Playlist with songs if found, null otherwise</returns>
    /// <exception cref="Microsoft.EntityFrameworkCore.DbUpdateException">Thrown when database operation fails</exception>
    Task<Playlist?> GetWithSongsAsync(int id);

    /// <summary>
    /// Removes a playlist by its ID
    /// </summary>
    /// <param name="id">Playlist ID to remove</param>
    /// <returns>True if playlist was found and removed, false if playlist was not found</returns>
    /// <exception cref="Microsoft.EntityFrameworkCore.DbUpdateException">Thrown when database operation fails</exception>
    Task<bool> RemoveByIdAsync(int id);
}

