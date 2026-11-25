using Kvasir.Media;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Application.Models;
using Application.Services;
using Connectors.Media;

namespace Kvasir.Tests;

internal static partial class Program
{
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
            var downloader = new AudioDownloaderService(activity);
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
}
