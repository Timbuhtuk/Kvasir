using System.Net;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kvasir.Media;

internal sealed record SpotifyCollection(string Name, IAsyncEnumerable<SpotifyTrackMetadata> Tracks);
internal sealed record SpotifyTrackMetadata(string Name, string[] Artists);

internal static class SpotifyEmbed
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        MaxResponseContentBufferSize = 8 * 1024 * 1024,
    };
    private static readonly Regex PageData = new(
        """<script\b[^>]*\bid=["']__NEXT_DATA__["'][^>]*>(.*?)</script>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));

    public static async Task<SpotifyCollection?> GetAsync(string kind, string id, Func<Uri, Task<string>>? download, CancellationToken cancellationToken)
    {
        var url = new Uri($"https://open.spotify.com/embed/{kind}/{id}");
        try
        {
            string html = download == null ? await Client.GetStringAsync(url, cancellationToken) : await download(url).WaitAsync(cancellationToken);
            return ReadCollection(html, kind, id);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MediaSourceException("Публичная страница Spotify не ответила вовремя.", "source-timeout");
        }
    }

    private static SpotifyCollection? ReadCollection(string html, string kind, string id)
    {
        Match data = PageData.Match(html);
        if (!data.Success) return null;

        JObject page;
        try { page = JObject.Parse(data.Groups[1].Value); }
        catch (JsonException) { return null; }
        if (page.SelectToken("props.pageProps.state.data.entity") is not JObject entity ||
            (string?)entity["uri"] != $"spotify:{kind}:{id}") return null;

        string? title = (string?)entity["title"] ?? (string?)entity["name"];
        if (string.IsNullOrWhiteSpace(title) || entity["trackList"] is not JArray items) return null;

        var tracks = new List<SpotifyTrackMetadata>();
        foreach (var track in items.OfType<JObject>())
        {
            string? uri = (string?)track["uri"];
            string? name = (string?)track["title"];
            string? artist = (string?)track["subtitle"];
            if (uri?.StartsWith("spotify:track:", StringComparison.Ordinal) == true && !string.IsNullOrWhiteSpace(name))
                tracks.Add(new(name, string.IsNullOrWhiteSpace(artist) ? [] : [artist]));
        }
        return new(title, tracks.ToAsyncEnumerable());
    }
}
