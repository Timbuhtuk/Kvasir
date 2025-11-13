using Application.Interfaces;
using CS_Discord_Bot.Handlers;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Entities.Enums;
using Entities.Models;
using Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using EventHandler = CS_Discord_Bot.Handlers.EventHandler;

namespace Application.Services;

/// <summary>
/// Hosted service that manages Discord bot lifecycle
/// </summary>
[LogCategory(LogCategory.Discord)]
public class DiscordBotService : IHostedService
{
    private readonly DiscordSocketClient _client;
    private readonly CommandService _commands;
    private readonly CommandHandler _commandHandler;
    private readonly ComponentHandler _componentHandler;
    private readonly EventHandler _eventHandler;
    private readonly IConfiguration _configuration;
    private readonly GuildService _guildService;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<DiscordBotService>? _logger;

    public DiscordBotService(
        DiscordSocketClient client,
        CommandService commands,
        CommandHandler commandHandler,
        ComponentHandler componentHandler,
        EventHandler eventHandler,
        IConfiguration configuration,
        GuildService guildService,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<DiscordBotService>? logger = null)
    {
        _client = client;
        _commands = commands;
        _commandHandler = commandHandler;
        _componentHandler = componentHandler;
        _eventHandler = eventHandler;
        _configuration = configuration;
        _guildService = guildService;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _commandHandler.RegisterCommandsAsync();
        await _componentHandler.RegisterComponentsAsync();
        await _eventHandler.RegisterEventsAsync();

        await Logger.AddLog($"use_database: {_configuration["use_database"]!}");
        await Logger.AddLog($"connection string: {_configuration["connection_string"]!}");

        await _client.LoginAsync(TokenType.Bot, _configuration["tokens:0"]);
        await _client.StartAsync();

        _client.Ready += OnReadyAsync;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _client.StopAsync();
        _client.Dispose();
    }

    private async Task OnReadyAsync()
    {
        await UpdateDBGuildsAsync();
        await _guildService.FillAsync();

        await Logger.AddLog($"logged as {_client.CurrentUser.Username}", Microsoft.Extensions.Logging.LogLevel.Warning);
        _ = CleanupAnchorChannelsAsync();
        _ = ResolveMissingfilesInDBAsync();
    }

    private async Task UpdateDBGuildsAsync()
    {
        await Logger.AddLog("UpdateDBGuilds called");

        using var scope = _serviceScopeFactory.CreateScope();
        IGuildRepository guildRepository = scope.ServiceProvider.GetRequiredService<IGuildRepository>();

        foreach (SocketGuild guild in _client.Guilds)
        {
            Guild? modelsGuild = await guildRepository.GetByDiscordIdAsync(guild.Id);

            if (modelsGuild == null)
            {
                modelsGuild = new Guild
                {
                    Name = guild.Name,
                    DiscordId = guild.Id,
                };
                await guildRepository.AddAsync(modelsGuild);
            }
        }
    }

    /// <summary>
    /// Cleans up all bot messages from Anchor channels on startup
    /// </summary>
    private async Task CleanupAnchorChannelsAsync()
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            IGuildRepository guildRepository = scope.ServiceProvider.GetRequiredService<IGuildRepository>();

            IQueryable<Guild> allGuilds = await guildRepository.GetAllAsync();
            List<Guild> guildsWithAnchor = allGuilds.Where(g => g.Anchor != null).ToList();

            await Logger.AddLog($"Found {guildsWithAnchor.Count} guilds with Anchor channels");

            ulong botUserId = _client.CurrentUser.Id;
            int totalDeleted = 0;

