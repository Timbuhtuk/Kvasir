using Application.Interfaces;
using Entities.Models;
using Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Application.Services;

/// <summary>
/// Base class for bot services containing non-Discord-specific functionality
/// </summary>
public abstract class BotServiceBase
{
    protected readonly IServiceScopeFactory _serviceScopeFactory;
    protected readonly IConfiguration _configuration;

    protected BotServiceBase(
        IServiceScopeFactory serviceScopeFactory,
        IConfiguration configuration)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _configuration = configuration;
    }

    /// <summary>
    /// Updates guilds in database based on provided guild data
    /// </summary>
    /// <param name="guilds">Collection of guild data (id, name)</param>
    protected async Task UpdateDBGuildsAsync(IEnumerable<(ulong Id, string Name)> guilds)
    {
        await Logger.AddLog("UpdateDBGuilds called");

        using IServiceScope scope = _serviceScopeFactory.CreateScope();
        IGuildRepository guildRepository = scope.ServiceProvider.GetRequiredService<IGuildRepository>();

        foreach ((ulong id, string name) in guilds)
        {
            Guild? modelsGuild = await guildRepository.GetByDiscordIdAsync(id);

            if (modelsGuild == null)
            {
                modelsGuild = new Guild
                {
                    Name = name,
                    DiscordId = id,
                };
                await guildRepository.AddAsync(modelsGuild);
            }
        }
    }

    /// <summary>
    /// Gets all guilds with Anchor channels from database
    /// </summary>
    protected async Task<List<Guild>> GetGuildsWithAnchorAsync()
    {
        using IServiceScope scope = _serviceScopeFactory.CreateScope();
        IGuildRepository guildRepository = scope.ServiceProvider.GetRequiredService<IGuildRepository>();

        IQueryable<Guild> allGuilds = await guildRepository.GetAllAsync();
        return allGuilds.Where(g => g.Anchor != null).ToList();
    }

    /// <summary>
    /// Resolves missing files in database by downloading missing songs and removing orphaned files
    /// Uses parallel downloading for better performance with event-based completion handling
    /// </summary>
    protected async Task<int> ResolveMissingfilesInDBAsync()
    {
        using IServiceScope scope = _serviceScopeFactory.CreateScope();
        ISongRepository songRepository = scope.ServiceProvider.GetRequiredService<ISongRepository>();
        IAudioDownloaderService audioDownloader = scope.ServiceProvider.GetRequiredService<IAudioDownloaderService>();
        IConfiguration configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        IQueryable<Song> allSongs = await songRepository.GetAllAsync();
        List<Song> allSongsList = allSongs.ToList();

        // Collect songs that need downloading
        List<Song> songsToDownload = new();
        foreach (Song song in allSongsList)
        {
            if (string.IsNullOrEmpty(song.FilePath) || !File.Exists(song.FilePath))
            {
                // Проверяем, что у песни есть ссылка для скачивания
                if (!string.IsNullOrEmpty(song.Link))
                {
                    songsToDownload.Add(song);
                }
                else
                {
                    // Если нет ссылки, удаляем запись из БД
                    await songRepository.RemoveAsync(song);
                    await Logger.AddLog($"{song.Name} - removed from DB (no link available for download)");
                }
            }
        }

        if (songsToDownload.Count == 0)
        {
            await Logger.AddLog("No missing files found in database");
            return 0;
        }

        await Logger.AddLog($"Found {songsToDownload.Count} songs with missing files. Starting parallel download...");

        // Получаем путь к папке с музыкой
        string? musicFolder = configuration["music_client:music_folder"];
        if (string.IsNullOrWhiteSpace(musicFolder))
        {
            await Logger.AddLog("music_client:music_folder configuration is null or empty", Microsoft.Extensions.Logging.LogLevel.Error);
            return 0;
        }

        if (!Directory.Exists(musicFolder))
            Directory.CreateDirectory(musicFolder);

        // Создаем Dictionary<int, Song> и задаем FilePath для каждого трека
        Dictionary<int, Song> songsDict = new();
        foreach (Song song in songsToDownload)
        {
            // Генерируем FilePath если его еще нет
            if (string.IsNullOrWhiteSpace(song.FilePath))
            {
                string fileName = $"{Guid.NewGuid()}.pcm";
                song.FilePath = Path.Combine(musicFolder, fileName);
            }
            songsDict[song.Id] = song;
        }

        // Запускаем параллельное скачивание (максимум 3 потока одновременно)
        List<Song> songsList = songsDict.Values.ToList();
        await audioDownloader.DownloadSongsAsync(songsList, maxConcurrency: 3);

        // Обрабатываем результаты скачивания
        int completedSongs = 0;
        using IServiceScope resultScope = _serviceScopeFactory.CreateScope();
        ISongRepository resultSongRepository = resultScope.ServiceProvider.GetRequiredService<ISongRepository>();

        foreach (Song song in songsList)
        {
            try
            {
                Song? songEntity = await resultSongRepository.GetByIdAsync(song.Id);
                if (songEntity == null)
                    continue;

                if (!string.IsNullOrWhiteSpace(song.FilePath) && File.Exists(song.FilePath))
                {
                    // Скачивание успешно - обновляем FilePath в БД если нужно
                    if (songEntity.FilePath != song.FilePath)
                    {
                        songEntity.FilePath = song.FilePath;
                        await resultSongRepository.UpdateAsync(songEntity);
                        await Logger.AddLog($"PCM file saved and updated in DB: {song.FilePath} for song {songEntity.Name}");
                    }
                    completedSongs++;
                }
                else
                {
                    // Скачивание провалилось - удаляем запись из БД
                    await resultSongRepository.RemoveAsync(songEntity);
                    await Logger.AddLog($"{song.Name} - removed from DB (failed to download or file missing)");
                }
            }
            catch (Exception ex)
            {
                await Logger.AddLog($"Error processing download result for {song.Name}: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, exception: ex);
            }
        }

        await Logger.AddLog($"Resolved {completedSongs} missing files in database");
        return completedSongs;
    }
}
