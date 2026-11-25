using Kvasir.Media;
using Application.Services;
using Connectors.Media;
using CS_Discord_Bot.Connectors;
using Entities.Models;
using Microsoft.Extensions.Configuration;
using NetCord;
using NetCord.Gateway;

namespace Kvasir.Tests;

internal static partial class Program
{
    private const string DefaultYoutubeVideo = "https://www.youtube.com/watch?v=jNQXAC9IVRw";

    private static Task RunLiveProbeAsync(string probe, string root) => probe switch
    {
        "--live-media-probe" => ProbeYtDlpAsync(root),
        "--live-youtube-explode" => ProbeYouTubeExplodeAsync(root),
        "--live-spotify" => ProbeSpotifyAsync(),
        "--live-discord" => ProbeDiscordAsync(),
        _ => throw new ArgumentException($"Unknown live probe {probe}."),
    };

    private static async Task ProbeYtDlpAsync(string root)
    {
        var finder = new VideoFinderService(new ConfigurationBuilder().Build());
        string video = YoutubeVideo();
        Console.WriteLine("yt-dlp: video metadata");
        var tracks = await finder.Find(video);
        Check(tracks is [{ Duration: > 0, Link.Length: > 0 }] && !string.IsNullOrWhiteSpace(tracks[0].Name), "YouTube video returns one playable track");

        Console.WriteLine("yt-dlp: audio download and FFmpeg conversion");
        await ProbeDownloadAsync(tracks![0].Link!, root, Provider.YoutubeDLSharp);

        Console.WriteLine("yt-dlp: text search");
        var search = await finder.Find("Me at the zoo jawed");
        Check(search is [{ Link.Length: > 0 }], "YouTube query returns a canonical track");

        string? customPlaylist = Environment.GetEnvironmentVariable("KVASIR_PROBE_YOUTUBE_PLAYLIST");
        string playlist = string.IsNullOrWhiteSpace(customPlaylist)
            ? "https://www.youtube.com/playlist?list=UUz75RVbH8q2jdBJ4SnwuZZQ" : customPlaylist;
        Console.WriteLine("yt-dlp: playlist tracks and name");
        Check(await finder.Find(playlist) is { Count: > 1 }, "YouTube playlist returns multiple playable entries");
        Check(!string.IsNullOrWhiteSpace(await finder.GetPlaylistName(playlist)), "YouTube playlist returns a title");
    }

    private static Task ProbeYouTubeExplodeAsync(string root) => ProbeDownloadAsync(YoutubeVideo(), root, Provider.YoutubeExplode);

    private static string YoutubeVideo()
    {
        string? customVideo = Environment.GetEnvironmentVariable("KVASIR_PROBE_YOUTUBE_VIDEO");
        return string.IsNullOrWhiteSpace(customVideo) ? DefaultYoutubeVideo : customVideo;
    }

    private static async Task ProbeDownloadAsync(string link, string root, Provider provider)
    {
        string audio = Path.Combine(root, "live.pcm");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(105));
        Check(await new AudioDownloaderService().TryDownloadWithProvider(link, audio, provider, timeout.Token), $"{provider} produced PCM");
        var info = new FileInfo(audio);
        Check(info.Length >= PcmEffectMixer.BytesPerSecond && info.Length % 4 == 0, $"{provider} output has aligned stereo PCM frames");
        Check((await File.ReadAllBytesAsync(audio, timeout.Token)).Any(b => b != 0), $"{provider} output contains audible samples");
    }

    private static async Task ProbeSpotifyAsync()
    {
        var configuration = SpotifyConfiguration(
            RequiredEnvironment("KVASIR_PROBE_SPOTIFY_CLIENT_ID"), RequiredEnvironment("KVASIR_PROBE_SPOTIFY_CLIENT_SECRET"));
        string track = RequiredEnvironment("KVASIR_PROBE_SPOTIFY_TRACK");
        string playlist = RequiredEnvironment("KVASIR_PROBE_SPOTIFY_PLAYLIST");
        var finder = new VideoFinderService(new SpotifyInteractor(configuration),
            _ => Task.FromResult<List<FinderSongDTO>>([new() { Link = DefaultYoutubeVideo }]));
        TimeSpan limit = TimeSpan.FromSeconds(40);
        Console.WriteLine("Spotify: OAuth and track metadata");
        Check(await finder.Find(track).WaitAsync(limit) is [{ Name.Length: > 0, AuthorName.Length: > 0 }], "Spotify resolves the fixture track");
        Console.WriteLine("Spotify: playlist name and items");
        Check(!string.IsNullOrWhiteSpace(await finder.GetPlaylistName(playlist).WaitAsync(limit)), "Spotify resolves the fixture playlist name");
        Check(await finder.Find(playlist).WaitAsync(limit) is { Count: > 0 }, "Spotify resolves at least one track in the fixture playlist");
    }

    private static async Task ProbeDiscordAsync()
    {
        string token = RequiredEnvironment("KVASIR_PROBE_DISCORD_TOKEN");
        ulong guildId = RequiredDiscordId("KVASIR_PROBE_DISCORD_GUILD");
        ulong textId = RequiredDiscordId("KVASIR_PROBE_DISCORD_TEXT");
        ulong voiceId = RequiredDiscordId("KVASIR_PROBE_DISCORD_VOICE");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(50));
        using GatewayClient gateway = new(new BotToken(token), new()
        {
            Intents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates,
        });
        var connector = new NetCordBotConnector(gateway, gateway.Rest);

        Console.WriteLine("Discord: authenticated REST channel lookup");
        Check(await connector.IsTextChannelAsync(guildId, textId, timeout.Token), "Discord returns the fixture text channel in the configured guild");
        Console.WriteLine("Discord: gateway connection and guild cache");
        await gateway.StartAsync(cancellationToken: timeout.Token);
        while (!gateway.Cache.Guilds.ContainsKey(guildId))
            await Task.Delay(100, timeout.Token);

        Check(connector.CurrentUserId != 0 && connector.Guilds.Any(g => g.Id == guildId), "Discord gateway caches the bot identity and fixture guild");
        var channel = gateway.Cache.Guilds[guildId].Channels[voiceId] as VoiceGuildChannel;
        Check(channel != null, "Discord gateway maps the fixture voice channel");

        Console.WriteLine("Discord: voice connection, silence frame, disconnect");
        await using var connection = await connector.ConnectVoiceAsync(channel!.ToBotVoiceChannel(), timeout.Token);
        using Stream stream = connection.CreatePcmStream();
        await stream.WriteAsync(new byte[PcmEffectMixer.FrameBytes], timeout.Token);
        await stream.FlushAsync(timeout.Token);
        Check(stream.CanWrite, "Discord voice transport accepts PCM via NetCord and Opus");
    }

    private static string RequiredEnvironment(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"Set {name} for this probe.") : value;
    }

    private static ulong RequiredDiscordId(string name) =>
        ulong.TryParse(RequiredEnvironment(name), out ulong value) && value != 0
            ? value : throw new InvalidOperationException($"{name} must contain a Discord ID.");
}
