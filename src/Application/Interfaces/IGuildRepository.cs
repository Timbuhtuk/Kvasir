using Entities.Models;

namespace Application.Interfaces;

/// <summary>
/// Repository interface for Guild entity
/// </summary>
public interface IGuildRepository : IGenericRepository<Guild>
{
    /// <summary>
    /// Gets a guild by its Discord ID
    /// </summary>
    /// <param name="discordId">Discord guild ID</param>
    /// <returns>Guild if found, null otherwise</returns>
    /// <exception cref="Microsoft.EntityFrameworkCore.DbUpdateException">Thrown when database operation fails</exception>
    Task<Guild?> GetByDiscordIdAsync(ulong discordId);

    /// <summary>
    /// Gets a guild by its Discord ID with related playlists loaded
    /// </summary>
    /// <param name="discordId">Discord guild ID</param>
    /// <returns>Guild with playlists if found, null otherwise</returns>
    /// <exception cref="Microsoft.EntityFrameworkCore.DbUpdateException">Thrown when database operation fails</exception>
    Task<Guild?> GetByDiscordIdWithPlaylistsAsync(ulong discordId);
}

