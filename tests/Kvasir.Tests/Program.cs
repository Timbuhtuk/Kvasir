using Kvasir.Media;
using Application.Interfaces;
using Application.Models;
using Application.Services;
using CS_Discord_Bot.Connectors;
using Entities.Models;
using Infrastructure;
using Infrastructure.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NetCord;
using NetCord.Rest;
using System.Buffers.Binary;
using System.Text.Json;

namespace Kvasir.Tests;

internal static partial class Program
{
    private static int _assertions;
    private static void Check(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }
    private static async Task Until(Func<bool> check)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!check()) await Task.Delay(10, timeout.Token);
    }
    public static async Task Main(string[] args)
    {
        Logging.Logger.EnableConsoleLogging = false; Logging.Logger.EnableFileLogging = false;
        string root = Path.Combine(Path.GetTempPath(), "kvasir-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            if (args is ["--native"])
            {
                await TestFfmpegAsync(root);
                await TestVoiceLibrariesAsync();
                await TestYtDlpProcessAsync(root);
                Console.WriteLine($"PASS: {_assertions} assertions; FFmpeg, yt-dlp, Opus and DAVE.");
                return;
            }
            if (args.FirstOrDefault()?.StartsWith("--live-", StringComparison.Ordinal) == true)
            {
                await RunLiveProbeAsync(args[0], root);
                Console.WriteLine($"PASS: {args[0]} ({_assertions} assertions)");
                return;
            }
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["music_client:music_folder"] = Path.Combine(root, "music"),
                ["music_client:preferences_file"] = Path.Combine(root, "preferences.json"),
                ["music_client:cache_max_age_days"] = "1",
            }).Build();
            ServiceCollection services = new(); services.AddSingleton<IConfiguration>(config);
            services.AddDbContext<DiscordMusicDBContext>(o => o.UseSqlite("Data Source=" + Path.Combine(root, "test.db")));
            services.AddSingleton<IMusicPreferences, MusicPreferences>(); services.AddSingleton<IAudioDownloaderService, FakeDownloader>();
            services.AddSingleton<IAudioNormalizer, FakeNormalizer>(); services.AddSingleton<IMediaCache, MediaCacheService>();
            services.AddSingleton<TrackService>();
            services.AddSingleton<PlaylistImportService>();
            services.AddSingleton<IVideoFinderService, FakeFinder>(); services.AddSingleton<IMusicLibrary, MusicLibraryService>();
            services.AddSingleton<IMusicDiagnostics, MusicDiagnostics>(); services.AddScoped<ISongRepository, SongRepository>(); services.AddScoped<IGuildRepository, GuildRepository>();
            await using ServiceProvider provider = services.BuildServiceProvider();
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DiscordMusicDBContext>();
                await db.Database.MigrateAsync();
                db.Guilds.AddRange(new Guild { Name = "One", DiscordId = 1 }, new Guild { Name = "Two", DiscordId = 2 });
                for (int q = 1; q <= 35; q++) db.Songs.Add(new Song { Id = q, Name = "Song " + q, AuthorName = "Artist", Link = "test:" + q, Duration = 1 });
                await db.SaveChangesAsync();
            }
            TestQueue();
            TestYtDlpMetadata();
            await TestSqliteUpgradeAsync(provider, root);
            await TestDiscordHostAsync(root);
            await TestDiscordRestAsync();
            await TestSpotifyContractAsync();
            await TestQueueDownloadsDuringPlaybackAsync(provider);
            await TestLibraryAsync(provider, config);
            await TestDesktopPlaylistImportAsync(provider, root);
            await TestCacheAsync(provider, root);
            await TestPlaybackAndPanelsAsync(provider, root);
            await TestSoundboardAsync(provider, root);
            if (args.Length > 0)
            {
                await TestFfmpegAsync(root);
                await TestVoiceLibrariesAsync();
            }
            Console.WriteLine($"PASS: {_assertions} assertions; queue, SQLite, Spotify, Discord REST, cache, playback and Components V2.");
        }
        finally
        {
            SqliteClearPools();
            string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (Path.GetDirectoryName(Path.GetFullPath(root)) == parent && Path.GetFileName(root).StartsWith("kvasir-tests-", StringComparison.Ordinal)) Directory.Delete(root, true);
        }
    }
    private static void SqliteClearPools() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    private static void TestYtDlpMetadata()
    {
        var single = YtDlpMetadata.Parse("""
            {"id":"jNQXAC9IVRw","title":"Fixture","uploader":"Author","duration":19,"url":"https://media.invalid/expired-signed-stream"}
            """);
        var songs = YtDlpMetadata.MapSongs(single);
        Check(songs.Count == 1 && songs[0].Name == "Fixture" && songs[0].Duration == 19, "yt-dlp single metadata mapped");
        Check(songs[0].Link == "https://www.youtube.com/watch?v=jNQXAC9IVRw", "Persist canonical page, never expired media URL");
        var playlist = YtDlpMetadata.Parse("""
            {"_type":"playlist","title":"Fixture playlist","entries":[
              null,{"id":"jNQXAC9IVRw","title":"Fixture","channel":"Playlist author","duration":19},
              {"id":"invalid","title":"Invalid"},{"id":"BaW_jenozKc","title":"[Private video]"}]}
            """);
        Check(playlist.Title == "Fixture playlist" && YtDlpMetadata.MapSongs(playlist) is [{ AuthorName: "Playlist author" }],
            "yt-dlp playlist JSON maps valid flat entries and skips unavailable entries");
        playlist.Entries = Enumerable.Repeat(single, 1200).ToArray();
        Check(YtDlpMetadata.MapSongs(playlist).Count == 1001, "Playlist metadata bounded before queue limits");
    }

    private static async Task TestSqliteUpgradeAsync(ServiceProvider provider, string root)
    {
        var options = new DbContextOptionsBuilder<DiscordMusicDBContext>()
            .UseSqlite("Data Source=" + Path.Combine(root, "old-install.db")).Options;
        await using var db = new DiscordMusicDBContext(options);
        await db.GetService<IMigrator>().MigrateAsync("20251114204016_InitialCreate");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO Song (id, Name, Views) VALUES (42, 'Legacy track', 7)");
        await db.Database.MigrateAsync();
        var repo = new SongRepository(db, provider.GetRequiredService<IVideoFinderService>(), provider.GetRequiredService<IMediaCache>());
        var song = await repo.GetByIdAsync(42);
        Check(song is { Name: "Legacy track", IsDownloaded: false, Views: 7 }, "SQLite upgrade preserves existing songs and backfills the new column");
        Check(await repo.IncrementViewsAsync(song!), "EF Core translates and executes the view counter update");
        Check((await repo.GetPopularSongsListAsync()).Single().Views == 8, "SQLite persists the atomic view counter update");
        Check(!(await db.Database.GetPendingMigrationsAsync()).Any(), "SQLite startup leaves no pending migrations");
    }
    private static void TestQueue()
    {
        MusicQueue queue = new(); BotVoiceChannel channel = new(10, 1, "voice"); var first = new MusicActor(1, 10); var second = new MusicActor(2, 10); var settings = new GuildMusicSettings { FairQueue = true, PerUserLimit = 3 };
        Song song = new() { Id = 1, Name = "One" };
        Check(queue.Add(channel, new[] { song, song, song }, first, settings).Success, "Batch add");
        Check(!queue.Add(channel, new[] { song }, first, settings).Success && queue.Snapshot().Count == 3, "Limit is atomic");
        queue.Add(channel, new[] { song, song }, second, settings);
        var entries = queue.Snapshot(); Check(entries.Select(e => e.Id).Distinct().Count() == 5, "Duplicate songs have distinct queue IDs");
        Check(entries.Take(4).Select(e => e.RequestedBy).SequenceEqual(new ulong[] { 1, 2, 1, 2 }), "Fair ordering");
        Check(!queue.Edit(entries[0].Id, "remove", second, false).Success, "Cannot remove another user's order");
        Check(queue.Edit(entries[1].Id, "remove", second, false).Success, "Can remove own order");
        Check(queue.Take(true, 1)?.RequestedBy == 2, "Rotate away from previous requester");
        queue.Undo(1); Check(queue.Snapshot().All(e => e.RequestedBy != 1), "Undo whole last batch");
        Check(!queue.Edit(Guid.NewGuid(), "remove", first, true).Success, "Reject stale queue selection");
        queue.Clear(); Parallel.For(0, 100, _ => queue.Add(channel, new[] { song }, first with { IsManager = true }, settings));
        Check(queue.Snapshot().Count == 100, "Concurrent additions are retained");
    }
    private static async Task TestQueueDownloadsDuringPlaybackAsync(ServiceProvider provider)
    {
        var cache = provider.GetRequiredService<IMediaCache>();
        var downloader = (FakeDownloader)provider.GetRequiredService<IAudioDownloaderService>();
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var prefs = provider.GetRequiredService<IMusicPreferences>();
        await using MusicClientService music = new(new Guild { DiscordId = 1 }, new FakeConnector(), scopes, provider.GetRequiredService<TrackService>(), prefs);
        BotVoiceChannel channel = new(10, 1, "voice");
        MusicActor actor = new(101, 10, IsManager: true);
        TimeSpan timeout = TimeSpan.FromSeconds(5);
        TaskCompletionSource firstDownload = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finishFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource lastDownload = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finishLast = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int firstTransfers = 0;
        downloader.BeforeDownload = (link, token) =>
        {
            if (link == "test:31")
            {
                Interlocked.Increment(ref firstTransfers);
                firstDownload.TrySetResult();
                return finishFirst.Task.WaitAsync(token);
            }
            if (link == "test:32")
            {
                lastDownload.TrySetResult();
                return finishLast.Task.WaitAsync(token);
            }
            return Task.CompletedTask;
        };
        try
        {
            var added = await music.EnqueueAsync(channel, actor, "collection fixture").WaitAsync(timeout);
            await firstDownload.Task.WaitAsync(timeout);
            await Until(() => music.Current != null);
            Check(added.Success && added.MessageArguments is ["3"] && music.Current?.Song.Link == "test:31" &&
                music.Queue.Snapshot().Select(e => e.Song.Link).SequenceEqual(new[] { "fail", "test:32" }) && !lastDownload.Task.IsCompleted,
                "A collection is accepted before audio is ready, and the worker starts with the first track");

            var existing = await music.EnqueueAsync(channel, actor, "test:33").WaitAsync(timeout);
            Check(existing.Success && music.Queue.Snapshot().Last().Song is { Link: "test:33", IsDownloaded: false },
                "A catalog match can be queued while the current audio download is blocked");
            await music.QueueActionAsync(actor, "undo");

            finishFirst.TrySetResult();
            await lastDownload.Task.WaitAsync(timeout);
            await Until(() => music.Status == Entities.Enums.PlaybackStatus.Playing);
            Check(music.Current?.Song.Link == "test:31" && cache.PreparingSongId == 32,
                "A later track downloads while the first plays even after an intervening download failure");
            await music.PauseAsync(actor, true);
            Check(music.Status == Entities.Enums.PlaybackStatus.Paused && cache.PreparingSongId == 32,
                "Pausing playback leaves background preparation active");
            await using (var duplicate = await cache.OpenAsync(new Song { Id = 31 }).WaitAsync(timeout))
                Check(duplicate.CanSeek && firstTransfers == 1, "A cached track opens during another download without fetching it twice");
            await music.StopAsync(actor, true).WaitAsync(timeout);
            await music.DisposeAsync();
            Check(cache.PreparingSongId == 32, "Disconnecting and disposing the guild player leaves the active download running");
            finishLast.TrySetResult();
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DiscordMusicDBContext>();
            await Until(() => db.Songs.Any(s => s.Id == 33 && s.IsDownloaded));
            Check(await db.Songs.CountAsync(s => (s.Id == 32 || s.Id == 33) && s.IsDownloaded) == 2,
                "The independent worker persists active and pending tracks after player disposal");

        }
        finally
        {
            finishFirst.TrySetResult(); finishLast.TrySetResult(); downloader.BeforeDownload = null;
        }
    }
    private static async Task TestLibraryAsync(ServiceProvider provider, IConfiguration config)
    {
        var library = provider.GetRequiredService<IMusicLibrary>(); var prefs = provider.GetRequiredService<IMusicPreferences>();
        MusicActor author = new(101, 10); MusicActor stranger = new(102, 10);
        var playlist = await library.CreateAsync(1, author, "Private", false);
        Check((await library.ListAsync(1, stranger)).Count == 0, "Private list hidden");
        Check(await library.GetAsync(2, author, playlist.Id) == null, "Guild boundary enforced");
        Check(!(await library.EditAsync(1, stranger, playlist.Id, "rename", "hijack")).Success, "Ownership enforced");
        for (int q = 1; q <= 27; q++) await library.EditAsync(1, author, playlist.Id, "add", songId: q);
        Check((await library.GetAsync(1, author, playlist.Id))!.Songs.Count == 27, "Playlists exceed old 23-track cap");
        await library.EditAsync(1, author, playlist.Id, "up", songId: 27);
        Check((await library.GetAsync(1, author, playlist.Id))!.Songs.ElementAt(25).Id == 27, "Track ordering persisted");
        await library.EditAsync(1, author, playlist.Id, "share");
        Check((await library.ListAsync(1, stranger)).Count == 1, "Shared list visible");
        Check(!(await library.EditAsync(1, stranger, playlist.Id, "delete")).Success, "Public is not world writable");
        await prefs.SaveSettingsAsync(1, new GuildMusicSettings { DjRoleId = 999, FairQueue = true, Volume = 25 });
        Check(new MusicPreferences(config).GetSettings(1).DjRoleId == 999, "Server settings survive reload");
        var copy = prefs.GetSettings(1); copy.Volume = 100; Check(prefs.GetSettings(1).Volume == 25, "Settings snapshots cannot mutate stored values");
        var imported = await library.CreateAsync(1, author, "Imported", false); ImportProgress? progress = null;
        await library.ImportAsync(1, author, imported.Id, "test", p => progress = p, CancellationToken.None);
        Check(progress is { Finished: true, Completed: 3 } && progress.Failures.Count == 1, "Partial import reports failures");
        Check(progress?.Detail != null && progress.Failures.All(f => !f.Contains("Exception")), "Local import detail retained separately from Discord failure labels");
        Check((await library.GetAsync(1, author, imported.Id))!.Songs.Select(song => song.IsDownloaded).SequenceEqual(new[] { true, false, true }),
            "Import keeps the source order and retains metadata even if audio fails");
    }
    private static async Task TestCacheAsync(ServiceProvider provider, string root)
    {
        var cache = provider.GetRequiredService<IMediaCache>(); Song song = new() { Id = 1 };
        string pcm = Path.Combine(root, "music", "1.norm.pcm");
        await using (Stream lease = await cache.OpenAsync(song))
        {
            Check(lease.CanSeek && lease.Length == 192000, "Cache supplies seekable PCM");
            File.SetLastWriteTimeUtc(pcm, DateTime.UtcNow.AddDays(-10)); await cache.TrimAsync();
            Check(File.Exists(pcm), "Cleanup preserves active playback file");
        }
        await cache.TrimAsync(); Check(!File.Exists(pcm), "Cleanup evicts old unleased files");
        await using (Stream restored = await cache.OpenAsync(song)) Check(restored.Length == 192000, "Evicted song redownloads");
        await File.WriteAllBytesAsync(pcm, new byte[5]);
        var report = await cache.MaintainAsync(true); Check(report.Repaired > 0 && new FileInfo(pcm).Length == 192000, "Truncated PCM repairs");
        Check(!((MediaCacheService)cache).IsManaged(Path.Combine(root, "outside.pcm")), "Cleanup path confined to cache root");
    }
    private static async Task TestPlaybackAndPanelsAsync(ServiceProvider provider, string root)
    {
        var prefs = provider.GetRequiredService<IMusicPreferences>(); await prefs.SaveSettingsAsync(1, new GuildMusicSettings { VoteSkip = true, Volume = 50 });
        FakeConnector connector = new(); FakePlaybackCache audio = new();
        await using TrackService tracks = new(provider.GetRequiredService<IServiceScopeFactory>(), audio);
        await using MusicClientService music = new(new Guild { DiscordId = 1, Name = "One" }, connector, provider.GetRequiredService<IServiceScopeFactory>(), tracks, prefs);
        MusicActor first = new(101, 10); MusicActor second = new(102, 10); MusicActor manager = first with { IsManager = true };
        music.music_view.Session(first.UserId).PlaylistId = 1;
        Check(music.music_view.Session(second.UserId).PlaylistId == null, "Navigation isolated per user");
        Song song = new() { Id = 1, Name = "Track @everyone", Duration = 10, AuthorName = "Artist" };
        await music.EnqueueAsync(new(10, 1, "voice"), first, songs: new[] { song, song.withClone(2) });
        await Until(() => music.Status == Entities.Enums.PlaybackStatus.Playing);
        Check(!(await music.PauseAsync(new MusicActor(103, 99))).Success, "Other voice channel cannot control player");
        Check((await music.PauseAsync(manager, true)).Success, "Pause");
        Check((await music.PauseAsync(manager, true)).Success && music.Status == Entities.Enums.PlaybackStatus.Paused, "Pause command is idempotent");
        Check((await music.SeekAsync(manager, 5)).Success, "Seek accepted");
        await Until(() => music.PositionSeconds >= 5);
        await music.PauseAsync(manager, false);
        Check((await music.SkipAsync(first, new ulong[] { 101, 102, 103 })).Message.Contains("1/2"), "Strict majority needed");
        Check((await music.SkipAsync(first, new ulong[] { 101, 102, 103 })).Message.Contains("1/2"), "Duplicate vote not counted");
        Check((await music.SkipAsync(second, new ulong[] { 101, 102, 103 })).Message.Contains("пропущен"), "Majority skips");
        await Until(() => music.Current?.Song.Id == 2);
        await music.SetRepeatAsync(manager, "track");
        Check((await music.StopAsync(manager, true)).Success, "Stop breaks repeat and disconnects");
        Check(music.Current == null && music.Queue.Snapshot().Count == 0 && music.VoiceChannelId == null, "Stop resets state");
        Check((await music.ConfigureAsync(manager with { VoiceChannelId = null }, s => s.Volume = 50)).Success, "Administrator can configure volume before voice connection");
        Check(!(await music.ConfigureAsync(first with { VoiceChannelId = null }, s => s.Volume = 50, false)).Success, "Discord listener still needs voice for DJ volume command");
        await music.EnqueueAsync(new(10, 1, "voice"), manager, songs: new[] { song });
        await Until(() => music.Status == Entities.Enums.PlaybackStatus.Playing);
        connector.LastVoice!.FailDisposeOnce = true;
        bool disconnectFailed = false;
        try { await music.StopAsync(manager, true); } catch (IOException) { disconnectFailed = true; }
        Check(disconnectFailed, "Voice disconnect failure is reported");
        Check((await music.EnqueueAsync(new(10, 1, "voice"), manager, songs: new[] { song })).Success, "Failed disconnect must not leave player permanently stopping");
        await music.StopAsync(manager, true);
        byte[] samples = new byte[4]; BinaryPrimitives.WriteInt16LittleEndian(samples, short.MinValue); BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(2), short.MaxValue);
        MusicClientService.ApplyVolume(samples, 4, 50); Check(BinaryPrimitives.ReadInt16LittleEndian(samples) == -16384 && BinaryPrimitives.ReadInt16LittleEndian(samples.AsSpan(2)) == 16383, "PCM volume preserves signed samples");
        audio.Failure = "private-test-source: download unavailable";
        await music.EnqueueAsync(new(10, 1, "voice"), manager, songs: new[] { song });
        await Until(() => music.LastErrorDetail != null && music.Status == Entities.Enums.PlaybackStatus.None);
        Check(music.LastErrorDetail!.Contains("private-test-source") && music.LastError?.Contains("private-test-source") == false, "Technical playback cause is separate from friendly message");
        Check(!JsonSerializer.Serialize(music.music_view.PublicPanel()).Contains("private-test-source"), "Technical playback detail is not published to Discord");
        audio.Failure = null;
        List<string> screens = new() { "queue", "library", "playlist", "history", "player", "server", "diagnostics", "import-progress", "confirm-clear", "confirm-delete" };
        Validate(music.music_view.PublicPanel());
        foreach (string screen in screens)
        {
            music.music_view.Session(first.UserId).Screen = screen;
            BotMessageContent panel = await music.music_view.PersonalPanelAsync(manager, "Проверка"); Validate(panel);
            if (screen == "playlist") Check(panel.Components != null, "Playlist panel builds");
        }
        string json = JsonSerializer.Serialize(new MessageProperties { Flags = MessageFlags.IsComponentsV2, Components = NetCordBotConnector.ConvertComponents((await music.music_view.PersonalPanelAsync(first)).Components) });
        Check(json.Contains("32768") && json.Contains("\"type\":17") && !json.Contains("\"embeds\""), "Native V2 payload serialization");
        using JsonDocument document = JsonDocument.Parse(json);
        File.WriteAllText(Path.Combine(root, "panel-payload.json"), json);
    }
    private static Song withClone(this Song song, int id) => new() { Id = id, Name = song.Name, Duration = song.Duration, AuthorName = song.AuthorName };
    private static void Validate(BotMessageContent content)
    {
        Check(content.ComponentsV2 && content.Embed == null && content.Content == "", "V2 has no legacy content/embed");
        List<string> ids = new(); int count = 0;
        void Visit(BotComponent component)
        {
            count++;
            switch (component)
            {
                case BotContainer container: Check(container.Components.Count <= 10, "Container child limit"); foreach (var child in container.Components) Visit(child); break;
                case BotComponentRow row: Check(row.Components.Count is >= 1 and <= 5, "Button row limit"); foreach (var child in row.Components) Visit(child); break;
                case BotSelect select: count++; ids.Add(select.Id); Check(select.Options.Count is >= 1 and <= 25, "Select option limit"); Check(select.Options.All(o => o.Label.Length <= 100 && o.Value.Length <= 100), "Option text limits"); break;
                case BotButton button: ids.Add(button.Id); break;
                case BotText text: Check(text.Text.Length <= 4000, "Text size limit"); break;
            }
        }
        foreach (var component in content.Components!) Visit(component);
        Check(count <= 40, "Total component limit"); Check(ids.Count == ids.Distinct().Count(), "No duplicate custom IDs");
    }
    private static async Task TestFfmpegAsync(string root)
    {
        string input = Path.Combine(root, "tone.pcm"), output = Path.Combine(root, "tone-normalized.pcm");
        byte[] samples = new byte[192000];
        for (int q = 0; q < samples.Length / 4; q++)
        {
            short sample = (short)(Math.Sin(q * 2 * Math.PI * 440 / 48000) * 1000);
            BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(q * 4), sample);
            BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(q * 4 + 2), sample);
        }
        await File.WriteAllBytesAsync(input, samples);
        await new AudioNormalizer().NormalizeAsync(input, output, CancellationToken.None);
        Check(new FileInfo(output).Length == samples.Length, "Real FFmpeg preserves sample format and duration");
        byte[] normalized = await File.ReadAllBytesAsync(output);
        Check(normalized.Any(b => b != 0) && !normalized.SequenceEqual(samples), "Real loudnorm changes gain without silencing the signal");
        await TestNativeSoundEffectsAsync(root);
    }
}

