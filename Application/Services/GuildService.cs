using Application.Interfaces;
using Application.Models;
using Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Application.Services;

/// <summary>
/// Owns one music client per guild without depending on a Discord library.
/// </summary>
public class GuildService(
    IServiceScopeFactory serviceScopeFactory,
    IBotConnector connector,
    IConfiguration configuration) : IAsyncDisposable
{
    private readonly Dictionary<ulong, (MusicClientService MusicClient, IServiceScope Scope)> _musicClients = new();
    private readonly SemaphoreSlim _createSemaphore = new(1, 1);

    public async Task<MusicClientService> GetOrCreateAsync(BotGuild botGuild)
    {
        if (_musicClients.TryGetValue(botGuild.Id, out var clientData))
            return clientData.MusicClient;

        await _createSemaphore.WaitAsync();
        try
        {
            if (_musicClients.TryGetValue(botGuild.Id, out clientData))
                return clientData.MusicClient;

            var result = await CreateMusicClientAsync(botGuild);
            _musicClients[botGuild.Id] = result;
            return result.MusicClient;
        }
        finally
        {
            _createSemaphore.Release();
        }
    }

    public MusicClientService? GetMusicClient(ulong guildId)
    {
        if (_musicClients.TryGetValue(guildId, out var clientData))
            return clientData.MusicClient;
        return null;
    }

    public async Task FillAsync()
    {
        foreach (BotGuild guild in connector.Guilds)
            await GetOrCreateAsync(guild);

        await Logger.AddLog($"MusicClientService created for {connector.Guilds.Count} guilds");
    }

    public async Task AnchorAsync(BotGuild botGuild, ulong channelId, string channelName)
    {
        LogContext.SetGuild(botGuild.Id, botGuild.Name);
        MusicClientService musicClient = await GetOrCreateAsync(botGuild);

        using IServiceScope scope = serviceScopeFactory.CreateScope();
        IGuildRepository guildRepository = scope.ServiceProvider.GetRequiredService<IGuildRepository>();
        Entities.Models.Guild? guild = await guildRepository.GetByDiscordIdAsync(botGuild.Id);
        if (guild == null)
            return;

        guild.Anchor = channelId;
        await guildRepository.UpdateAsync(guild);
        await Logger.AddLog($"Anchor for {botGuild.Name} is now {channelName}");

        if (musicClient.music_view != null)
            await musicClient.music_view.RerenderMusicViewAsync(channelId);
    }

    private async Task<(MusicClientService MusicClient, IServiceScope Scope)> CreateMusicClientAsync(BotGuild botGuild)
    {
        IServiceScope scope = serviceScopeFactory.CreateScope();
        try
        {
            IGuildRepository guildRepository = scope.ServiceProvider.GetRequiredService<IGuildRepository>();
            Entities.Models.Guild? guild = await guildRepository.GetByDiscordIdAsync(botGuild.Id);

            if (guild == null)
            {
                guild = new Entities.Models.Guild
                {
                    Name = botGuild.Name,
                    DiscordId = botGuild.Id,
                };
                guild = await guildRepository.AddAsync(guild);
            }

            MusicClientService musicClient = ActivatorUtilities.CreateInstance<MusicClientService>(
                scope.ServiceProvider,
                guild,
                connector,
                configuration,
                scope.ServiceProvider,
                serviceScopeFactory);

            return (musicClient, scope);
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach ((MusicClientService musicClient, IServiceScope scope) in _musicClients.Values)
        {
            await musicClient.DisposeAsync();
            scope.Dispose();
        }

        _musicClients.Clear();
        _createSemaphore.Dispose();
    }
}
