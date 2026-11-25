using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using SpotifyAPI.Web;

namespace Kvasir.Media;

public class VideoFinderService : IVideoFinderService
{
    private readonly MediaActivity _activity;
    private readonly SpotifyInteractor _spotifyInteractor;
    private readonly Func<string, Task<List<FinderSongDTO>>> _findOnYoutube;
    private readonly Func<Uri, Task<string>>? _downloadSpotifyEmbed;

    public VideoFinderService(IConfiguration configuration, MediaActivity? activity = null)
        : this(new SpotifyInteractor(configuration), async query => YtDlpMetadata.MapSongs(await YtDlpMetadata.FetchAsync(query)), activity: activity) { }

    internal VideoFinderService(SpotifyInteractor spotifyInteractor, Func<string, Task<List<FinderSongDTO>>> findOnYoutube,
        Func<Uri, Task<string>>? downloadSpotifyEmbed = null, MediaActivity? activity = null)
    {
        _activity = activity ?? new();
        _spotifyInteractor = spotifyInteractor;
        _findOnYoutube = findOnYoutube;
        _downloadSpotifyEmbed = downloadSpotifyEmbed;
    }

    public Task<List<FinderSongDTO>?> Find(string query, bool return_song = false) =>
        _activity.RunAsync(query, MediaStage.Search, operation => FindCoreAsync(query, operation));

