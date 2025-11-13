using Entities.Models;

namespace Application.Interfaces;

/// <summary>
/// Repository interface for Song entity
/// </summary>
public interface ISongRepository : IGenericRepository<Song>
{
    Task<Song?> GetByLinkAsync(string link);
    Task<Song?> GetByFilePathAsync(string filePath);
    Task<Song?> GetByNameOrLinkAsync(string nameOrLink);
    Task<IQueryable<Song>> GetByNameAsync(string name);
    Task<IQueryable<Song>> GetByAuthorNameAsync(string authorName);
    Task<IQueryable<Song>> GetPopularSongsAsync(int limit = 10);
    Task<List<Song>> GetPopularSongsListAsync(int limit = 10);
}

