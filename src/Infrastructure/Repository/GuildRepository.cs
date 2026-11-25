using Application.Interfaces;
using Entities.Models;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repository;

/// <summary>
/// Repository implementation for Guild entity
/// </summary>
public class GuildRepository(DiscordMusicDBContext context) : GenericRepository<Guild>(context), IGuildRepository
{
    public override async Task<Guild?> GetByIdAsync(int? id) {
        if (id == null)
            return null;

        return await context.Guilds
            .Include(g => g.Playlists)
            .FirstOrDefaultAsync(g => g.Id == id);
    }

    public async Task<Guild?> GetByDiscordIdAsync(ulong discordId) =>
        await context.Guilds
            .FirstOrDefaultAsync(g => g.DiscordId == discordId);

    public async Task<Guild?> GetByDiscordIdWithPlaylistsAsync(ulong discordId) =>
        await context.Guilds
            .Include(g => g.Playlists)
            .FirstOrDefaultAsync(g => g.DiscordId == discordId);
}
