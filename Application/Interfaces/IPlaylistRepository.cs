using Entities.Models;

namespace Application.Interfaces;

/// <summary>
/// Repository interface for Playlist entity
/// </summary>
public interface IPlaylistRepository : IGenericRepository<Playlist>
{
    Task<Playlist?> GetByNameAsync(string name);
    Task<IQueryable<Playlist>> GetPublicPlaylistsAsync();
    Task<Playlist?> GetWithSongsAsync(int id);
}

