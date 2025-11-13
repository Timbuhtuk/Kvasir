using Entities.Enums;
using Entities.Models;
using Logging;
using Microsoft.Extensions.Configuration;
using SpotifyAPI.Web;
using System.Diagnostics;
using YoutubeExplode;
using YoutubeExplode.Playlists;
using YoutubeExplode.Search;
using YoutubeExplode.Videos;

namespace Application.Services;

/// <summary>
/// Service that provides methods for finding videos on Youtube using Youtube/Spotify urls
/// </summary>
[LogCategory(LogCategory.VideoFinder)]
public class VideoFinderService
{
    private readonly YoutubeClient _youtubeClient;
    private readonly IConfiguration _configuration;
    private readonly SpotifyInteractor _spotifyInteractor;

    public VideoFinderService(IConfiguration configuration)
    {
        _youtubeClient = new YoutubeClient();
        _configuration = configuration;
        _spotifyInteractor = new SpotifyInteractor(configuration);
    }

    private bool _supportSpotify => _spotifyInteractor.IsEnabled;

    /// <summary>
    /// Find video on Youtube by name or link to Youtube or Spotify, supports single tracks and playlists
    /// </summary>
    /// <param name="query">Youtube or Spotify link to track or playlist</param>
    /// <returns>Dictionary&lt;Urls,Titles&gt; or null if no searching results</returns>
    public async Task<Dictionary<string, string>?> Find(string query)
    {

        if (string.IsNullOrEmpty(query))
        {
            return null;
        }
        else if (query.Contains("https://www.youtube.com/watch"))
        {
            try
            {
                VideoId videoId = VideoId.Parse(query);

                Stopwatch watch = new();
                watch.Start();
                YoutubeExplode.Videos.Video video = await _youtubeClient.Videos.GetAsync(videoId);
                watch.Stop();
                await Logger.AddLog($"get video info took {watch.Elapsed}");

                Dictionary<string, string> result = new();
                result.Add(video.Url, video.Title);

                return result;
            }
            catch (Exception ex)
            {
                await Logger.AddLog(ex.Message, LogLevel.ERROR, exception: ex);
                return null;
            }
        }
        else if (query.Contains("https://www.youtube.com/playlist"))
        {
            try
            {
                PlaylistId playlist_id = PlaylistId.Parse(query);

                Stopwatch watch = new();
                watch.Start();
                IAsyncEnumerable<YoutubeExplode.Playlists.PlaylistVideo> videos = _youtubeClient.Playlists.GetVideosAsync(playlist_id);
                watch.Stop();
                await Logger.AddLog($"get video info took {watch.Elapsed}");

                Dictionary<string, string> result = new();
                foreach (YoutubeExplode.Playlists.PlaylistVideo video in videos.ToListAsync().Result)
                {
                    result.Add(video.Url, video.Title);
                }

                return result;
            }
            catch (Exception ex)
            {
                await Logger.AddLog(ex.Message, LogLevel.ERROR, exception: ex);
                return null;
            }
        }
        else if (query.Contains("https://open.spotify.com/track/"))
        {
            if (!_supportSpotify) return null;
            try
            {
                SpotifyClient spotify = await _spotifyInteractor.GetClientAsync();

                string trackId = query.Split('/').Last().Split("?")[0];
                FullTrack track = await spotify.Tracks.Get(trackId);

                Console.WriteLine($"Track: {track.Name}, Artist: {track.Artists.First().Name}");
                return await Find($"{track.Name} - {track.Artists.First().Name}");
            }
            catch (Exception ex)
            {
                await Logger.AddLog(ex.Message, LogLevel.ERROR, exception: ex);
                return null;
            }
        }
        else if (query.Contains("https://open.spotify.com/playlist/"))
        {
            if (!_supportSpotify) return null;

            SpotifyClient spotify = await _spotifyInteractor.GetClientAsync();

            string playlistId = query.Split('/').Last().Split("?")[0];
            FullPlaylist playlist = await spotify.Playlists.Get(playlistId);

            Console.WriteLine($"Playlist: {playlist.Name}");

            Dictionary<string, string> result = new();

            if (playlist.Tracks != null && playlist.Tracks.Items != null)
            {
                foreach (PlaylistTrack<IPlayableItem> trackItem in playlist.Tracks.Items)
                {
                    FullTrack? track = trackItem.Track as FullTrack;
                    if (track != null)
                    {
                        Dictionary<string, string>? find_result = await Find($"{track.Name} - {track.Artists.First().Name}");
                        if (find_result != null)
                        {
                            KeyValuePair<string, string> KVP = find_result.First();
                            result.Add(KVP.Key, KVP.Value);
                        }
                    }
                }
            }
            return result;
        }
        else
        {
            try
            {
                Stopwatch watch = new();
                watch.Start();

                IAsyncEnumerable<ISearchResult> searchResults = _youtubeClient.Search.GetResultsAsync(query);
                ISearchResult video_raw = await searchResults.FirstAsync();

                VideoId video_id = VideoId.Parse(video_raw.Url);
                YoutubeExplode.Videos.Video video = await _youtubeClient.Videos.GetAsync(video_id);
                watch.Stop();
                await Logger.AddLog($"get video info took {watch.Elapsed}");

                Dictionary<string, string> result = new();
                result.Add(video.Url, video.Title);

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// Find video on Youtube by name or link to Youtube or Spotify, supports single tracks and playlists
    /// </summary>
    /// <param name="query">Youtube or Spotify link to track or playlist</param>
    /// <param name="return_song">No matter value of param, but on include it triggers different return on function 'Find'</param>
    /// <returns>List&lt;Song&gt; or null if no searching results</returns>
    public async Task<List<Song>?> Find(string query, bool return_song)
    {
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }
        else if (query.Contains("https://www.youtube.com/watch"))
        {
            try
            {
                VideoId videoId = VideoId.Parse(query);

                Stopwatch watch = new();
                watch.Start();
                YoutubeExplode.Videos.Video video = await _youtubeClient.Videos.GetAsync(videoId);
                watch.Stop();
                await Logger.AddLog($"get video info took {watch.Elapsed}");

                List<Song> result = new();
                result.Add(new Song()
                {
                    Link = video.Url,
                    Name = video.Title,
                    AuthorName = video.Author.ChannelTitle,
                    Duration = video.Duration != null ? video.Duration.Value.TotalSeconds / 60 : 0
                });

                return result;
            }
            catch (Exception ex)
            {
                await Logger.AddLog(ex.Message, LogLevel.ERROR, exception: ex);
                return null;
            }
        }
        else if (query.Contains("https://www.youtube.com/playlist"))
        {
            try
            {
                PlaylistId playlist_id = PlaylistId.Parse(query);

                Stopwatch watch = new();
                watch.Start();
                IAsyncEnumerable<YoutubeExplode.Playlists.PlaylistVideo> videos = _youtubeClient.Playlists.GetVideosAsync(playlist_id);
                watch.Stop();
                await Logger.AddLog($"get video info took {watch.Elapsed}");

                List<Song> result = new();
                foreach (YoutubeExplode.Playlists.PlaylistVideo video in videos.ToListAsync().Result)
                {
                    result.Add(new Song()
                    {
                        Link = video.Url,
                        Name = video.Title,
                        AuthorName = video.Author.ChannelTitle,
                        Duration = video.Duration != null ? video.Duration.Value.TotalSeconds / 60 : 0
                    });
                }

                return result;
            }
            catch (Exception ex)
            {
                await Logger.AddLog(ex.Message, LogLevel.ERROR, exception: ex);
                return null;
            }
        }
        else if (query.Contains("https://open.spotify.com/track/"))
        {
            if (!_supportSpotify) return null;
            try
            {
                SpotifyClient spotify = await _spotifyInteractor.GetClientAsync();

                string trackId = query.Split('/').Last().Split("?")[0];
                FullTrack track = await spotify.Tracks.Get(trackId);

                Console.WriteLine($"Track: {track.Name}, Artist: {track.Artists.First().Name}");
                return await Find($"{track.Name} - {track.Artists.First().Name}", true);
            }
            catch (Exception ex)
            {
                await Logger.AddLog(ex.Message, LogLevel.ERROR, exception: ex);
                return null;
            }
        }
        else if (query.Contains("https://open.spotify.com/playlist/"))
        {
            if (!_supportSpotify) return null;

            SpotifyClient spotify = await _spotifyInteractor.GetClientAsync();

            string playlistId = query.Split('/').Last().Split("?")[0];
            FullPlaylist playlist = await spotify.Playlists.Get(playlistId);

            Console.WriteLine($"Playlist: {playlist.Name}");

            List<Song> result = new();

            foreach (PlaylistTrack<IPlayableItem> trackItem in playlist.Tracks.Items)
            {
                FullTrack? track = trackItem.Track as FullTrack;
                if (track != null)
                {
                    List<Song>? songs = await Find($"{track.Name} - {track.Artists.First().Name}", true);
                    if (songs != null && songs.Any())
                    {
                        result.Add(songs.First());
                    }
                }
            }
            return result;
        }
        else
        {
            try
            {
                Stopwatch watch = new();
                watch.Start();

                IAsyncEnumerable<ISearchResult> searchResults = _youtubeClient.Search.GetResultsAsync(query);
                ISearchResult video_raw = await searchResults.FirstAsync();

                VideoId video_id = VideoId.Parse(video_raw.Url);
                YoutubeExplode.Videos.Video video = await _youtubeClient.Videos.GetAsync(video_id);
                watch.Stop();
                await Logger.AddLog($"get video info took {watch.Elapsed}");

                List<Song> result = new();
                result.Add(new Song()
                {
                    Link = video.Url,
                    Name = video.Title,
                    AuthorName = video.Author.ChannelTitle,
                    Duration = video.Duration != null ? video.Duration.Value.TotalSeconds / 60 : 0
                });

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// Find playlist on Youtube by link to Youtube or Spotify
    /// </summary>
    /// <param name="query">Youtube or Spotify link to track or playlist</param>
    /// <returns>Dictionary&lt;Urls,Titles&gt; or null if no searching results</returns>
    public async Task<Dictionary<string, string>?> FindPlaylistByLink(string query)
    {
        await Logger.AddLog($"Find called for {query}");
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }
        else if (query.Contains("https://www.youtube.com/playlist"))
        {
            try
            {
                PlaylistId playlist_id = PlaylistId.Parse(query);

                Stopwatch watch = new();
                watch.Start();
                IAsyncEnumerable<YoutubeExplode.Playlists.PlaylistVideo> videos = _youtubeClient.Playlists.GetVideosAsync(playlist_id);
                watch.Stop();
                await Logger.AddLog($"get playlist info took {watch.Elapsed}");

                Dictionary<string, string> result = new();
                foreach (YoutubeExplode.Playlists.PlaylistVideo video in videos.ToListAsync().Result)
                {
                    result.Add(video.Url, video.Title);
                }

                return result;
            }
            catch (Exception ex)
            {
                await Logger.AddLog(ex.Message, LogLevel.ERROR, exception: ex);
                return null;
            }
        }
        else if (query.Contains("https://open.spotify.com/playlist/"))
        {
            if (!_supportSpotify) return null;

            SpotifyClient spotify = await _spotifyInteractor.GetClientAsync();

            string playlistId = query.Split('/').Last().Split("?")[0];
            FullPlaylist playlist = await spotify.Playlists.Get(playlistId);

            Console.WriteLine($"Playlist: {playlist.Name}");

            Dictionary<string, string> result = new();

            foreach (PlaylistTrack<IPlayableItem> trackItem in playlist.Tracks.Items)
            {
                FullTrack? track = trackItem.Track as FullTrack;
                if (track != null)
                {
                    Dictionary<string, string>? find_result = await Find($"{track.Name} - {track.Artists.First().Name}");
                    if (find_result != null)
                    {
                        KeyValuePair<string, string> KVP = find_result.First();
                        result.Add(KVP.Key, KVP.Value);
                    }
                }
            }
            return result;
        }
        return null;
    }

    /// <summary>
    /// Find playlist on Youtube by link to Youtube or Spotify
    /// </summary>
    /// <param name="query">Youtube or Spotify link to track or playlist</param>
    /// <param name="return_song">No matter value of param, but on include it triggers different return on function 'Find'</param>
    /// <returns>Dictionary&lt;Urls,Titles&gt; or null if no searching results</returns>
    public async Task<List<Song>?> FindPlaylistByLink(string query, bool return_song)
    {
        await Logger.AddLog($"Find called for {query}");
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }
        else if (query.Contains("https://www.youtube.com/playlist"))
        {
            try
            {
                PlaylistId playlist_id = PlaylistId.Parse(query);

                Stopwatch watch = new();
                watch.Start();
                IAsyncEnumerable<YoutubeExplode.Playlists.PlaylistVideo> videos = _youtubeClient.Playlists.GetVideosAsync(playlist_id);
                watch.Stop();
                await Logger.AddLog($"get playlist info took {watch.Elapsed}");

                List<Song> result = new();
                foreach (YoutubeExplode.Playlists.PlaylistVideo video in videos.ToListAsync().Result)
                {
                    result.Add(
                        new Song()
                        {
                            Name = video.Title,
                            AuthorName = video.Author.Title,
                            Link = video.Url,
                            Duration = video.Duration != null ? video.Duration.Value.TotalSeconds / 60 : 0
                        });
                }

                return result;
            }
            catch (Exception ex)
            {
                await Logger.AddLog(ex.Message, LogLevel.ERROR, exception: ex);
                return null;
            }
        }
        else if (query.Contains("https://open.spotify.com/playlist/"))
        {
            if (!_supportSpotify) return null;

            SpotifyClient spotify = await _spotifyInteractor.GetClientAsync();

            string playlistId = query.Split('/').Last().Split("?")[0];
            FullPlaylist playlist = await spotify.Playlists.Get(playlistId);

            Console.WriteLine($"Playlist: {playlist.Name}");

            List<Song> result = new();

            foreach (PlaylistTrack<IPlayableItem> trackItem in playlist.Tracks.Items)
            {
                FullTrack? track = trackItem.Track as FullTrack;
                if (track != null)
                {
                    List<Song>? songs = await Find($"{track.Name} - {track.Artists.First().Name}", true);
                    if (songs != null && songs.Any())
                    {
                        result.Add(songs.First());
                    }
                }
            }
            return result;
        }
        return null;
    }

    /// <summary>
    /// Get playlist name by link
    /// </summary>
    /// <param name="URL">Youtube or Spotify link to playlist</param>
    /// <returns>playlist name</returns>
    public async Task<string?> GetPlaylistName(string URL)
    {
        if (string.IsNullOrEmpty(URL))
        {
            return null;
        }
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
                await Logger.AddLog(ex.Message, LogLevel.ERROR, exception: ex);
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
            foreach (PlaylistTrack<IPlayableItem> trackItem in playlist.Tracks.Items)
            {
                IPlayableItem? track = trackItem.Track;
                if (track != null)
                {
                    Console.WriteLine(track.ToString());
                }
            }
            return playlist.Name;
        }
        return null;
    }
}

