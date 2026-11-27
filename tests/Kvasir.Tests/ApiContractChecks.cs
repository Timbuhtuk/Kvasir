using Kvasir.Media;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Application.Interfaces;
using Application.Models;
using Application.Services;
using Connectors.Media;
using CS_Discord_Bot.Commands;
using CS_Discord_Bot.Connectors;
using CS_Discord_Bot.DesktopControl;
using Entities.Models;
using Infrastructure;
using Kvasir.Control;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetCord;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using NetCord.Rest;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Http;

namespace Kvasir.Tests;

internal static partial class Program
{
    private static async Task TestDiscordHostAsync(string root)
    {
        string path = Path.Combine(root, "bot-configuration.json");
        await File.WriteAllTextAsync(path, """{"tokens":["NDQ.fixture.fixture"],"connection_string":"Data Source=:memory:"}""");
        IHost host = CS_Discord_Bot.Program.CreateHostBuilder([], path).Build();
        await using var lifetime = (IAsyncDisposable)host;
        CS_Discord_Bot.Program.RegisterCommands(host);
        var commands = host.Services.GetRequiredService<NetCord.Services.ApplicationCommands.ApplicationCommandService<NetCord.Services.ApplicationCommands.ApplicationCommandContext>>();
        var help = CommonCommands.HelpPages(commands).ToArray();
        Check(help.All(page => page.Length <= 2000) && help.SelectMany(page => page.Split('\n')).Select(line => line.Split(' ')[0])
            .SequenceEqual(commands.GetCommands().Where(c => c.Register && c.Type == ApplicationCommandType.ChatInput)
                .Select(c => "/" + c.Name).Order(StringComparer.Ordinal)), "Help lists each registered slash command in order");
        Check(host.Services.GetRequiredService<IBotConnector>() is NetCordBotConnector, "The production host resolves the Discord connector after registering command modules");
        var gateway = host.Services.GetRequiredService<GatewayClient>();
        Check(gateway.Token.Id == 44 && gateway.Cache.User == null, "The production host configures the expected Discord identity before connecting");
    }

