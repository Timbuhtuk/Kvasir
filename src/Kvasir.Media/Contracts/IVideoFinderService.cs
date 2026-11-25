using System.Runtime.CompilerServices;

namespace Kvasir.Media;

public interface IVideoFinderService
{
    Task<List<FinderSongDTO>?> Find(string query, bool returnSong = false);
    Task<string?> GetPlaylistName(string url);
    async IAsyncEnumerable<IReadOnlyList<FinderSongDTO>> FindImportBatchesAsync(string query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var tracks = await Find(query, true);
        cancellationToken.ThrowIfCancellationRequested();
        if (tracks is { Count: > 0 }) yield return tracks;
    }
}
