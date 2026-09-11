using Entities.Enums;
using Entities.Models;
using Application.Interfaces;
using Logging;
using Microsoft.Extensions.Configuration;
using SpotifyAPI.Web;
using System.Diagnostics;
using YoutubeExplode;
using YoutubeExplode.Playlists;
using YoutubeExplode.Search;
using YoutubeExplode.Videos;

namespace Connectors.Media;

/// <summary>
/// Service that provides methods for finding videos on Youtube using Youtube/Spotify urls
/// </summary>
[LogCategory(LogCategory.VideoFinder)]
public class VideoFinderService(IConfiguration configuration) : IVideoFinderService
{
    private readonly YoutubeClient _youtubeClient = new();
    private readonly SpotifyInteractor _spotifyInteractor = new(configuration);
    private bool _supportSpotify => _spotifyInteractor.IsEnabled;


    /// <summary>
    /// Find video on Youtube by name or link to Youtube or Spotify, supports single tracks and playlists
    /// </summary>
    /// <param name="query">Youtube or Spotify link to track or playlist</param>
    /// <param name="return_song">No matter value of param, but on include it triggers different return on function 'Find'</param>
    /// <returns>List&lt;Song&gt; or null if no searching results</returns>
    public async Task<List<FinderSongDTO>?> Find(string query, bool return_song = false)
    {
        Stopwatch watch = new();
        if (string.IsNullOrEmpty(query))
            return null;
        else if (query.Contains("https://www.youtube.com/watch"))
        {
            try
            {
                VideoId videoId = VideoId.Parse(query);

                watch.Start();
                Video video = await _youtubeClient.Videos.GetAsync(videoId);
                watch.Stop();
                await Logger.AddLog($"get video info took {watch.Elapsed}");

                List<FinderSongDTO> result = new()
                {
                    new FinderSongDTO(){
                        Link = video.Url,
                        Name = video.Title,
                        AuthorName = video.Author.ChannelTitle,
                        Duration = video.Duration != null ? video.Duration.Value.TotalSeconds : 0
                    }
                };


                return result;
            }
            catch (Exception ex)
            {
                await Logger.AddLog(ex.Message, Microsoft.Extensions.Logging.LogLevel.Error, exception: ex);
                return null;
            }
        }
        else if (query.Contains("https://www.youtube.com/playlist"))
        {
            try
            {
                PlaylistId playlist_id = PlaylistId.Parse(query);

                watch.Start();
                IAsyncEnumerable<PlaylistVideo> videos = _youtubeClient.Playlists.GetVideosAsync(playlist_id);
                watch.Stop();
                await Logger.AddLog($"get video info took {watch.Elapsed}");

                List<FinderSongDTO> result = new();
                await foreach (PlaylistVideo video in videos)
                {
                    result.Add(new FinderSongDTO()
                    {
                        Link = video.Url,
                        Name = video.Title,
                        AuthorName = video.Author.ChannelTitle,
                        Duration = video.Duration != null ? video.Duration.Value.TotalSeconds : 0
                    });
                }

                return result;
            }
            catch (Exception ex)
            {
                await Logger.AddLog(ex.Message, Microsoft.Extensions.Logging.LogLevel.Error, exception: ex);
                return null;
            }
        }
        else if (query.Contains("https://open.spotify.com/"))
        {
            watch.Start();
            List<FinderSongDTO>? result = await FindOnSpotifyAsync(query, return_song);
            watch.Stop();
            await Logger.AddLog($"get video info took {watch.Elapsed}");
            return result;
        }
        else
        {
            try
            {
                watch.Start();

                IAsyncEnumerable<ISearchResult> searchResults = _youtubeClient.Search.GetResultsAsync(query);
                ISearchResult? video_raw = null;
                await foreach (var searchResult in searchResults)
                {
                    video_raw = searchResult;
                    break;
                }
                if (video_raw == null)
                    return null;

                VideoId video_id = VideoId.Parse(video_raw.Url);
                YoutubeExplode.Videos.Video video = await _youtubeClient.Videos.GetAsync(video_id);
                watch.Stop();
                await Logger.AddLog($"get video info took {watch.Elapsed}");

                List<FinderSongDTO> result = new()
                {
                    new FinderSongDTO()
                    {
                        Link = video.Url,
                        Name = video.Title,
                        AuthorName = video.Author.ChannelTitle,
                        Duration = video.Duration != null ? video.Duration.Value.TotalSeconds : 0
                    }
                };

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                return null;
            }
        }
    }

