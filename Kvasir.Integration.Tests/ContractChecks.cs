using System.Net;
using System.Text;
using System.Text.Json;
using Application.Models;
using Connectors.Media;
using CS_Discord_Bot.Connectors;
using Infrastructure;
using Infrastructure.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Http;

namespace Kvasir.Integration.Tests;

internal static partial class Program
{
    private static async Task CheckDiscordRestAsync()
    {
        const string textChannel = """{"id":"11","type":0,"guild_id":"22","name":"music"}""";
        const string category = """{"id":"12","type":4,"guild_id":"22","name":"category"}""";
        const string message = """
            {"id":"33","channel_id":"11","guild_id":"22","type":0,"content":"Music controls",
             "author":{"id":"44","username":"fixture","discriminator":"0","bot":true},
             "timestamp":"2026-01-01T00:00:00Z","tts":false,"mention_everyone":false,"mentions":[],
             "mention_roles":[],"attachments":[],"embeds":[]}
            """;
        var handler = new ContractHttpHandler(async (request, ct) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            return (request.Method.Method, path) switch
            {
                ("GET", "/api/v10/channels/11") => JsonResponse(textChannel),
                ("GET", "/api/v10/channels/12") => JsonResponse(category),
                ("POST", "/api/v10/channels/11/messages") => await ValidateMessageAsync(request, ct, updated: false),
                ("PATCH", "/api/v10/channels/11/messages/33") => await ValidateMessageAsync(request, ct, updated: true),
                ("DELETE", "/api/v10/channels/11/messages/33") => new(HttpStatusCode.NoContent),
                ("GET", "/api/v10/channels/99") => JsonResponse("""{"code":10003,"message":"Unknown Channel"}""", HttpStatusCode.NotFound),
                _ => throw new InvalidOperationException($"Unexpected Discord REST request {request.Method} {path}."),
            };
        });

