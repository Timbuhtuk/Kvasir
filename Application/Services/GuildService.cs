using Application.Interfaces;
using CS_Discord_Bot.music_parts;
using Discord.Commands;
using Discord.WebSocket;
using Entities.Models;
using Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Application.Services;

/// <summary>
/// Service for managing collection of MusicClient instances
/// </summary>
public class GuildService : IAsyncDisposable
{
    private readonly Dictionary<ulong, (MusicClient MusicClient, IServiceScope Scope)> _musicClients;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly DiscordSocketClient _client;
    private readonly IConfiguration _config;
    private readonly VideoFinderService _videoFinder;
    private readonly IAudioDownloaderService _audioDownloader;

    public GuildService(
        IServiceScopeFactory serviceScopeFactory,
        DiscordSocketClient client,
        IConfiguration config,
        VideoFinderService videoFinder,
        IAudioDownloaderService audioDownloader)
    {
        _musicClients = new Dictionary<ulong, (MusicClient, IServiceScope)>();
        _serviceScopeFactory = serviceScopeFactory;
        _client = client;
        _config = config;
        _videoFinder = videoFinder;
        _audioDownloader = audioDownloader;
    }

    /// <summary>
    /// Gets or creates a MusicClient for the specified guild
    /// </summary>
    public async Task<MusicClient> GetOrCreateAsync(SocketGuild guild)
    {
        if (_musicClients.TryGetValue(guild.Id, out var clientData))
        {
            return clientData.MusicClient;
        }

        var (musicClient, scope) = await CreateMusicClientAsync(guild);
        _musicClients[guild.Id] = (musicClient, scope);
        return musicClient;
    }

    /// <summary>
    /// Gets MusicClient for the specified guild ID, returns null if not found
    /// </summary>
    public MusicClient? GetMusicClient(ulong guildId)
    {
        if (_musicClients.TryGetValue(guildId, out var clientData))
        {
            return clientData.MusicClient;
        }
        return null;
    }

    /// <summary>
    /// Initializes MusicClients for all guilds the bot is connected to
    /// </summary>
    public async Task FillAsync()
    {
        _musicClients.Clear();
        foreach (SocketGuild guild in _client.Guilds)
        {
            await GetOrCreateAsync(guild);
        }
        await Logger.AddLog($"MusicClient created for {_client.Guilds.Count} guilds");
    }

    // Music command methods
    public async Task AnchorAsync(SocketCommandContext context)
    {
        // Контекст уже установлен в CommandHandler, но убедимся
        LogContext.SetGuild(context.Guild.Id, context.Guild.Name);
        MusicClient musicClient = await GetOrCreateAsync(context.Guild);
        await musicClient.SetAnchorAsync(context);
    }

    public async Task ClearAsync(SocketCommandContext context)
    {
        LogContext.SetGuild(context.Guild.Id, context.Guild.Name);
        MusicClient musicClient = await GetOrCreateAsync(context.Guild);
        await musicClient.ClearAsync(context);
    }

    public async Task LeaveAsync(SocketCommandContext context)
    {
        LogContext.SetGuild(context.Guild.Id, context.Guild.Name);
        MusicClient musicClient = await GetOrCreateAsync(context.Guild);
        await musicClient.LeaveAsync(context);
    }

    public async Task PauseAsync(SocketCommandContext context)
    {
        LogContext.SetGuild(context.Guild.Id, context.Guild.Name);
        MusicClient musicClient = await GetOrCreateAsync(context.Guild);
        await musicClient.TogglePauseAsync(context);
    }

    public async Task PlayAsync(SocketCommandContext context, string query)
    {
        LogContext.SetGuild(context.Guild.Id, context.Guild.Name);
        MusicClient musicClient = await GetOrCreateAsync(context.Guild);
        await musicClient.PlayAsync(context, query);
    }

    public async Task PlayAsync(SocketCommandContext context)
    {
        LogContext.SetGuild(context.Guild.Id, context.Guild.Name);
        MusicClient musicClient = await GetOrCreateAsync(context.Guild);
        await musicClient.PlayAsync(context);
    }

    public async Task ResumeAsync(SocketCommandContext context)
    {
        LogContext.SetGuild(context.Guild.Id, context.Guild.Name);
        MusicClient musicClient = await GetOrCreateAsync(context.Guild);
        await musicClient.TogglePauseAsync(context);
    }

    public async Task SkipAsync(SocketCommandContext context)
    {
        LogContext.SetGuild(context.Guild.Id, context.Guild.Name);
        MusicClient musicClient = await GetOrCreateAsync(context.Guild);
        await musicClient.SkipAsync(context);
    }

    private async Task<(MusicClient MusicClient, IServiceScope Scope)> CreateMusicClientAsync(SocketGuild socketGuild)
    {
        // Создаем scope для этой гильдии - он будет жить пока живет MusicClient
        IServiceScope scope = _serviceScopeFactory.CreateScope();
        IGuildRepository guildRepository = scope.ServiceProvider.GetRequiredService<IGuildRepository>();

        Guild? guild = await guildRepository.GetByDiscordIdAsync(socketGuild.Id);

        if (guild == null)
        {
            guild = new Guild
            {
                Name = socketGuild.Name,
                DiscordId = socketGuild.Id,
            };
            guild = await guildRepository.AddAsync(guild);
        }

        // Создаем MusicClient через ActivatorUtilities с параметрами
        // MusicView будет создан внутри MusicClient через scope
        // Репозитории будут автоматически инжектированы из scope
        MusicClient musicClient = Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<MusicClient>(
            scope.ServiceProvider,
            guild,
            _serviceScopeFactory,
            _client,
            _config,
            _videoFinder,
            _audioDownloader,
            scope.ServiceProvider);

        return (musicClient, scope);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (musicClient, scope) in _musicClients.Values)
        {
            await musicClient.DisposeAsync();
            scope.Dispose();
        }
        _musicClients.Clear();
    }
}