internal sealed class FakeFinder : IVideoFinderService
{
    public const string DesktopCollection = "https://open.spotify.com/album/fixture";
    public Func<CancellationToken, Task>? BeforeNextFixtureBatch { get; set; }

    public Task<List<FinderSongDTO>?> Find(string query, bool return_song = false) => Task.FromResult<List<FinderSongDTO>?>(new() { new() { Name = "Good A", Link = "test:31", Duration = 1 }, new() { Name = "Failure", Link = "fail", Duration = 1 }, new() { Name = "Good B", Link = "test:32", Duration = 1 } });
    public Func<string, Task<string?>>? TitleLookup { get; set; }
    public Task<string?> GetPlaylistName(string url) => TitleLookup?.Invoke(url) ?? Task.FromResult<string?>("Imported");
    public async IAsyncEnumerable<IReadOnlyList<FinderSongDTO>> FindImportBatchesAsync(string query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var tracks = (await Find(query))!;
        if (query != DesktopCollection) { yield return tracks; yield break; }
        tracks[0].Link = "test:41";
        tracks[2].Link = "test:42";
        yield return tracks.GetRange(0, 1);
        if (BeforeNextFixtureBatch is { } before) await before(cancellationToken);
        yield return tracks.GetRange(1, 2);
    }
}
internal sealed class FakeDownloader : IAudioDownloaderService
{
    public Func<string, CancellationToken, Task>? BeforeDownload { get; set; }
    public async Task<bool> DownloadAsync(string videoLink, string outputFilePath, CancellationToken cancellationToken = default)
    {
        if (BeforeDownload is { } before) await before(videoLink, cancellationToken);
        if (videoLink == "fail") return false;
        await File.WriteAllBytesAsync(outputFilePath, new byte[192000], cancellationToken);
        return true;
    }
    public Task<bool> DownloadAsync(Song song, CancellationToken cancellationToken = default) => DownloadAsync(song.Link!, song.FilePath!, cancellationToken);
    public async Task<List<bool>?> DownloadSongsAsync(List<Song> songs, int maxConcurrency = 3) { List<bool> results = new(); foreach (var song in songs) results.Add(await DownloadAsync(song)); return results; }
}
internal sealed class FakeNormalizer : IAudioNormalizer
{
    public Task NormalizeAsync(string source, string destination, CancellationToken cancellationToken) { File.Copy(source, destination, true); return Task.CompletedTask; }
}
internal sealed class FakePlaybackCache : IMediaCache
{
    public string? Failure { get; set; }
    public short Sample { get; set; }
    public Task<Stream> OpenAsync(Song song, CancellationToken cancellationToken = default) => Failure == null ?
        Task.FromResult<Stream>(new MemoryStream(Signal())) : Task.FromException<Stream>(new IOException(Failure));
    public async Task<Stream?> TryOpenReadyAsync(Song song, CancellationToken token) => await OpenAsync(song, token);
    private byte[] Signal()
    {
        byte[] bytes = new byte[1920000];
        if (Sample != 0) for (int q = 0; q < bytes.Length; q += 2) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(q), Sample);
        return bytes;
    }
    public Task<CacheReport> InspectAsync() => Task.FromResult(new CacheReport(0, 100, 0, 0));
    public Task<CacheReport> MaintainAsync(bool repair, CancellationToken cancellationToken = default) => InspectAsync();
    public Task TrimAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