    private static async Task TestDesktopPlaylistImportAsync(ServiceProvider provider, string root)
    {
        using GatewayClient gateway = new(new BotToken("NDQ.fixture.fixture"), new());
        var connector = new FakeConnector { Guilds = [new(1, "One"), new(2, "Two")] };
        await using var guilds = new GuildService(provider.GetRequiredService<IServiceScopeFactory>(), connector);
        var library = provider.GetRequiredService<IMusicLibrary>();
        var mediaActivity = new MediaActivity();
        using var service = new DesktopControlService(gateway, connector, guilds, library, provider.GetRequiredService<PlaylistImportService>(),
            provider.GetRequiredService<IMusicDiagnostics>(), provider.GetRequiredService<IMediaCache>(),
            new SoundboardLibrary(new SoundEffectDecoder(), Path.Combine(root, "playlist-import-soundboard")),
            provider.GetRequiredService<IVideoFinderService>(), mediaActivity, provider.GetRequiredService<TrackService>());
        var handle = typeof(DesktopControlService).GetMethod("HandleAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Task<ControlResponse> Send(ControlRequest request) =>
            (Task<ControlResponse>)handle.Invoke(service, [request, CancellationToken.None])!;

        var empty = await Send(new("fixture", "playlist-create", 1, Value: "Empty desktop playlist"));
        Check(empty is { Success: true, State: { Import: null, Songs.Length: 0 } } &&
              empty.State.Playlists.Any(p => p.Name == "Empty desktop playlist"), "Plain text creates an empty playlist without looking up a source");
        var finder = (FakeFinder)provider.GetRequiredService<IVideoFinderService>();
        var downloader = (FakeDownloader)provider.GetRequiredService<IAudioDownloaderService>();
        var metadataSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        finder.BeforeNextFixtureBatch = token => { metadataSaved.TrySetResult(); return nextBatch.Task.WaitAsync(token); };
        downloader.BeforeDownload = (link, token) =>
        {
            if (link != "test:41") return Task.CompletedTask;
            downloading.TrySetResult();
            return finishDownload.Task.WaitAsync(token);
        };
        try
        {
            var created = await TestMediaStatusPipeAsync(service, mediaActivity, finder);
            int id = created.State?.PlaylistId ?? 0;
            Check(created.Success && id > 0 && created.MessageKey == "PlaylistCreatedImportStarted" &&
                  created.State!.Playlists.Any(p => p.Id == id && p.Shared && p.Name == "Imported"), "Desktop uses the collection's title and returns its new playlist ID");
            ControlRequest request = new("fixture", GuildId: 1, PlaylistId: id);
            await metadataSaved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check((await Send(request)).State?.Songs is [{ Name: "Good A", DownloadState: TrackDownloadState.Pending }],
                "The first track is visible as soon as its metadata is saved, while more metadata is still pending");
            nextBatch.TrySetResult();
            await downloading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check((await Send(request)).State?.Songs is [
                { DownloadState: TrackDownloadState.Downloading }, { DownloadState: TrackDownloadState.Pending }, { DownloadState: TrackDownloadState.Pending }],
                "The snapshot marks only the track whose audio is being downloaded");
            finishDownload.TrySetResult();
            var imports = provider.GetRequiredService<PlaylistImportService>();
            await Until(() => !imports.IsImportRunning(1, connector.CurrentUserId));
            var imported = (await Send(request)).State;
            Check(imported is { Import: { Finished: true, Completed: 3, PlaylistId: var importedId }, Songs: [
                { DownloadState: TrackDownloadState.Downloaded }, { DownloadState: TrackDownloadState.Pending }, { DownloadState: TrackDownloadState.Downloaded }] } && importedId == id,
                "Successful downloads clear their indicators and a failed download leaves the track in the playlist");

            int[] order = imported!.Songs.Select(song => song.Id).ToArray();
            var move = request with { Action = "playlist-move-track", SongId = order[2], AnchorSongId = order[0] };
            var moved = await Send(move);
            Check(moved.Success && moved.State!.Songs.Select(song => song.Id).SequenceEqual(new[] { order[2], order[0], order[1] }),
                "Dropping before a track saves the new playlist order");
            var restored = await Send(move with { AnchorSongId = order[1], InsertAfter = true });
            Check(restored.Success && restored.State!.Songs.Select(song => song.Id).SequenceEqual(order),
                "Dropping after a track adjusts for the removed position");
            Check((await Send(move with { AnchorSongId = int.MaxValue })) is { Success: false, ErrorCode: "input", MessageKey: "PlaylistDropTrackMissing" },
                "A missing drop target does not alter the playlist");

            int destination = empty.State!.PlaylistId!.Value;
            var copy = request with { Action = "playlist-copy-track", SongId = order[1], DestinationPlaylistId = destination };
            var copied = await Send(copy);
            Check(copied is { Success: true, MessageKey: "PlaylistTrackCopied" } && copied.State!.PlaylistId == id &&
                  copied.State.Songs.Select(song => song.Id).SequenceEqual(order) && copied.State.Playlists.Single(p => p.Id == destination).Count == 1,
                "Copy refreshes the destination count and preserves the selected source and its order");
            var secondCopy = await Send(copy with { SongId = order[0] });
            Check(secondCopy.Success && (await Send(copy)).MessageKey == "PlaylistTrackAlreadyPresent" &&
                  (await Send(request with { PlaylistId = destination })).State!.Songs.Select(song => song.Id).SequenceEqual(new[] { order[1], order[0] }),
                "Copies append in drop order and retries do not insert duplicates");

            var guest = new MusicActor(9876, null);
            var guestPlaylist = await library.CreateAsync(1, guest, "Guest copy", false);
            Check(!(await library.MoveTrackAsync(1, guest, id, order[1], order[0], false)).Success &&
                  !(await library.CopyTrackAsync(1, guest, id, destination, order[1])).Success &&
                  (await library.CopyTrackAsync(1, guest, id, guestPlaylist.Id, order[1])).Success &&
                  !(await library.CopyTrackAsync(1, guest, destination, guestPlaylist.Id, order[0])).Success,
                "Copy requires visibility of the source and edit rights on the destination");

            var local = await Send(new("fixture", "playlist-create", 1,
                Value: "https://open.spotify.com/album/fixture") { CreateLocalPlaylist = true });
            int localId = local.State?.PlaylistId ?? 0;
            Check(local.Success && local.State?.Playlists.FirstOrDefault() is { IsLocal: true, Shared: false } item && item.Id == localId,
                "A local playlist created from a link is listed ahead of server playlists");
            await Until(() => !imports.IsImportRunning(0, connector.CurrentUserId));
            var otherRequest = new ControlRequest("fixture", GuildId: 2, PlaylistId: localId);
            var other = (await Send(otherRequest)).State;
            Check(other is { PlaylistId: var selected, LocalImport: { Finished: true, Completed: 3 }, Songs.Length: 3 } &&
                  selected == localId && other.Playlists.Single().Id == localId,
                "The same local playlist, track data and import report are available from another server");

            var discordManager = new MusicActor(connector.CurrentUserId, null, IsManager: true);
            Check((await library.ListAsync(1, discordManager)).All(p => p.Id != localId) &&
                  await library.GetAsync(2, discordManager, localId) == null &&
                  !(await library.EditAsync(1, discordManager, localId, "delete")).Success,
                "Discord callers cannot discover or change local playlists even with manager rights");
            Check(!(await Send(otherRequest with { Action = "playlist-share" })).Success,
                "The local playlist cannot be shared on the server");
            var renamed = await Send(otherRequest with { Action = "playlist-rename", Value = "My playlist" });
            Check(renamed.Success && (await Send(request with { PlaylistId = localId })).State?.Playlists[0].Name == "My playlist",
                "Renaming from another server updates the original local playlist");
            var copyFromLocal = await Send(copy with { PlaylistId = localId, SongId = order[2] });
            Check(copyFromLocal is { Success: true, MessageKey: "PlaylistTrackCopied" }, "Local tracks can be copied into a server playlist");
            var removed = await Send(otherRequest with { Action = "playlist-delete" });
            Check(removed.Success && (await Send(request)).State?.Playlists.All(p => p.Id != localId) == true,
                "Deleting from another server removes the local playlist everywhere");

            var author = new MusicActor(connector.CurrentUserId, null) { CanAccessLocalPlaylists = true };
            Check((await library.EditAsync(1, author, destination, "add", songId: 27)).Success, "A pending track is available to retry");
            var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finishRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int downloads = 0;
            downloader.BeforeDownload = (link, token) =>
            {
                if (link != "test:27") return Task.CompletedTask;
                Interlocked.Increment(ref downloads);
                retryStarted.TrySetResult();
                return finishRetry.Task.WaitAsync(token);
            };
            var retry = request with { Action = "playlist-retry-track", PlaylistId = destination, SongId = 27 };
            try
            {
                Check((await Send(retry with { SongId = int.MaxValue })) is { Success: false, ErrorCode: "input", MessageKey: "PlaylistTrackUnavailable" },
                    "Only a member of the selected playlist can be retried");
                Check((await Send(retry)) is { Success: true, MessageKey: "TrackDownloadQueued" },
                    "A retry schedules just the selected track without waiting for its audio");
                await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var repeated = (await Send(retry)).State!;
                Check(repeated.Queue.Length == 0 && repeated.Songs.Single(song => song.Id == 27).DownloadState == TrackDownloadState.Downloading,
                    "Retry prepares audio without queuing playback and reports an in-progress download");
            }
            finally { finishRetry.TrySetResult(); }
            using var retryScope = provider.CreateScope();
            var retryDb = retryScope.ServiceProvider.GetRequiredService<DiscordMusicDBContext>();
            await Until(() => retryDb.Songs.Any(song => song.Id == 27 && song.IsDownloaded));
            var ready = await Send(retry);
            Check(ready.MessageKey == "TrackAlreadyDownloaded" && downloads == 1 &&
                  ready.State!.Songs.Single(song => song.Id == 27).DownloadState == TrackDownloadState.Downloaded,
                "A repeated retry deduplicates downloads and leaves the downloaded track ready");
        }
        finally
        {
            nextBatch.TrySetResult(); finishDownload.TrySetResult();
            finder.BeforeNextFixtureBatch = null; downloader.BeforeDownload = null;
        }
    }

    private static async Task<ControlResponse> TestMediaStatusPipeAsync(DesktopControlService service, MediaActivity activity, FakeFinder finder)
    {
        string pipeName = "Kvasir-" + Guid.NewGuid().ToString("N");
        string? previousPipe = Environment.GetEnvironmentVariable("KVASIR_CONTROL_PIPE");
        string? previousSession = Environment.GetEnvironmentVariable("KVASIR_CONTROL_SESSION");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var searching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        finder.TitleLookup = url => activity.RunAsync<string?>(url, MediaStage.Search, async _ =>
        {
            searching.TrySetResult();
            await release.Task.WaitAsync(timeout.Token);
            return "Imported";
        });
        async Task<ControlResponse> SendAsync(string suffix, ControlRequest request)
        {
            using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", pipeName + suffix,
                System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
            await ControlProtocol.WriteAsync(pipe, request, timeout.Token);
            return await ControlProtocol.ReadAsync<ControlResponse>(pipe, timeout.Token);
        }
        try
        {
            Environment.SetEnvironmentVariable("KVASIR_CONTROL_PIPE", pipeName);
            Environment.SetEnvironmentVariable("KVASIR_CONTROL_SESSION", "fixture");
            await service.StartAsync(timeout.Token);
            var command = SendAsync("", new("fixture", "playlist-create", 1, Value: FakeFinder.DesktopCollection, Number: 1));
            await searching.Task.WaitAsync(timeout.Token);
            var progress = await SendAsync("-media", new("fixture", "media-status"));
            Check(!command.IsCompleted && progress.MediaWork is [{ Stage: "Search", Subject: FakeFinder.DesktopCollection }],
                "The media pipe reports search progress while the normal command pipe is busy");
            release.TrySetResult();
            return await command;
        }
        finally
        {
            release.TrySetResult();
            finder.TitleLookup = null;
            await service.StopAsync(CancellationToken.None);
            Environment.SetEnvironmentVariable("KVASIR_CONTROL_PIPE", previousPipe);
            Environment.SetEnvironmentVariable("KVASIR_CONTROL_SESSION", previousSession);
        }
    }

    private static async Task TestDiscordRestAsync()
    {
        const string textChannel = """{"id":"11","type":0,"guild_id":"22","name":"music"}""";
        const string category = """{"id":"12","type":4,"guild_id":"22","name":"category"}""";
        const string message = """
            {"id":"33","channel_id":"11","guild_id":"22","type":0,"content":"","author":{"id":"44","username":"fixture","discriminator":"0","bot":true},
             "timestamp":"2026-01-01T00:00:00Z","tts":false,"mention_everyone":false,"mentions":[],"mention_roles":[],"attachments":[],"embeds":[],"flags":32768}
            """;
        var handler = new ContractHttpHandler(async (request, cancellationToken) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            return (request.Method.Method, path) switch
            {
                ("GET", "/api/v10/channels/11") => JsonResponse(textChannel),
                ("GET", "/api/v10/channels/12") => JsonResponse(category),
                ("POST", "/api/v10/channels/11/messages") => await ValidateMessageAsync(request, cancellationToken),
                ("PATCH", "/api/v10/channels/11/messages/33") => await ValidateMessageAsync(request, cancellationToken),
                ("PATCH", "/api/v10/webhooks/44/fixture/messages/@original") => await ValidateMessageAsync(request, cancellationToken),
                ("POST", "/api/v10/interactions/55/fixture/callback") => await ValidateDeferralAsync(request, cancellationToken),
                ("DELETE", "/api/v10/channels/11/messages/33") => new(HttpStatusCode.NoContent),
                ("GET", "/api/v10/channels/99") => JsonResponse("""{"code":10003,"message":"Unknown Channel"}""", HttpStatusCode.NotFound),
                _ => throw new InvalidOperationException($"Unexpected Discord REST request {request.Method} {path}."),
            };
        });

        async Task<HttpResponseMessage> ValidateDeferralAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Check(document.RootElement.GetProperty("type").GetInt32() == 5 &&
                document.RootElement.GetProperty("data").GetProperty("flags").GetInt32() == 64,
                "Discord accepts the command's deferred ephemeral response shape");
            return new(HttpStatusCode.NoContent);
        }