    public async Task<List<FinderSongDTO>?> FindOnSpotifyAsync(string query, bool return_song = false)
    {
        if (!_supportSpotify) return null;

        SpotifyClient spotify = await _spotifyInteractor.GetClientAsync();
        string spotifyEntityId = query.Split('/').Last().Split("?")[0];

        if (query.Contains("track/"))
        {
            try
            {
                FullTrack track = await spotify.Tracks.Get(spotifyEntityId);

                await Logger.AddLog($"Track: {track.Name}, Artist: {track.Artists.First().Name}");
                FinderSongDTO? result = (await Find($"{track.Name} - {track.Artists.First().Name}", true))?.FirstOrDefault();
                if (result != null)
                {
                    result.AuthorName = string.Join("|", track.Artists.Select(a => a.Name));
                    result.Name = track.Name;
                    return new() { result };
                }
            }
            catch (Exception ex)
            {
                await Logger.AddLog(ex.Message, Microsoft.Extensions.Logging.LogLevel.Error, exception: ex);
                return null;
            }
        }
        else if (query.Contains("playlist/"))
        {
            FullPlaylist playlist = await spotify.Playlists.Get(spotifyEntityId);

            if (playlist.Items?.Items == null)
            {
                await Logger.AddLog($"Playlist: {playlist.Name} is empty");
                return null;
            }
            await Logger.AddLog($"Playlist: {playlist.Name}");

            List<FinderSongDTO> result = new();

            foreach (PlaylistTrack<IPlayableItem> trackItem in playlist.Items.Items)
            {
                if (trackItem.Track is FullTrack track)
                {
                    FinderSongDTO? song = (await Find($"{track.Name} - {track.Artists.First().Name}", true))?.FirstOrDefault();
                    if (song != null)
                    {
                        song.AuthorName = string.Join("|", track.Artists.Select(a => a.Name));
                        song.Name = track.Name;
                        result.Add(song);
                    }
                }
            }
            return result;
        }
        return null;
    }

    public async Task<string?> GetPlaylistName(string URL)
    {
        if (string.IsNullOrEmpty(URL))
            return null;
        else if (URL.Contains("https://www.youtube.com/playlist"))
        {
            try
            {
                PlaylistId playlist_id = PlaylistId.Parse(URL);

                Stopwatch watch = new();
                watch.Start();
                YoutubeExplode.Playlists.Playlist playlist = await _youtubeClient.Playlists.GetAsync(playlist_id);
                watch.Stop();
                await Logger.AddLog($"get playlist info took {watch.Elapsed}");

                return playlist.Title;
            }
            catch (Exception ex)
            {
                await Logger.AddLog(ex.Message, Microsoft.Extensions.Logging.LogLevel.Error, exception: ex);
                return null;
            }
        }
        else if (URL.Contains("https://open.spotify.com/playlist/"))
        {
            if (!_supportSpotify) return null;

            SpotifyClient spotify = await _spotifyInteractor.GetClientAsync();

            string playlistId = URL.Split('/').Last().Split("?")[0];
            FullPlaylist playlist = await spotify.Playlists.Get(playlistId);

            Console.WriteLine($"Playlist: {playlist.Name}");
            foreach (PlaylistTrack<IPlayableItem> trackItem in playlist.Items?.Items ?? [])
            {
                IPlayableItem? track = trackItem.Track;
                if (track != null)
                    Console.WriteLine(track.ToString());
            }
            return playlist.Name;
        }
        return null;
    }
}
