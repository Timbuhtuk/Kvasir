using System.Buffers.Binary;
using Application.Models;
using Connectors.Media;
using CS_Discord_Bot.Connectors;
using Microsoft.Extensions.Configuration;
using NetCord;
using NetCord.Gateway;

namespace Kvasir.Integration.Tests;

internal static partial class Program
{
    private const int PcmBytesPerSecond = 48000 * 2 * sizeof(short);
    private const string DefaultVideo = "https://www.youtube.com/watch?v=jNQXAC9IVRw";
    private const string DefaultPlaylist = "https://www.youtube.com/playlist?list=UUz75RVbH8q2jdBJ4SnwuZZQ";
    private static int _checks;

    public static async Task Main(string[] args)
    {
        Logging.Logger.EnableConsoleLogging = false;
        Logging.Logger.EnableFileLogging = false;
        string mode = args.SingleOrDefault() ?? "--contracts";
        string cwd = Environment.CurrentDirectory;
        string root = Directory.CreateTempSubdirectory("kvasir-integration-").FullName;
        try
        {
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
            switch (mode)
            {
                case "--contracts":
                    await CheckDiscordRestAsync();
                    await CheckSpotifyAsync();
                    await CheckSqliteAsync(root);
                    break;
                case "--native":
                    await CheckNativeAudioAsync(root);
                    break;
                case "--live-media-probe":
                    await DownloadViaYtDlpAsync(Fixture("KVASIR_PROBE_YOUTUBE_VIDEO", DefaultVideo), root);
                    break;
                case "--live-youtube-explode":
                    await ProbeYoutubeAsync(root);
                    break;
                case "--live-spotify":
                    await ProbeSpotifyAsync();
                    break;
                case "--live-discord":
                    await ProbeDiscordAsync();
                    break;
                default:
                    throw new ArgumentException($"Unknown integration test mode: {mode}.");
            }
            Console.WriteLine($"PASS {mode} ({_checks} checks)");
        }
        finally
        {
            Directory.SetCurrentDirectory(cwd);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (Path.GetDirectoryName(Path.GetFullPath(root)) == temp && Path.GetFileName(root).StartsWith("kvasir-integration-", StringComparison.Ordinal))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
        Console.WriteLine($"PASS {message}");
    }

    private static string Fixture(string name, string fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string Required(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"Set {name} for this probe.") : value;
    }

    private static async Task ProbeYoutubeAsync(string root)
    {
        var finder = new VideoFinderService(new ConfigurationBuilder().Build());
        string video = Fixture("KVASIR_PROBE_YOUTUBE_VIDEO", DefaultVideo);
        string playlist = Fixture("KVASIR_PROBE_YOUTUBE_PLAYLIST", DefaultPlaylist);
        var mediaLimit = TimeSpan.FromSeconds(40);
        var tracks = await finder.Find(video).WaitAsync(mediaLimit);
        Check(tracks is [{ Duration: > 0, Name.Length: > 0, Link.Length: > 0 }], "YoutubeExplode resolves video metadata");

        // Give the bot's downloader FFmpeg and no yt-dlp binary so success requires the primary provider.
        string appdata = Directory.CreateDirectory(Path.Combine(root, "appdata")).FullName;
        File.Copy(Path.Combine(AppContext.BaseDirectory, "appdata", "ffmpeg.exe"), Path.Combine(appdata, "ffmpeg.exe"));
        Directory.SetCurrentDirectory(root);
        string pcm = Path.Combine(root, "youtube-explode.pcm");
        bool downloaded = await new AudioDownloaderService().DownloadAsync(tracks![0].Link, pcm).WaitAsync(TimeSpan.FromSeconds(70));
        Check(downloaded, "Kvasir downloads audio with YoutubeExplode");
        await CheckPcmFileAsync(pcm, exactBytes: null);

        Check(await finder.Find("Me at the zoo jawed").WaitAsync(mediaLimit) is [{ Link.Length: > 0 }], "YoutubeExplode resolves a text search");
        Check(await finder.Find(playlist).WaitAsync(mediaLimit) is { Count: > 1 }, "YoutubeExplode enumerates a public playlist");
        Check(!string.IsNullOrWhiteSpace(await finder.GetPlaylistName(playlist).WaitAsync(mediaLimit)), "YoutubeExplode resolves the playlist title");
    }

    private static async Task ProbeSpotifyAsync()
    {
        IConfiguration config = SpotifyConfiguration(Required("KVASIR_PROBE_SPOTIFY_CLIENT_ID"), Required("KVASIR_PROBE_SPOTIFY_CLIENT_SECRET"));
        string track = Required("KVASIR_PROBE_SPOTIFY_TRACK");
        string playlist = Required("KVASIR_PROBE_SPOTIFY_PLAYLIST");
        var limit = TimeSpan.FromSeconds(45);
        var finder = new VideoFinderService(config);
        Check(!string.IsNullOrWhiteSpace(await finder.GetPlaylistName(playlist).WaitAsync(limit)), "Spotify OAuth and playlist lookup");
        Check(await finder.FindOnSpotifyAsync(track).WaitAsync(limit) is [{ Name.Length: > 0, AuthorName.Length: > 0 }], "Kvasir maps a Spotify track to a playable result");
        Check(await finder.FindOnSpotifyAsync(playlist).WaitAsync(limit) is { Count: > 0 }, "Kvasir imports tracks from a Spotify playlist");
    }

    private static async Task ProbeDiscordAsync()
    {
        string token = Required("KVASIR_PROBE_DISCORD_TOKEN");
        ulong guildId = DiscordId("KVASIR_PROBE_DISCORD_GUILD");
        ulong textId = DiscordId("KVASIR_PROBE_DISCORD_TEXT");
        ulong voiceId = DiscordId("KVASIR_PROBE_DISCORD_VOICE");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(50));
        using GatewayClient gateway = new(new BotToken(token), new()
        {
            Intents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates,
        });
        var connector = new NetCordBotConnector(gateway, gateway.Rest);
        Check(await connector.IsTextChannelAsync(guildId, textId, timeout.Token), "Discord REST resolves the configured text channel");

        await gateway.StartAsync(cancellationToken: timeout.Token);
        while (!gateway.Cache.Guilds.ContainsKey(guildId))
            await Task.Delay(100, timeout.Token);
        Check(connector.CurrentUserId != 0 && connector.Guilds.Any(g => g.Id == guildId), "Discord Gateway caches the configured guild and bot");
        Check(gateway.Cache.Guilds[guildId].Channels.TryGetValue(voiceId, out var voice) && voice is VoiceGuildChannel, "Discord Gateway resolves the voice channel");

        await using (var connection = await connector.ConnectVoiceAsync(new BotVoiceChannel(guildId, voiceId, "test"), timeout.Token).WaitAsync(timeout.Token))
        {
            await using Stream output = connection.CreatePcmStream();
            await output.WriteAsync(new byte[PcmBytesPerSecond / 50], timeout.Token);
            await output.FlushAsync(timeout.Token);
            Check(output.CanWrite, "Discord voice transport accepts one Opus silence frame");
        }
        using CancellationTokenSource disconnect = new(TimeSpan.FromSeconds(10));
        await gateway.UpdateVoiceStateAsync(new(guildId, null), cancellationToken: disconnect.Token);
    }

    private static ulong DiscordId(string name) =>
        ulong.TryParse(Required(name), out ulong id) && id != 0
            ? id : throw new InvalidOperationException($"{name} must be a Discord ID.");

    private static byte[] PcmTone(int samplesPerChannel, int channels, int sampleRate)
    {
        byte[] pcm = new byte[samplesPerChannel * channels * sizeof(short)];
        for (int q = 0; q < samplesPerChannel; q++)
        {
            short value = (short)(Math.Sin(q * 2 * Math.PI * 440 / sampleRate) * 2000);
            for (int e = 0; e < channels; e++)
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((q * channels + e) * sizeof(short)), value);
        }
        return pcm;
    }
}
