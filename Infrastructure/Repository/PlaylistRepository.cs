using Application.Interfaces;
using Entities.Models;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repository;

/// <summary>
/// Repository implementation for Playlist entity
/// </summary>
public class PlaylistRepository(DiscordMusicDBContext context) : GenericRepository<Playlist>(context), IPlaylistRepository
{
    public override async Task<Playlist?> GetByIdAsync(int? id)
    {
        if (id == null)
            return null;

        return await context.Playlists
            .Include(p => p.Songs)
            .Include(p => p.Guilds)
            .FirstOrDefaultAsync(p => p.Id == id);
    }



    public async Task<IQueryable<Playlist>> GetPublicPlaylistsAsync()
    {
        return await Task.FromResult(
            context.Playlists
                .Where(p => p.IsPublic)
                .AsQueryable());
    }

    public async Task<Playlist?> GetWithSongsAsync(int id)
    {
        return await context.Playlists
            .Include(p => p.Songs)
            .FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<Playlist?> GetByNameAsync(string name)
    {
        return await context.Playlists
            .Include(p => p.Songs)
            .FirstOrDefaultAsync(p => p.Name.ToLower().Trim() == name.ToLower().Trim());
    }
}