        async Task<HttpResponseMessage> ValidateMessageAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            JsonElement body = document.RootElement;
            Check((body.GetProperty("flags").GetInt32() & 32768) != 0, "Discord REST sends the Components V2 flag");
            Check(body.GetProperty("components")[0].GetProperty("components")[0].GetProperty("content").GetString() == "fixture", "Discord REST sends nested V2 content");
            Check(body.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength() == 0, "Discord REST disables automatic mentions");
            Check(!body.TryGetProperty("content", out var content) || content.ValueKind == JsonValueKind.Null, "Discord V2 payload has no legacy content");
            return JsonResponse(message);
        }

        using GatewayClient gateway = new(new BotToken("NDQ.fixture.fixture"), new()
        {
            RestClientConfiguration = new() { RequestHandler = new RestRequestHandler(handler) },
        });
        var connector = new NetCordBotConnector(gateway, gateway.Rest);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Check(await connector.IsTextChannelAsync(22, 11, timeout.Token), "Discord text channel matched to its guild");
        Check(!await connector.IsTextChannelAsync(23, 11, timeout.Token), "Discord text channel from a different guild rejected");
        Check(!await connector.IsTextChannelAsync(22, 12, timeout.Token), "Discord category rejected as a text channel");

        var panel = new BotMessageContent("legacy", Components: [new BotContainer([new BotText("fixture")])], ComponentsV2: true);
        BotMessage sent = await connector.SendMessageAsync(11, panel, timeout.Token);
        Check(sent.Id == 33 && sent.GuildId == 22 && sent.AuthorId == 44, "Discord send response mapped into the application message");
        await connector.UpdateMessageAsync(11, sent.Id, panel, timeout.Token);
        await connector.DeleteMessageAsync(11, sent.Id, timeout.Token);
        var interactionData = JsonSerializer.Deserialize<NetCord.JsonModels.JsonInteraction>("""
            {"id":"55","application_id":"44","type":2,"token":"fixture","version":1,"channel_id":"11",
             "channel":{"id":"11","type":0,"guild_id":"22","name":"music"},
             "user":{"id":"77","username":"listener","discriminator":"0"},"locale":"en-US","app_permissions":0,
             "entitlements":[],"authorizing_integration_owners":{},"data":{"id":"66","name":"panel","type":1}}
            """, new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString })!;
        Interaction interaction = Interaction.CreateFromJson(interactionData, gateway.Cache, gateway.Rest);
        await interaction.SendResponseAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral), cancellationToken: timeout.Token);
        await MusicPanelActions.ReplyAsync(interaction, panel);
        Check(handler.Requests.Select(r => r.Method).SequenceEqual(["GET", "GET", "GET", "GET", "POST", "PATCH", "DELETE", "POST", "PATCH"]),
            "Discord sends one REST request for each mutation, including slash response and edit");
        try
        {
            await connector.IsTextChannelAsync(22, 99, timeout.Token);
            Check(false, "Expected Discord HTTP 404");
        }
        catch (RestException ex)
        {
            Check(ex.StatusCode == HttpStatusCode.NotFound, "Discord REST propagates a meaningful 404");
        }
    }

    private static async Task TestSpotifyContractAsync()
    {
        const string first = """{"type":"track","id":"one","name":"First song","artists":[{"name":"First artist"},{"name":"Guest"}]}""";
        const string second = """{"type":"track","id":"two","name":"Second song","artists":[{"name":"Second artist"}]}""";
        const string playlist = """
            {"id":"fixture","name":"Fixture playlist","items":{"href":"https://api.spotify.com/v1/playlists/fixture/items",
             "limit":1,"offset":0,"total":2,"next":"https://api.spotify.com/v1/playlists/fixture/items?offset=1","items":[{"item":FIRST_TRACK}]}}
            """;
        const string album = """
            {"name":"Fixture album","tracks":{"items":[FIRST_TRACK],
             "next":"https://api.spotify.com/v1/albums/fixture/tracks?offset=1","total":2,"limit":1,"offset":0}}
            """;
        const string publicEmbed = """
            <script id="__NEXT_DATA__" type="application/json">
            {"props":{"pageProps":{"state":{"data":{"entity":{"uri":"spotify:playlist:PLAYLIST_ID","title":"Public playlist",
            "trackList":[{"uri":"spotify:track:one","title":"First song","subtitle":"First artist"},
                         {"uri":"spotify:episode:skip","title":"Podcast episode","subtitle":"Host"},
                         {"uri":"spotify:track:two","title":"Second song","subtitle":"Second artist"}]}}}}}}
            </script>
            """;
        var handler = new ContractHttpHandler(async (request, cancellationToken) =>
        {
            Uri uri = request.RequestUri!;
            if (uri.Host == "accounts.spotify.com" && uri.AbsolutePath == "/api/token" && request.Method == HttpMethod.Post)
            {
                string form = await request.Content!.ReadAsStringAsync(cancellationToken);
                Check(form.Contains("grant_type=client_credentials", StringComparison.Ordinal), "Spotify SDK requests the expected OAuth grant");
                return JsonResponse("""{"access_token":"fixture-access","token_type":"Bearer","expires_in":3600}""");
            }

            Check(uri.Host == "api.spotify.com" && request.Method == HttpMethod.Get, "Spotify SDK uses the expected resource host and method");
            Check(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "fixture-access", "Spotify sends the OAuth token to the API");
            return uri.AbsolutePath switch
            {
                "/v1/tracks/one" => JsonResponse(first),
                "/v1/playlists/fixture" => JsonResponse(playlist.Replace("FIRST_TRACK", first, StringComparison.Ordinal)),
                "/v1/playlists/fixture/items" when uri.Query == "?offset=1" => JsonResponse("""{"limit":1,"offset":1,"total":2,"next":null,"items":[{"item":SECOND_TRACK}]}"""
                    .Replace("SECOND_TRACK", second, StringComparison.Ordinal)),
                "/v1/playlists/old" => JsonResponse("""{"name":"Legacy","tracks":{"items":[{"track":FIRST_TRACK}],"next":null}}"""
                    .Replace("FIRST_TRACK", first, StringComparison.Ordinal)),
                "/v1/albums/fixture" => JsonResponse(album.Replace("FIRST_TRACK", first, StringComparison.Ordinal)),
                "/v1/albums/fixture/tracks" when uri.Query == "?offset=1" =>
                    JsonResponse("""{"limit":1,"offset":1,"total":2,"next":null,"items":[SECOND_TRACK]}"""
                        .Replace("SECOND_TRACK", second, StringComparison.Ordinal)),
                "/v1/playlists/public" => JsonResponse("""{"name":"Public playlist","public":true}"""),
                "/v1/playlists/denied" => JsonResponse("""{"error":{"status":403,"message":"Forbidden"}}""", HttpStatusCode.Forbidden),
                _ => throw new InvalidOperationException($"Unexpected Spotify request {uri.PathAndQuery}."),
            };
        });
        using HttpClient http = new(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var spotify = new SpotifyInteractor(SpotifyConfiguration("fixture-id", "fixture-secret"),
            SpotifyClientConfig.CreateDefault().WithHTTPClient(new NetHttpClient(http)));
        var queries = new List<string>();
        var finder = new VideoFinderService(spotify, query =>
        {
            queries.Add(query);
            return Task.FromResult<List<FinderSongDTO>>([new() { Link = "https://www.youtube.com/watch?v=jNQXAC9IVRw", Duration = 19 }]);
        }, uri =>
        {
            Check(uri.Host == "open.spotify.com" && uri.AbsolutePath is "/embed/playlist/public" or "/embed/playlist/denied",
                "Public fallback uses the fixed Spotify embed URL");
            return Task.FromResult(publicEmbed.Replace("PLAYLIST_ID", uri.Segments[^1], StringComparison.Ordinal));
        });
        var tracks = await finder.Find("https://open.spotify.com/track/one?si=fixture");
        Check(tracks is [{ Name: "First song", AuthorName: "First artist|Guest", Duration: 19 }], "Spotify track enriches the matching YouTube result");
        const string url = "https://open.spotify.com/playlist/fixture";
        Check(await finder.GetPlaylistName(url) == "Fixture playlist", "Spotify playlist name read through the SDK");
        tracks = await finder.Find(url);
        Check(tracks is [{ Name: "First song" }, { Name: "Second song" }], "Spotify playlist reads every page using the API's items/item schema");
        Check(queries.SequenceEqual(["First song - First artist", "First song - First artist", "Second song - Second artist"]), "Spotify looks up each track by its name and first artist");
        Check(await finder.Find("https://open.spotify.com/playlist/old") is [{ Name: "First song" }], "Spotify also reads the legacy tracks/track schema");
        const string albumUrl = "https://open.spotify.com/intl-ru/album/fixture/?si=fixture";
        Check(await finder.GetPlaylistName(albumUrl) == "Fixture album", "Album title is read even with a localized sharing URL");
        var imported = new List<string>();
        await foreach (var batch in finder.FindImportBatchesAsync(albumUrl, CancellationToken.None))
            imported.AddRange(batch.Select(track => track.Name));
        Check(imported.SequenceEqual(["First song", "Second song"]), "Album import streams both pages in album order");
        var publicTracks = await finder.Find("https://open.spotify.com/playlist/public");
        Check(publicTracks is [{ Name: "First song", AuthorName: "First artist" }, { Name: "Second song" }],
            "A public playlist with a metadata-only Web API response imports the music tracks from its embed");
        Check(await finder.GetPlaylistName("https://open.spotify.com/playlist/denied") == "Public playlist",
            "A public embed supplies the title when the Web API denies access");
        Check(YtDlpMetadata.GetPlaylistUrl("https://music.youtube.com/watch?v=video&list=PL_example-1") ==
              "https://www.youtube.com/playlist?list=PL_example-1", "A YouTube video link with a playlist parameter resolves the collection");
    }

    private static IConfiguration SpotifyConfiguration(string id, string secret) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["spotify_settings:SPOTIFY_ENABLED"] = "true",
            ["spotify_settings:SPOTIFY_CLIENT_ID"] = id,
            ["spotify_settings:SPOTIFY_CLIENT_SECRET"] = secret,
        }).Build();

    private static async Task TestVoiceLibrariesAsync()
    {
        using var destination = new MemoryStream();
        using (var encoder = new OpusEncodeStream(destination, PcmFormat.Short, VoiceChannels.Stereo, OpusApplication.Audio, leaveOpen: true))
            await encoder.WriteAsync(ConstantPcm(1200, -1200, 2).AsMemory());
        Check(destination.Length > 0 && destination.Length < PcmEffectMixer.FrameBytes * 2, "NetCord and packaged Opus encode stereo PCM");

        string path = Kvasir.Configuration.ApplicationPaths.RuntimeFile("libdave.dll");
        nint dave = NativeLibrary.Load(path);
        try
        {
            Check(NativeLibrary.TryGetExport(dave, "daveSessionCreate", out _), "Packaged DAVE exposes the voice session API");
            nint maxVersion = NativeLibrary.GetExport(dave, "daveMaxSupportedProtocolVersion");
            Check(Marshal.GetDelegateForFunctionPointer<DaveProtocolVersion>(maxVersion)() > 0, "Packaged DAVE reports an encryption protocol version");
        }
        finally { NativeLibrary.Free(dave); }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ushort DaveProtocolVersion();

    private static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class ContractHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public List<(string Method, string Route)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method.Method, request.RequestUri!.PathAndQuery));
            return send(request, cancellationToken);
        }
    }
}
