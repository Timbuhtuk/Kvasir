using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Kvasir.Configuration;
using Kvasir.Desktop.Localization;

namespace Kvasir.Desktop.Services;

internal static class GitHubUpdater
{
    private const string Releases = "https://github.com/Timbuhtuk/Kvasir/releases/";
    private const string LatestReleaseApi = "https://api.github.com/repos/Timbuhtuk/Kvasir/releases/latest";
    private const long MaximumBytes = 1024L * 1024 * 1024;
    private static readonly HttpClient Client = CreateClient();

    internal sealed record Package(Version Version, Uri Url, string Sha256, long Size);
    internal sealed record Release(Version Version, Uri Page, Package? Installer);

    internal static Version CurrentVersion { get; } = Version.Parse(
        typeof(GitHubUpdater).Assembly.GetName().Version!.ToString(3));
    internal static bool IsReleaseBuild => CurrentVersion != new Version(0, 0, 0);

    internal static async Task<Release?> CheckAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
        return ReadRelease(json.RootElement, CurrentVersion);
    }

    internal static Release? ReadRelease(JsonElement release, Version installed)
    {
        string? tag = release.GetProperty("tag_name").GetString();
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean() ||
            tag is null || !tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version) ||
            version.Build < 0 || version.Revision >= 0 || tag != $"v{version}" || version <= installed)
            return null;

        string assetName = $"Kvasir-{tag}-win-x64.exe";
        var page = new Uri(Releases + "tag/" + tag);
        var assetUrl = new Uri(Releases + "download/" + tag + "/" + assetName);
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != assetName ||
                asset.GetProperty("browser_download_url").GetString() != assetUrl.AbsoluteUri ||
                !asset.TryGetProperty("digest", out var digest) || digest.GetString() is not { } sha256 ||
                !sha256.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
                sha256.Length != 71 || !sha256[7..].All(Uri.IsHexDigit) ||
                !asset.GetProperty("size").TryGetInt64(out long size) || size is <= 0 or > MaximumBytes)
                continue;
            return new Release(version, page, new Package(version, assetUrl, sha256[7..], size));
        }
        return new Release(version, page, null);
    }

    internal static async Task<string> DownloadAsync(Package package, IProgress<int> progress, CancellationToken cancellationToken)
    {
        string folder = Path.Combine(ApplicationPaths.DataDirectory, "Updates");
        DesktopMaintenance.CheckPath(folder);
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, $".Kvasir-download-{Guid.NewGuid():N}.exe");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var token = timeout.Token;
        try
        {
            using var response = await Client.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != package.Size)
                throw new InvalidDataException(UiText.Get("UpdateChecksumFailed"));

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, useAsync: true);
            byte[] buffer = new byte[131072];
            long total = 0;
            int percentage = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > package.Size) throw new InvalidDataException(UiText.Get("UpdateChecksumFailed"));
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                int next = (int)(total * 100 / package.Size);
                if (next > percentage) progress.Report(percentage = next);
            }
            if (total != package.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(UiText.Get("UpdateChecksumFailed"));
            return file;
        }
        catch
        {
            DeleteDownload(file);
            throw;
        }
    }

    internal static void DeleteDownload(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Kvasir-Updater/1.0");
        return client;
    }
}