            foreach (Guild guild in guildsWithAnchor)
            {
                try
                {
                    SocketGuild? socketGuild = _client.GetGuild(guild.DiscordId);
                    if (socketGuild == null)
                    {
                        await Logger.AddLog($"Guild {guild.Name} (ID: {guild.DiscordId}) not found in Discord", Microsoft.Extensions.Logging.LogLevel.Warning);
                        continue;
                    }

                    if (await _client.GetChannelAsync(guild.Anchor.Value) is not ITextChannel channel)
                    {
                        await Logger.AddLog($"Anchor channel {guild.Anchor.Value} not found for guild {guild.Name}", Microsoft.Extensions.Logging.LogLevel.Warning);
                        continue;
                    }

                    LogContext.SetGuild(guild.DiscordId, guild.Name);

                    int deletedInChannel = 0;
                    const int batchSize = 100;

                    // Получаем сообщения батчами и удаляем сообщения от бота
                    IAsyncEnumerable<IReadOnlyCollection<IMessage>> messageBatches = channel.GetMessagesAsync(batchSize);
                    await foreach (IReadOnlyCollection<IMessage> messages in messageBatches)
                    {
                        List<IMessage> botMessages = messages.Where(m => m.Author.Id == botUserId).ToList();

                        foreach (IMessage message in botMessages)
                        {
                            try
                            {
                                await message.DeleteAsync();
                                deletedInChannel++;
                                totalDeleted++;

                                // Небольшая задержка, чтобы не превысить rate limit
                                await Task.Delay(100);
                            }
                            catch (Exception ex)
                            {
                                await Logger.AddLog($"Failed to delete message {message.Id} in channel {channel.Name}: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error);
                            }
                        }

                        // Если получили меньше сообщений, чем batchSize, значит достигли конца
                        if (messages.Count < batchSize)
                            break;
                    }

                    if (deletedInChannel > 0)
                    {
                        await Logger.AddLog($"Deleted {deletedInChannel} bot messages from Anchor channel {channel.Name} in guild {guild.Name}");
                    }
                }
                catch (Exception ex)
                {
                    await Logger.AddLog($"Error cleaning up Anchor channel for guild {guild.Name}: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, exception: ex);
                }
                finally
                {
                    LogContext.Clear();
                }
            }

            await Logger.AddLog($"Anchor channels cleanup completed. Total messages deleted: {totalDeleted}");
        }
        catch (Exception ex)
        {
            await Logger.AddLog($"Error during Anchor channels cleanup: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, exception: ex);
        }
    }

    /// <summary>
    /// Resolves missing files in database by downloading missing songs and removing orphaned files
    /// </summary>
    private async Task<int> ResolveMissingfilesInDBAsync()
    {
        using var scope = _serviceScopeFactory.CreateScope();
        ISongRepository songRepository = scope.ServiceProvider.GetRequiredService<ISongRepository>();
        int resolved = 0;

        IQueryable<Song> allSongs = await songRepository.GetAllAsync();
        List<Song> songs = allSongs.ToList();
        foreach (Song song in songs)
        {
            // Проверяем наличие PCM файла
            if (string.IsNullOrEmpty(song.FilePath) || !File.Exists(song.FilePath))
            {
                using (var audioScope = _serviceScopeFactory.CreateScope())
                {
                    IAudioDownloaderService audioDownloader = audioScope.ServiceProvider.GetRequiredService<IAudioDownloaderService>();
                    IConfiguration configuration = audioScope.ServiceProvider.GetRequiredService<IConfiguration>();
                    string musicFolderPath = Path.Combine(Environment.CurrentDirectory, configuration["music_folder"] ?? "music");

                    // Сохраняем оригинальный путь для сравнения
                    string? originalFilePath = song.FilePath;

                    Song? downloaded_song = await audioDownloader.Download(song, songRepository, musicFolderPath);
                    if (downloaded_song == null || string.IsNullOrEmpty(downloaded_song.FilePath) || !File.Exists(downloaded_song.FilePath))
                    {
                        await songRepository.RemoveAsync(song);
                        await Logger.AddLog($"{song.Name} - removed from DB (failed to download or file missing)");
                    }
                    else
                    {
                        // Проверяем, изменился ли путь к файлу после скачивания
                        if (downloaded_song.FilePath != originalFilePath)
                        {
                            // Обновляем путь в БД, если он изменился
                            song.FilePath = downloaded_song.FilePath;
                            await songRepository.UpdateAsync(song);
                            await Logger.AddLog($"{song.Name} - file downloaded successfully, path updated in DB: {downloaded_song.FilePath}");
                        }
                        else
                        {
                            await Logger.AddLog($"{song.Name} - file downloaded successfully: {downloaded_song.FilePath}");
                        }
                    }

                    resolved++;
                }
            }
        }
        return resolved;
    }
}

