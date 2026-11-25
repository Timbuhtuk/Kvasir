using Kvasir.Media;
using Application.Interfaces;
using Entities.Models;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repository;

public sealed class SongRepository(DiscordMusicDBContext context, IVideoFinderService finder, IMediaCache cache) : GenericRepository<Song>(context), ISongRepository
{
    private static readonly SemaphoreSlim _catalog = new(1, 1);
    public override Task<Song?> GetByIdAsync(int? id) => id == null ? Task.FromResult<Song?>(null) : context.Songs.FirstOrDefaultAsync(s => s.Id == id);
    public Task<List<Song>> GetPopularSongsListAsync(int limit = 10) => context.Songs.AsNoTracking().OrderByDescending(s => s.Views).Take(limit).ToListAsync();
    public async Task<List<Song>?> GetByQueryAsync(string query, int maxSongs = 1000)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        string key = query.Trim().ToLower();
        Song? existing = await context.Songs.AsNoTracking().FirstOrDefaultAsync(s => s.Link == query || s.Name.ToLower().Trim() == key);
        List<Song> songs = new();
        if (existing != null) songs.Add(existing);
        else
        {
            var found = await finder.Find(query, true);
            if (found == null || found.Count == 0) return null;
            if (found.Count > maxSongs) throw new ArgumentException($"В подборке {found.Count} треков, а доступно мест: {maxSongs}. Увеличьте лимит в режиме компании или добавьте отдельные треки.");
            await _catalog.WaitAsync();
            try
            {
                foreach (var dto in found)
                {
                    Song? song = await context.Songs.FirstOrDefaultAsync(s => dto.Link != null && s.Link == dto.Link || s.Name == dto.Name && s.AuthorName == dto.AuthorName);
                    if (song == null)
                    {
                        song = new Song { Name = dto.Name, AuthorName = dto.AuthorName, Duration = dto.Duration, Link = dto.Link };
                        context.Songs.Add(song); await context.SaveChangesAsync();
                    }
                    songs.Add(song);
                }
            }
            finally { _catalog.Release(); }
        }
        return songs;
    }
    public async Task<Stream?> GetFileStreamAsync(Song song)
    {
        try { return await cache.OpenAsync(song); }
        catch (IOException) { return null; }
    }
    public async Task<bool> IncrementViewsAsync(Song song) => await context.Songs.Where(s => s.Id == song.Id).ExecuteUpdateAsync(update => update.SetProperty(s => s.Views, s => s.Views + 1)) > 0;
}
