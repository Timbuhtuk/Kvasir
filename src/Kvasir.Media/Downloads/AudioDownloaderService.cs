using System.Diagnostics;
using YoutubeExplode;

namespace Kvasir.Media;

public enum Provider
{
    YoutubeExplode,
    YoutubeDLSharp
}

public class AudioDownloaderService(MediaActivity? activity = null)
{
    private readonly MediaActivity _activity = activity ?? new();
    private readonly YoutubeClient _youtube = new();

    public Task<bool> DownloadAsync(string? link, string? path, CancellationToken cancellationToken = default) =>
        DownloadAsync(link, path, link ?? "", cancellationToken);

    public async Task<bool> DownloadAsync(string? link, string? path, string title, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(link) || string.IsNullOrWhiteSpace(path)) return false;
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        return await _activity.RunAsync(title, MediaStage.Connecting, async operation =>
        {
            try { return await DownloadWithYoutubeDLSharp(link, path, cancellationToken, operation); }
            catch (Exception ex) when (ex is not (OperationCanceledException or MediaSourceException or PCMConvertetionException))
            {
                System.Diagnostics.Trace.WriteLine(ex);
                return await DownloadWithYoutubeExplode(link, path, cancellationToken, operation);
            }
        });
    }

    internal Task<bool> TryDownloadWithProvider(string link, string path, Provider provider, CancellationToken token) =>
        _activity.RunAsync(link, MediaStage.Connecting, operation => provider == Provider.YoutubeExplode
            ? DownloadWithYoutubeExplode(link, path, token, operation)
            : DownloadWithYoutubeDLSharp(link, path, token, operation));

    private async Task<bool> DownloadWithYoutubeExplode(string videoLink, string outputFilePath, CancellationToken cancellationToken, MediaOperation operation)
    {
        operation.Report(MediaStage.Connecting, "YoutubeExplode");
        YoutubeExplode.Videos.Video video = await _youtube.Videos.GetAsync(videoLink, cancellationToken);
        YoutubeExplode.Videos.Streams.StreamManifest streamManifest;
        IReadOnlyList<YoutubeExplode.Videos.Streams.IAudioStreamInfo> audioStreams;
        System.Diagnostics.Trace.WriteLine($"Video found: {video.Title}");
        try
        {
            streamManifest = await _youtube.Videos.Streams.GetManifestAsync(video.Id, cancellationToken);
            audioStreams = streamManifest.GetAudioOnlyStreams().ToList();
            if (!audioStreams.Any())
                throw new AudioDownloadException("No audio stream available");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            throw new AudioDownloadException(ex.Message);
        }

        YoutubeExplode.Videos.Streams.IAudioStreamInfo audioStreamInfo = audioStreams.Where(S => S.Bitrate.BitsPerSecond == audioStreams.Max(s => s.Bitrate.BitsPerSecond)).First();

        Stopwatch watch = new();
        watch.Start();

        string tempMp3Path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mp3");
        try
        {
            operation.Report(MediaStage.Downloading, "YoutubeExplode");
            var progress = new InlineProgress(value => operation.Report(MediaStage.Downloading, "YoutubeExplode",
                (long)(audioStreamInfo.Size.Bytes * value), value * 100));
            await _youtube.Videos.Streams.DownloadAsync(audioStreamInfo, tempMp3Path, progress, cancellationToken);
            operation.Report(MediaStage.Converting, "FFmpeg");
            watch.Stop();
            System.Diagnostics.Trace.WriteLine($"Download completed in {watch.Elapsed}");

            string? convertedPath = await FfmpegInteractor.ConvertMp3ToPcm(tempMp3Path, outputFilePath, cancellationToken, operation);

            if (convertedPath == null || !File.Exists(convertedPath))
                throw new PCMConvertetionException();

            return true;
        }
        finally
        {
            if (File.Exists(tempMp3Path))
                try { File.Delete(tempMp3Path); } catch { }
        }
    }

    private async Task<bool> DownloadWithYoutubeDLSharp(string videoLink, string outputFilePath, CancellationToken cancellationToken, MediaOperation operation)
    {
        string temporary = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".audio");
        try
        {
            operation.Report(MediaStage.Connecting, "yt-dlp");
            await YtDlpProcess.RunAsync(["--ignore-config", "--no-playlist", "--newline", "--progress", "--progress-template", "download:kvasir %(progress.downloaded_bytes)s %(progress.total_bytes)s %(progress.total_bytes_estimate)s", "--format", "bestaudio", "--output", temporary,
                "--socket-timeout", "12", "--retries", "1", "--extractor-retries", "1", "--", videoLink], TimeSpan.FromSeconds(90), cancellationToken, line =>
                {
                    string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length != 4 || fields[0] != "kvasir" || !long.TryParse(fields[1], out long bytes)) return;
                    if (!long.TryParse(fields[2], out long total)) long.TryParse(fields[3], out total);
                    operation.Report(MediaStage.Downloading, "yt-dlp", bytes, total > 0 ? 100d * bytes / total : null);
                });
            operation.Report(MediaStage.Converting, "FFmpeg");
            string? converted = await FfmpegInteractor.ConvertMp3ToPcm(temporary, outputFilePath, cancellationToken, operation);
            if (converted == null || !File.Exists(converted)) throw new PCMConvertetionException();
            return true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
