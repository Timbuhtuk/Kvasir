using Application.Interfaces;
using Entities.Models;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repository;

/// <summary>
/// Repository implementation for Song entity
/// </summary>
public class SongRepository(DiscordMusicDBContext context) : GenericRepository<Song>(context), ISongRepository
{
    public override async Task<Song?> GetByIdAsync(int? id)
    {
        if (id == null)
            return null;

        return await context.Songs
            .Include(s => s.Playlists)
            .FirstOrDefaultAsync(s => s.Id == id);
    }

    public async Task<Song?> GetByLinkAsync(string link)
    {
        return await context.Songs
            .FirstOrDefaultAsync(s => s.Link == link);
    }

    public async Task<Song?> GetByFilePathAsync(string filePath)
    {
        return await context.Songs
            .FirstOrDefaultAsync(s => s.FilePath == filePath);
    }

    public async Task<IQueryable<Song>> GetByNameAsync(string name)
    {
        return await Task.FromResult(
            context.Songs
                .Where(s => s.Name.Contains(name))
                .AsQueryable());
    }

    public async Task<IQueryable<Song>> GetByAuthorNameAsync(string authorName)
    {
        return await Task.FromResult(
            context.Songs
                .Where(s => s.AuthorName.Contains(authorName))
                .AsQueryable());
    }

    public async Task<IQueryable<Song>> GetPopularSongsAsync(int limit = 10)
    {
        return await Task.FromResult(
            context.Songs
                .OrderByDescending(s => s.Views)
                .Take(limit)
                .AsQueryable());
    }

    public async Task<Song?> GetByNameOrLinkAsync(string nameOrLink)
    {
        return await context.Songs
            .FirstOrDefaultAsync(s => s.Name.ToLower().Trim() == nameOrLink.ToLower().Trim() || s.Link == nameOrLink);
    }

    public async Task<List<Song>> GetPopularSongsListAsync(int limit = 10)
    {
        return await context.Songs
            .OrderByDescending(s => s.Views)
            .Take(limit)
            .ToListAsync();
    }
}

