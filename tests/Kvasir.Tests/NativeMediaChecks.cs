using Kvasir.Media;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Diagnostics;
using Application.Models;
using Application.Services;
using Connectors.Media;
using Entities.Models;
using Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kvasir.Tests;

internal static partial class Program
{
    private static async Task TestBufferedDecodeAsync(string root)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        string path = Path.Combine(root, "сжатый трек.audio");
        var info = new ProcessStartInfo(MediaTools.RuntimeFile("ffmpeg.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        string[] args = ["-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=4",
            "-c:a", "libmp3lame", "-f", "mp3", path];
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using (Process process = Process.Start(info)!)
        {
            await process.WaitForExitAsync(timeout.Token);
            Check(process.ExitCode == 0, "FFmpeg produces a compressed fixture");
        }

        await using (var pcm = await BufferedPcmStream.OpenAsync(File.OpenRead(path), timeout.Token))
        {
            byte[] frame = new byte[PcmEffectMixer.FrameBytes];
            await pcm.ReadExactlyAsync(frame, timeout.Token);
            Check(pcm.Position == frame.Length && frame.Any(b => b != 0), "Decode-on-playback provides a non-silent Discord PCM frame");
            await pcm.SeekAsync(2 * PcmEffectMixer.BytesPerSecond, timeout.Token);
            using MemoryStream tail = new();
            await pcm.CopyToAsync(tail, timeout.Token);
            Check(Math.Abs(tail.Length - 2 * PcmEffectMixer.BytesPerSecond) < frame.Length &&
                pcm.Position == 2 * PcmEffectMixer.BytesPerSecond + tail.Length, "Buffered seek discards old audio and decodes from the requested time");
            await pcm.SeekAsync(0, timeout.Token);
        }
        File.Delete(path);
        Check(!File.Exists(path), "Closing a buffered decoder releases the source even with a full buffer");
    }

