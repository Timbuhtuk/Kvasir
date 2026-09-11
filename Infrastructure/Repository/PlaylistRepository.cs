using Application.Interfaces;
using Entities.Models;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repository;

/// <summary>
/// Repository implementation for Playlist entity
/// </summary>
public class PlaylistRepository(DiscordMusicDBContext context) : GenericRepository<Playlist>(context), IPlaylistRepository
{
    public override async Task<Playlist?> GetByIdAsync(int? id) {
        if (id == null)
            return null;

        return await context.Playlists
            .Include(p => p.Songs)
            .Include(p => p.Guilds)
            .FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<IQueryable<Playlist>> GetPublicPlaylistsAsync() =>
        await Task.FromResult(
            context.Playlists
                .Where(p => p.IsPublic)
                .AsQueryable());

    public async Task<Playlist?> GetWithSongsAsync(int id) =>
        await context.Playlists
            .Include(p => p.Songs)
            .FirstOrDefaultAsync(p => p.Id == id);

    public async Task<Playlist?> GetByNameAsync(string name) =>
        await context.Playlists
            .Include(p => p.Songs)
            .FirstOrDefaultAsync(p => p.Name.ToLower().Trim() == name.ToLower().Trim());

    public async Task<bool> RemoveByIdAsync(int id)
    {
        Playlist? playlist = await GetByIdAsync(id);
        if (playlist == null)
            return false;

        await RemoveAsync(playlist);
        return true;
    }
}
