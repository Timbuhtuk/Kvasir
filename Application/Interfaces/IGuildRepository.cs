using Entities.Models;

namespace Application.Interfaces;

/// <summary>
/// Repository interface for Guild entity
/// </summary>
public interface IGuildRepository : IGenericRepository<Guild>
{
    Task<Guild?> GetByDiscordIdAsync(ulong discordId);
    Task<Guild?> GetByDiscordIdWithPlaylistsAsync(ulong discordId);
}