        async Task<HttpResponseMessage> ValidateMessageAsync(HttpRequestMessage request, CancellationToken ct, bool updated)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            JsonElement body = document.RootElement;
            Check(body.GetProperty("content").GetString() == "Music controls", "Discord REST preserves message text");
            if (updated)
            {
                Check(body.GetProperty("embeds")[0].GetProperty("description").GetString() == "Now playing", "Discord REST serializes the embed");
                var components = body.GetProperty("components");
                Check(components[0].GetProperty("components")[0].GetProperty("custom_id").GetString() == "play", "Discord REST serializes the control button");
                Check(components[1].GetProperty("components")[0].GetProperty("options")[0].GetProperty("value").GetString() == "one",
                    "Discord REST serializes the playlist option inside its action row");
            }
            return JsonResponse(message);
        }

        using GatewayClient gateway = new(new BotToken("NDQ.fixture.fixture"), new()
        {
            RestClientConfiguration = new() { RequestHandler = new RestRequestHandler(handler) },
        });
        var connector = new NetCordBotConnector(gateway, gateway.Rest);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Check(await connector.IsTextChannelAsync(22, 11, timeout.Token), "Discord resolves a guild text channel");
        Check(!await connector.IsTextChannelAsync(23, 11, timeout.Token), "Discord rejects a channel from a different guild");
        Check(!await connector.IsTextChannelAsync(22, 12, timeout.Token), "Discord rejects a category as a text channel");

        var panel = new BotMessageContent("Music controls", new("Now playing", 0x123456, "Kvasir"),
            [new BotComponentRow([new BotButton("play", "Play", BotButtonStyle.Primary)]),
             new BotSelect("playlist", "Playlist", [new("One", "one")])]);
        BotMessage sent = await connector.SendMessageAsync(11, panel, timeout.Token);
        Check(sent is { Id: 33, ChannelId: 11, GuildId: 22, AuthorId: 44 }, "Discord maps the send response");
        await connector.UpdateMessageAsync(11, sent.Id, panel, timeout.Token);
        await connector.DeleteMessageAsync(11, sent.Id, timeout.Token);
        Check(handler.Methods.Where(m => m != HttpMethod.Get).SequenceEqual([HttpMethod.Post, HttpMethod.Patch, HttpMethod.Delete]),
            "Discord sends, edits and deletes the message exactly once");
        try
        {
            await connector.IsTextChannelAsync(22, 99, timeout.Token);
            Check(false, "Discord must report an unknown channel");
        }
        catch (RestException ex)
        {
            Check(ex.StatusCode == HttpStatusCode.NotFound, "Discord preserves REST 404");
        }
    }

    private static async Task CheckSpotifyAsync()
    {
        var disabled = new ConfigurationBuilder().Build();
        Check(await new VideoFinderService(disabled).FindOnSpotifyAsync("https://open.spotify.com/track/fixture") == null,
            "Kvasir leaves Spotify disabled without configuration");
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["spotify_settings:SPOTIFY_ENABLED"] = "true" }).Build();
            await new SpotifyInteractor(config).GetClientAsync();
            Check(false, "Missing Spotify credentials must fail");
        }
        catch (InvalidOperationException ex)
        {
            Check(ex.Message.Contains("client ID", StringComparison.Ordinal), "Spotify reports a missing client ID before OAuth");
        }

        const string track = """{"type":"track","id":"fixture","name":"First song","artists":[{"name":"First artist"}]}""";
        const string playlist = """
            {"id":"fixture","name":"Fixture playlist","items":{"href":"https://api.spotify.com/v1/playlists/fixture/items",
             "limit":1,"offset":0,"total":1,"next":null,"items":[{"item":TRACK_JSON}]}}
            """;
        using var handler = new ContractHttpHandler(async (request, ct) =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "accounts.spotify.com" && uri.AbsolutePath == "/api/token" && request.Method == HttpMethod.Post)
            {
                string form = await request.Content!.ReadAsStringAsync(ct);
                Check(form.Contains("grant_type=client_credentials", StringComparison.Ordinal), "Spotify SDK uses the Client Credentials grant");
                return JsonResponse("""{"access_token":"fixture-access","token_type":"Bearer","expires_in":3600}""");
            }

            Check(uri.Host == "api.spotify.com" && request.Method == HttpMethod.Get, "Spotify SDK uses the expected API host and method");
            Check(request.Headers.Authorization is { Scheme: "Bearer", Parameter: "fixture-access" }, "Spotify SDK sends the OAuth token");
            return uri.AbsolutePath switch
            {
                "/v1/tracks/fixture" => JsonResponse(track),
                "/v1/playlists/fixture" => JsonResponse(playlist.Replace("TRACK_JSON", track, StringComparison.Ordinal)),
                _ => throw new InvalidOperationException($"Unexpected Spotify route {uri.PathAndQuery}."),
            };
        });
        using HttpClient http = new(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var sdkConfig = SpotifyClientConfig.CreateDefault().WithHTTPClient(new NetHttpClient(http));
        var credentials = await new OAuthClient(sdkConfig).RequestToken(new ClientCredentialsRequest("fixture-id", "fixture-secret"));
        var spotify = new SpotifyClient(sdkConfig.WithToken(credentials.AccessToken));
        Check(await spotify.Tracks.Get("fixture") is { Name: "First song", Artists: [{ Name: "First artist" }] }, "Spotify SDK deserializes track and artist metadata");
        Check(await spotify.Playlists.Get("fixture") is { Name: "Fixture playlist", Items.Items: [{ Item: FullTrack { Id: "fixture" } }] },
            "Spotify SDK deserializes the current items/item playlist response");
    }

    private static IConfiguration SpotifyConfiguration(string id, string secret) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["spotify_settings:SPOTIFY_ENABLED"] = "true",
            ["spotify_settings:SPOTIFY_CLIENT_ID"] = id,
            ["spotify_settings:SPOTIFY_CLIENT_SECRET"] = secret,
        }).Build();

    private static async Task CheckSqliteAsync(string root)
    {
        var config = new ConfigurationBuilder().Build();
        var options = new DbContextOptionsBuilder<DiscordMusicDBContext>()
            .UseSqlite("Data Source=" + Path.Combine(root, "legacy.db")).Options;
        await using var db = new DiscordMusicDBContext(options);
        await db.GetService<IMigrator>().MigrateAsync("20251114204016_InitialCreate");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO Song (id, Name, Views) VALUES (42, 'Legacy track', 7)");
        await db.Database.MigrateAsync();
        var repo = new SongRepository(db, new VideoFinderService(config), new AudioDownloaderService(), config);
        var song = await repo.GetByIdAsync(42);
        Check(song is { Name: "Legacy track", IsDownloaded: false, Views: 7 }, "SQLite migrations upgrade and preserve an existing track");
        Check(await repo.IncrementViewsAsync(song!), "EF Core updates playback statistics");
        Check((await repo.GetPopularSongsListAsync()).Single().Views == 8, "SQLite persists the playback statistics");
        Check(!(await db.Database.GetPendingMigrationsAsync()).Any(), "SQLite leaves no pending migrations");
    }

    private static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class ContractHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public List<HttpMethod> Methods { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            return send(request, cancellationToken);
        }
    }
}