    private async Task<List<FinderSongDTO>?> FindCoreAsync(string query, MediaOperation operation)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        string input = query.Trim();
        if (ParseSpotifyLink(input) is { } spotify)
            return await FindOnSpotifyAsync(spotify.Kind, spotify.Id, operation);
        return await _findOnYoutube(input);
    }

    public async Task<List<FinderSongDTO>?> FindOnSpotifyAsync(string query, bool return_song = false) =>
        ParseSpotifyLink(query.Trim()) is { } link ? await _activity.RunAsync(query, MediaStage.Search, op => FindOnSpotifyAsync(link.Kind, link.Id, op)) : null;

    private async Task<List<FinderSongDTO>?> FindOnSpotifyAsync(string kind, string id, MediaOperation operation)
    {
        operation.Report(MediaStage.Search, "Spotify");
        if (kind == "track")
        {
            if (!_spotifyInteractor.IsEnabled) return null;
            try
            {
                var spotify = await _spotifyInteractor.GetClientAsync();
                FullTrack track = await spotify.Tracks.Get(id);
                var song = await MatchSpotifyTrackAsync(new(track.Name, track.Artists.Select(artist => artist.Name).ToArray()));
                return song == null ? null : [song];
            }
            catch (Exception ex)
            {
                throw new MediaSourceException(ex.Message);
            }
        }

        List<FinderSongDTO> songs = [];
        await foreach (var song in FindSpotifyCollectionTracksAsync(kind, id, CancellationToken.None))
        {
            songs.Add(song);
            operation.Report(MediaStage.Search, "Spotify", matches: songs.Count);
        }
        return songs;
    }

    public async IAsyncEnumerable<IReadOnlyList<FinderSongDTO>> FindImportBatchesAsync(string query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string input = query.Trim();
        if (ParseSpotifyLink(input) is { } spotify && spotify.Kind != "track")
        {
            await using var matches = FindSpotifyCollectionTracksAsync(spotify.Kind, spotify.Id, cancellationToken).GetAsyncEnumerator(cancellationToken);
            int found = 0;
            while (await _activity.RunAsync(input, MediaStage.Search, operation =>
            {
                operation.Report(MediaStage.Search, "Spotify", matches: found);
                return matches.MoveNextAsync().AsTask();
            }))
            {
                found++;
                yield return new[] { matches.Current };
            }
            yield break;
        }

        var tracks = await Find(YtDlpMetadata.GetPlaylistUrl(input) ?? input, true);
        cancellationToken.ThrowIfCancellationRequested();
        if (tracks is { Count: > 0 }) yield return tracks;
    }

    private async IAsyncEnumerable<FinderSongDTO> FindSpotifyCollectionTracksAsync(string kind, string id,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var collection = await OpenSpotifyCollectionAsync(kind, id, includeTracks: true, cancellationToken);
        if (collection == null)
            throw new MediaSourceException("Публичный плейлист или альбом Spotify недоступен.", "source-restricted");

        await foreach (var track in collection.Tracks.WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FinderSongDTO? song = await MatchSpotifyTrackAsync(track);
            cancellationToken.ThrowIfCancellationRequested();
            if (song != null) yield return song;
        }
    }

    private async Task<FinderSongDTO?> MatchSpotifyTrackAsync(SpotifyTrackMetadata track)
    {
        if (string.IsNullOrWhiteSpace(track.Name)) return null;
        string query = track.Artists.Length == 0 ? track.Name : $"{track.Name} - {track.Artists[0]}";
        FinderSongDTO? song = (await _findOnYoutube(query)).FirstOrDefault();
        if (song == null) return null;

        song.Name = track.Name;
        if (track.Artists.Length > 0) song.AuthorName = string.Join("|", track.Artists);
        return song;
    }

    public Task<string?> GetPlaylistName(string url) =>
        _activity.RunAsync(url, MediaStage.Search, _ => GetPlaylistNameAsync(url));

    private async Task<string?> GetPlaylistNameAsync(string url)
    {
        if (ParseSpotifyLink(url) is { } link && link.Kind != "track")
            return (await OpenSpotifyCollectionAsync(link.Kind, link.Id, includeTracks: false, CancellationToken.None))?.Name;
        string? youtube = YtDlpMetadata.GetPlaylistUrl(url);
        return youtube == null ? null : (await YtDlpMetadata.FetchAsync(youtube, titleOnly: true)).Title;
    }

    private async Task<SpotifyCollection?> OpenSpotifyCollectionAsync(string kind, string id, bool includeTracks, CancellationToken cancellationToken)
    {
        if (_spotifyInteractor.IsEnabled)
        {
            try
            {
                SpotifyClient spotify = await _spotifyInteractor.GetClientAsync();
                cancellationToken.ThrowIfCancellationRequested();
                if (kind == "album")
                {
                    FullAlbum album = await spotify.Albums.Get(id, cancellationToken);
                    if (!includeTracks) return new(album.Name, AsyncEnumerable.Empty<SpotifyTrackMetadata>());
                    var page = album.Tracks ?? await spotify.Albums.GetTracks(id, cancellationToken);
                    return new(album.Name, spotify.Paginate(page, cancel: cancellationToken)
                        .Select(track => new SpotifyTrackMetadata(track.Name, track.Artists.Select(artist => artist.Name).ToArray())));
                }

                FullPlaylist playlist = await spotify.Playlists.Get(id, cancellationToken);
                string? name = playlist.Name;
                if (!includeTracks && !string.IsNullOrWhiteSpace(name)) return new(name, AsyncEnumerable.Empty<SpotifyTrackMetadata>());
                var items = GetPlaylistItems(playlist);
                if (includeTracks && items?.Items != null)
                {
                    var tracks = spotify.Paginate(items, cancel: cancellationToken)
                        .Select(item => item?.Item ?? item?.Track)
                        .OfType<FullTrack>().Where(track => !track.IsLocal)
                        .Select(track => new SpotifyTrackMetadata(track.Name, track.Artists.Select(artist => artist.Name).ToArray()));
                    return new(name ?? "", tracks);
                }
            }
            catch (APIException ex) when (ex.Response?.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                // Some tokens only have access to the publicly visible embed.
            }
        }
        return await SpotifyEmbed.GetAsync(kind, id, _downloadSpotifyEmbed, cancellationToken);
    }

    private static (string Kind, string Id)? ParseSpotifyLink(string input)
    {
        if (!Uri.TryCreate(input, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") ||
            !url.Host.Equals("open.spotify.com", StringComparison.OrdinalIgnoreCase)) return null;
        string[] parts = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && (parts[0] == "embed" || parts[0].StartsWith("intl-", StringComparison.Ordinal)))
            parts = parts[1..];
        if (parts.Length != 2 || parts[0] is not ("track" or "playlist" or "album") || !parts[1].All(char.IsAsciiLetterOrDigit))
            return null;
        return (parts[0], parts[1]);
    }

    private static Paging<PlaylistTrack<IPlayableItem>>? GetPlaylistItems(FullPlaylist playlist)
    {
#pragma warning disable CS0618 // Some Spotify apps still receive the older "tracks" field.
        return playlist.Items ?? playlist.Tracks;
#pragma warning restore CS0618
    }
}
