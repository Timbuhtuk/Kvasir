using YoutubeDLSharp.Metadata;
using Newtonsoft.Json;

namespace Kvasir.Media;

public static class YtDlpMetadata
{
    public static string? GetPlaylistUrl(string input)
    {
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || !IsYoutube(uri)) return null;
        string? id = uri.Query.TrimStart('?').Split('&')
            .FirstOrDefault(pair => pair.StartsWith("list=", StringComparison.OrdinalIgnoreCase))?[5..];
        if (string.IsNullOrEmpty(id) || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            return null;
        return $"https://www.youtube.com/playlist?list={id}";
    }

    private static bool IsYoutube(Uri address) => address.Scheme is "https" or "http" &&
        (address.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) ||
         address.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
         address.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase));

    public static async Task<VideoData> FetchAsync(string query, bool titleOnly = false)
    {
        bool url = Uri.TryCreate(query, UriKind.Absolute, out var address) && address.Scheme is "https" or "http";
        bool youtube = url && IsYoutube(address!);
        if (url && !youtube) throw new MediaSourceException("Этот источник не поддерживается. Используйте ссылку YouTube или название трека.");
        bool playlist = youtube && address!.AbsolutePath.TrimEnd('/').Equals("/playlist", StringComparison.OrdinalIgnoreCase);
        System.Diagnostics.Trace.WriteLine("yt-dlp: metadata request started");
        string json = await YtDlpProcess.RunAsync(["--ignore-config", "--dump-single-json", "--skip-download", "--flat-playlist",
            playlist ? "--yes-playlist" : "--no-playlist", "--playlist-items", titleOnly ? "1" : "1:1001", "--socket-timeout", "12", "--retries", "1", "--extractor-retries", "1", "--",
            youtube ? query : "ytsearch1:" + query], TimeSpan.FromSeconds(50));
        System.Diagnostics.Trace.WriteLine("yt-dlp: metadata received");
        return Parse(json);
    }

    internal static VideoData Parse(string json) => JsonConvert.DeserializeObject<VideoData>(json)
        ?? throw new MediaSourceException("Источник не вернул информацию о треке.");
    public static List<FinderSongDTO> MapSongs(VideoData data)
    {
        List<FinderSongDTO> songs = [];
        void Add(VideoData item)
        {
            if (songs.Count >= 1001) return;
            if (item.Entries != null) { foreach (var child in item.Entries) if (child != null) Add(child); return; }
            if (string.IsNullOrWhiteSpace(item.ID) || item.ID.Length != 11 || item.ID.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) return;
            if (item.Title is "[Deleted video]" or "[Private video]") return;
            songs.Add(new FinderSongDTO
            {
                Name = item.Title ?? "Без названия", AuthorName = item.Uploader ?? item.Channel ?? "",
                Duration = item.Duration ?? 0, Link = "https://www.youtube.com/watch?v=" + item.ID,
            });
        }
        Add(data);
        return songs;
    }
}