internal sealed class FakeConnector : IBotConnector
{
    public FakeVoice? LastVoice { get; private set; }
    public IReadOnlyCollection<BotGuild> Guilds { get; init; } = [];
    public ulong CurrentUserId => 999;
    public Task<IBotVoiceConnection> ConnectVoiceAsync(BotVoiceChannel channel, CancellationToken cancellationToken = default) => Task.FromResult<IBotVoiceConnection>(LastVoice = new FakeVoice());
    public Task<bool> IsTextChannelAsync(ulong guildId, ulong channelId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<BotMessage> SendMessageAsync(ulong channelId, BotMessageContent message, CancellationToken cancellationToken = default) => Task.FromResult(new BotMessage(1, channelId, 1, 999, ""));
    public Task UpdateMessageAsync(ulong channelId, ulong messageId, BotMessageContent message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteMessageAsync(ulong channelId, ulong messageId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
internal sealed class FakeVoice : IBotVoiceConnection
{
    public bool FailDisposeOnce { get; set; }
    public System.Collections.Concurrent.ConcurrentQueue<byte[]> Frames { get; } = new();
    public Stream CreatePcmStream() => new PacedStream(Frames);
    public ValueTask DisposeAsync()
    {
        if (FailDisposeOnce) { FailDisposeOnce = false; throw new IOException("Simulated voice disconnect failure"); }
        return ValueTask.CompletedTask;
    }
    private sealed class PacedStream(System.Collections.Concurrent.ConcurrentQueue<byte[]> frames) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { await Task.Delay(5, cancellationToken); frames.Enqueue(buffer.ToArray()); while (frames.Count > 1000) frames.TryDequeue(out _); await base.WriteAsync(buffer, cancellationToken); }
    }
}
