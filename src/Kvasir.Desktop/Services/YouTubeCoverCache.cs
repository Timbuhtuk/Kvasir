using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;
using Kvasir.Configuration;

namespace Kvasir.Desktop.Services;

internal sealed class YouTubeCoverCache(string directory)
{
    public static YouTubeCoverCache Shared { get; } = new(Path.Combine(ApplicationPaths.DataDirectory, "covers"));

    private const int MaxImageBytes = 512 * 1024;
    private readonly SemaphoreSlim _downloads = new(4, 4);
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(8),
        MaxResponseContentBufferSize = MaxImageBytes,
    };

    public static string? GetVideoId(string? link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;

        string? id = null;
        string[] segments = uri.AbsolutePath.Trim('/').Split('/');
        if (uri.Host == "youtu.be" && segments.Length == 1)
            id = segments[0];
        else if (uri.Host == "youtube.com" || uri.Host.EndsWith(".youtube.com", StringComparison.Ordinal))
        {
            if (segments is ["watch"])
                id = uri.Query.TrimStart('?').Split('&').FirstOrDefault(part => part.StartsWith("v=", StringComparison.Ordinal))?[2..];
            else if (segments is ["shorts" or "live" or "embed" or "v", var videoId])
                id = videoId;
        }

        return id is { Length: 11 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? id : null;
    }

    public async Task<BitmapSource?> GetAsync(string videoId, CancellationToken cancellationToken)
    {
        string path = Path.Combine(directory, videoId + ".jpg");
        if (ReadCached(path) is { } cached) return cached;

        await _downloads.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ReadCached(path) is { } ready) return ready;
            using var response = await Client.GetAsync($"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "image/jpeg") return null;
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            using var stream = new MemoryStream(bytes);
            var image = Decode(stream);
            if (image != null && !cancellationToken.IsCancellationRequested) Save(path, bytes);
            return image;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return null;
        }
        finally { _downloads.Release(); }
    }

    private static BitmapSource? ReadCached(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return stream.Length <= MaxImageBytes ? Decode(stream) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static BitmapSource? Decode(Stream stream)
    {
        try
        {
            BitmapImage image = new();
            image.BeginInit();
            image.StreamSource = stream;
            image.DecodePixelWidth = 216;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or FormatException or NotSupportedException)
        {
            return null;
        }
    }

    private void Save(string path, byte[] bytes)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { }
        }
    }
}
