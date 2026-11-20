using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Connectors.Media;
using NetCord.Gateway.Voice;
using YoutubeDLSharp;
using YoutubeDLSharp.Options;

namespace Kvasir.Integration.Tests;

internal static partial class Program
{
    private static async Task CheckNativeAudioAsync(string root)
    {
        byte[] wave = MakeWave();
        string wavePath = Path.Combine(root, "fixture mono 22k.wav");
        await File.WriteAllBytesAsync(wavePath, wave);
        byte[]? pcm = await FfmpegInteractor.ConvertMp3ToPcmBytes(wavePath).WaitAsync(TimeSpan.FromSeconds(15));
        Check(pcm is { Length: PcmBytesPerSecond } && pcm.Any(b => b != 0), "FFmpeg streams one second of stereo 48 kHz PCM");

        string invalid = Path.Combine(root, "invalid.mp3");
        await File.WriteAllTextAsync(invalid, "invalid audio");
        Check(await FfmpegInteractor.ConvertMp3ToPcmBytes(invalid).WaitAsync(TimeSpan.FromSeconds(15)) == null, "FFmpeg reports invalid media");

        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        Task serving = ServeWaveAsync(server, wave, timeout.Token);
        try
        {
            int port = ((IPEndPoint)server.LocalEndpoint).Port;
            await DownloadViaYtDlpAsync($"http://127.0.0.1:{port}/fixture.wav", root, PcmBytesPerSecond);
        }
        finally
        {
            await timeout.CancelAsync();
            try { await serving; }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        }

        using var destination = new MemoryStream();
        using (var encoder = new OpusEncodeStream(destination, PcmFormat.Short, VoiceChannels.Stereo, OpusApplication.Audio, leaveOpen: true))
            await encoder.WriteAsync(PcmTone(960 * 2, 2, 48000).AsMemory());
        Check(destination.Length > 0 && destination.Length < PcmBytesPerSecond / 25, "NetCord and packaged Opus encode stereo PCM");

        nint dave = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "libdave.dll"));
        try
        {
            Check(NativeLibrary.TryGetExport(dave, "daveSessionCreate", out _), "Packaged DAVE exposes the session API");
            nint version = NativeLibrary.GetExport(dave, "daveMaxSupportedProtocolVersion");
            Check(Marshal.GetDelegateForFunctionPointer<DaveProtocolVersion>(version)() > 0, "Packaged DAVE reports an encryption protocol version");
        }
        finally { NativeLibrary.Free(dave); }
    }

    private static async Task DownloadViaYtDlpAsync(string url, string root, int? exactBytes = null)
    {
        string appdata = Path.Combine(AppContext.BaseDirectory, "appdata");
        string source = Path.Combine(root, "yt-dlp source.mp3");
        string pcm = Path.Combine(root, "yt-dlp audio.pcm");
        YoutubeDL ytdlp = new()
        {
            YoutubeDLPath = Path.Combine(appdata, "yt-dlp.exe"),
            FFmpegPath = Path.Combine(appdata, "ffmpeg.exe"),
        };
        OptionSet options = new()
        {
            Output = source,
            Format = "bestaudio",
            IgnoreConfig = true,
            NoPlaylist = true,
            SocketTimeout = 12,
            Retries = 1,
        };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(75));
        var result = await ytdlp.RunVideoDownload(url, ct: timeout.Token, overrideOptions: options);
        Check(result.Success && File.Exists(source),
            result.Success ? "YoutubeDLSharp saves audio with the installed yt-dlp" : "yt-dlp failed. " + string.Join(" | ", result.ErrorOutput ?? []));
        Check(await FfmpegInteractor.ConvertMp3ToPcm(source, pcm).WaitAsync(TimeSpan.FromSeconds(20)) == pcm, "Kvasir sends the downloaded file through FFmpeg");
        await CheckPcmFileAsync(pcm, exactBytes);
    }

    private static async Task CheckPcmFileAsync(string path, int? exactBytes)
    {
        long length = new FileInfo(path).Length;
        Check(length >= PcmBytesPerSecond && length % 4 == 0 && (!exactBytes.HasValue || length == exactBytes),
            "Conversion produced aligned stereo PCM of the expected duration");
        Check((await File.ReadAllBytesAsync(path)).Any(b => b != 0), "Converted PCM contains a signal");
    }

    private static async Task ServeWaveAsync(TcpListener server, byte[] wave, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using TcpClient client = await server.AcceptTcpClientAsync(cancellationToken);
            await using NetworkStream stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            string? request = await reader.ReadLineAsync(cancellationToken);
            string? header;
            do { header = await reader.ReadLineAsync(cancellationToken); }
            while (!string.IsNullOrEmpty(header));

            bool found = request?.Contains(" /fixture.wav ", StringComparison.Ordinal) == true;
            string status = found ? "200 OK" : "404 Not Found";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: audio/wav\r\nContent-Length: {(found ? wave.Length : 0)}\r\nConnection: close\r\n\r\n"), cancellationToken);
            if (found && request!.StartsWith("GET ", StringComparison.Ordinal))
                await stream.WriteAsync(wave, cancellationToken);
        }
    }

    private static byte[] MakeWave()
    {
        const int sampleRate = 22050;
        byte[] samples = PcmTone(sampleRate, 1, sampleRate);
        using MemoryStream wave = new();
        using BinaryWriter writer = new(wave);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples.Length);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples.Length);
        writer.Write(samples);
        return wave.ToArray();
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ushort DaveProtocolVersion();
}