    private static async Task TestYtDlpProcessAsync(string root)
    {
        byte[] wave = await File.ReadAllBytesAsync(Path.Combine(root, "эффект mono 22k.wav"));
        using var server = new TcpListener(IPAddress.Loopback, 0);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        server.Start();
        int port = ((IPEndPoint)server.LocalEndpoint).Port;
        string address = $"http://127.0.0.1:{port}";
        Task serving = ServeAsync();
        try
        {
            string pcm = Path.Combine(root, "yt-dlp audio.pcm");
            MediaActivity activity = new();
            var downloader = new SongAudioDownloader(activity);
            var transfer = downloader.TryDownloadWithProvider(address + "/audio.wav", pcm, Provider.YoutubeDLSharp, timeout.Token);
            bool measured = false;
            while (!transfer.IsCompleted)
            {
                var status = activity.Snapshot().FirstOrDefault();
                measured |= status is { Stage: MediaStage.Downloading, Bytes: > 0, Percent: > 0 };
                await Task.Delay(10, timeout.Token);
            }
            Check(await transfer,
                "Real yt-dlp downloads a local audio fixture and passes it to FFmpeg");
            Check(measured && activity.Snapshot().Any(s => s.Stage == MediaStage.Ready), "yt-dlp publishes measured transfer progress before completion");
            byte[] decoded = await File.ReadAllBytesAsync(pcm, timeout.Token);
            Check(decoded.Length == PcmEffectMixer.BytesPerSecond && decoded.Any(b => b != 0), "Downloaded WAV converts to one second of non-silent Discord PCM");
            byte[]? direct = await FfmpegInteractor.ConvertMp3ToPcmBytes(Path.Combine(root, "эффект mono 22k.wav"));
            Check(direct != null && direct.Length == PcmEffectMixer.BytesPerSecond && direct.Any(b => b != 0), "FFmpeg can stream stereo PCM to stdout");

            string compressed = Path.Combine(root, "yt-dlp audio.audio");
            Check(await downloader.DownloadCompressedAsync(address + "/audio.wav", compressed, "Unconverted fixture", timeout.Token) &&
                (await File.ReadAllBytesAsync(compressed, timeout.Token)).SequenceEqual(wave),
                "Decode-on-playback downloads the original bytes without creating PCM");
            await TestCompressedCacheRepairAsync(root, downloader, address + "/audio.wav", timeout.Token);

            try
            {
                await YtDlpProcess.RunAsync(["--ignore-config", "--no-progress", "--retries", "0", "--", address + "/missing"], TimeSpan.FromSeconds(10), timeout.Token);
                Check(false, "yt-dlp returned success for a missing resource");
            }
            catch (MediaSourceException ex)
            {
                Check(ex.Code == "source" && ex.Message.Contains("404", StringComparison.Ordinal), "yt-dlp nonzero exit produces a diagnosable source error");
            }

            try
            {
                await YtDlpProcess.RunAsync(["--ignore-config", "--", address + "/stall"], TimeSpan.FromSeconds(2), timeout.Token);
                Check(false, "yt-dlp returned success without a response");
            }
            catch (MediaSourceException ex)
            {
                Check(ex.Code == "source-timeout", "yt-dlp timeout terminates the subprocess with a distinct error code");
            }
        }
        finally
        {
            await timeout.CancelAsync();
            try { await serving; }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        }

        async Task ServeAsync()
        {
            while (!timeout.IsCancellationRequested)
            {
                using TcpClient client = await server.AcceptTcpClientAsync(timeout.Token);
                await using NetworkStream stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                string? request = await reader.ReadLineAsync(timeout.Token);
                string? header;
                do { header = await reader.ReadLineAsync(timeout.Token); }
                while (!string.IsNullOrEmpty(header));

                if (request?.Contains(" /stall ", StringComparison.Ordinal) == true)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token);
                    continue;
                }
                bool found = request?.Contains(" /audio.wav ", StringComparison.Ordinal) == true;
                string status = found ? "200 OK" : "404 Not Found";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status}\r\nContent-Type: audio/wav\r\nContent-Length: {(found ? wave.Length : 0)}\r\nConnection: close\r\n\r\n"), timeout.Token);
                if (found && request!.StartsWith("GET ", StringComparison.Ordinal))
                {
                    try
                    {
                        for (int offset = 0; offset < wave.Length; offset += 2048)
                        {
                            await stream.WriteAsync(wave.AsMemory(offset, Math.Min(2048, wave.Length - offset)), timeout.Token);
                            await Task.Delay(15, timeout.Token);
                        }
                    }
                    catch (IOException) { /* yt-dlp may close the metadata probe before reading the body. */ }
                }
            }
        }
    }

    private static async Task TestCompressedCacheRepairAsync(string root, SongAudioDownloader downloader, string url, CancellationToken token)
    {
        string folder = Path.Combine(root, "compressed-cache");
        Directory.CreateDirectory(folder);
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["music_client:music_folder"] = folder,
            ["music_client:decode_on_playback"] = "true"
        }).Build();
        var services = new ServiceCollection().AddDbContext<DiscordMusicDBContext>(options =>
            options.UseSqlite("Data Source=" + Path.Combine(folder, "cache.db")));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DiscordMusicDBContext>();
        await db.Database.MigrateAsync(token);
        string path = Path.Combine(folder, "1.audio");
        var song = new Song { Id = 1, Name = "Fixture", Link = url, Duration = 1, IsDownloaded = true, FilePath = path };
        db.Songs.Add(song);
        await db.SaveChangesAsync(token);
        await File.WriteAllTextAsync(path, "Nonempty corrupt audio fixture", token);

        var cache = new MediaCacheService(provider.GetRequiredService<IServiceScopeFactory>(), downloader, new AudioNormalizer(), settings);
        Check((await cache.InspectAsync()).InvalidFiles == 1, "Cache inspection detects a nonempty audio file FFmpeg cannot decode");
        var repaired = await cache.MaintainAsync(true, token);
        Check(repaired is { Repaired: 1, FailedRepairs: 0, InvalidFiles: 0 }, "Cache repair replaces a corrupt download");

        await using var source = await cache.OpenAsync(song, token);
        await using var pcm = await cache.OpenPlaybackStreamAsync(source, token);
        byte[] frame = new byte[PcmEffectMixer.FrameBytes];
        await pcm.ReadExactlyAsync(frame, token);
        Check(frame.Any(b => b != 0), "The repaired download produces audio in CPU mode");
    }
}
