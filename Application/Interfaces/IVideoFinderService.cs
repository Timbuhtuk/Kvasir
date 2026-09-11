using Entities.Models;

namespace Application.Interfaces;

public interface IVideoFinderService
{
    Task<List<FinderSongDTO>?> Find(string query, bool returnSong = false);
    Task<string?> GetPlaylistName(string url);
}
