using Discord;
using Discord.WebSocket;
using Logging;
using Entities.Enums;

namespace RefactoringExamples;

/// <summary>
/// Пример рефакторинга: Replace Nested Conditional with Guard Clauses
/// 
/// Проблема: Вложенные if-else усложняют чтение кода и понимание логики.
/// Решение: Использовать guard clauses (ранние возвраты) для упрощения структуры.
/// </summary>
public class ReplaceNestedConditionalWithGuardClauses
{
    #region Пример 1: MusicClient.JoinAsync - сложная логика подключения

    /// <summary>
    /// БЫЛО: Вложенные if-else для обработки различных состояний подключения
    /// </summary>
    public class MusicClient_BEFORE
    {
        private IAudioClient? audio_client;
        private IVoiceChannel? current_voice_channel;
        private const int CONNECTION_WAIT_MS = 1000;

        public async Task JoinAsync(IVoiceChannel channel)
        {
            await Logger.AddLog($"JoinAsync to voice channel {channel.Name} called");

            // Вложенные условия делают код сложным для понимания
            if (audio_client != null && audio_client.ConnectionState == ConnectionState.Connected && current_voice_channel != channel)
            {
                await audio_client.StopAsync();
                audio_client = await channel.ConnectAsync();
                current_voice_channel = channel;
            }
            else if (audio_client != null && audio_client.ConnectionState == ConnectionState.Connecting && current_voice_channel != channel)
            {
                await Task.Delay(CONNECTION_WAIT_MS);
                await audio_client.StopAsync();
                audio_client = await channel.ConnectAsync();
                current_voice_channel = channel;
            }
            else if (audio_client != null && audio_client.ConnectionState == ConnectionState.Disconnecting)
            {
                await Task.Delay(CONNECTION_WAIT_MS);
                audio_client = await channel.ConnectAsync();
                current_voice_channel = channel;
            }
            else
            {
                audio_client = await channel.ConnectAsync();
                current_voice_channel = channel;
            }
        }
    }

    /// <summary>
    /// СТАЛО: Guard clauses упрощают логику и делают код более читаемым
    /// </summary>
    public class MusicClient_AFTER
    {
        private IAudioClient? audio_client;
        private IVoiceChannel? current_voice_channel;
        private const int CONNECTION_WAIT_MS = 1000;

        public async Task JoinAsync(IVoiceChannel channel)
        {
            await Logger.AddLog($"JoinAsync to voice channel {channel.Name} called");

            // Guard clause: если клиента нет, просто подключаемся
            if (audio_client == null)
            {
                audio_client = await channel.ConnectAsync();
                current_voice_channel = channel;
                return;
            }

            // Guard clause: если уже подключены к этому каналу, ничего не делаем
            if (current_voice_channel == channel && audio_client.ConnectionState == ConnectionState.Connected)
            {
                return;
            }

            // Guard clause: если идет подключение или отключение, ждем
            if (audio_client.ConnectionState == ConnectionState.Connecting || 
                audio_client.ConnectionState == ConnectionState.Disconnecting)
            {
                await Task.Delay(CONNECTION_WAIT_MS);
            }

            // Guard clause: если подключены, отключаемся перед переподключением
            if (audio_client.ConnectionState != ConnectionState.Disconnected)
            {
                await audio_client.StopAsync();
            }

            // Основная логика: подключаемся к новому каналу
            audio_client = await channel.ConnectAsync();
            current_voice_channel = channel;
        }
    }

    #endregion

    #region Пример 2: AudioDownloaderService.Download - проверка условий загрузки

    /// <summary>
    /// БЫЛО: Вложенные условия для проверки и загрузки
    /// </summary>
    public class AudioDownloaderService_BEFORE
    {
        public async Task<Song?> Download_BEFORE(Song song, ISongRepository songRepository, string musicFolderPath, Provider provider)
        {
            if (song.Link == null)
            {
                await Logger.AddLog("song link was null", LogLevel.ERROR);
                return null;
            }
            else
            {
                // Проверяем, есть ли уже PCM файл
                if (!string.IsNullOrEmpty(song.FilePath) && File.Exists(song.FilePath))
                {
                    await Logger.AddLog($"PCM file already exists: {song.FilePath}");
                    return song;
                }
                else
                {
                    // Try primary provider first
                    string? pcmFilePath = await TryDownloadWithProvider(song.Link, song, musicFolderPath, provider);
                    if (pcmFilePath == null)
                    {
                        // If primary provider failed, try fallback provider
                        Provider fallbackProvider = provider == Provider.YoutubeExplode ? Provider.YoutubeDLSharp : Provider.YoutubeExplode;
                        await Logger.AddLog($"Primary provider failed, trying fallback provider: {fallbackProvider}", LogLevel.WARNING);
                        pcmFilePath = await TryDownloadWithProvider(song.Link, song, musicFolderPath, fallbackProvider);
                    }

                    if (pcmFilePath == null)
                    {
                        await Logger.AddLog("Failed to download audio from all providers", LogLevel.ERROR);
                        return null;
                    }
                    else
                    {
                        // Проверяем, что файл действительно был создан
                        if (!File.Exists(pcmFilePath))
                        {
                            await Logger.AddLog($"PCM file was not created: {pcmFilePath}", LogLevel.ERROR);
                            return null;
                        }
                        else
                        {
                            // Обновляем путь к файлу в БД
                            song.FilePath = pcmFilePath;
                            await songRepository.UpdateAsync(song);
                            await Logger.AddLog($"PCM file saved: {pcmFilePath}");
                            return song;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// СТАЛО: Guard clauses упрощают структуру кода
    /// </summary>
    public class AudioDownloaderService_AFTER
    {
        public async Task<Song?> Download_AFTER(Song song, ISongRepository songRepository, string musicFolderPath, Provider provider)
        {
            // Guard clause: проверка ссылки
            if (song.Link == null)
            {
                await Logger.AddLog("song link was null", LogLevel.ERROR);
                return null;
            }

            // Guard clause: если файл уже существует, возвращаем его
            if (!string.IsNullOrEmpty(song.FilePath) && File.Exists(song.FilePath))
            {
                await Logger.AddLog($"PCM file already exists: {song.FilePath}");
                return song;
            }

            // Пытаемся загрузить с основным провайдером
            string? pcmFilePath = await TryDownloadWithProvider(song.Link, song, musicFolderPath, provider);
            
            // Guard clause: если основной провайдер не сработал, пробуем резервный
            if (pcmFilePath == null)
            {
                Provider fallbackProvider = provider == Provider.YoutubeExplode ? Provider.YoutubeDLSharp : Provider.YoutubeExplode;
                await Logger.AddLog($"Primary provider failed, trying fallback provider: {fallbackProvider}", LogLevel.WARNING);
                pcmFilePath = await TryDownloadWithProvider(song.Link, song, musicFolderPath, fallbackProvider);
            }

            // Guard clause: если все провайдеры не сработали
            if (pcmFilePath == null)
            {
                await Logger.AddLog("Failed to download audio from all providers", LogLevel.ERROR);
                return null;
            }

            // Guard clause: проверка существования файла
            if (!File.Exists(pcmFilePath))
            {
                await Logger.AddLog($"PCM file was not created: {pcmFilePath}", LogLevel.ERROR);
                return null;
            }

            // Основная логика: обновление пути в БД
            song.FilePath = pcmFilePath;
            await songRepository.UpdateAsync(song);
            await Logger.AddLog($"PCM file saved: {pcmFilePath}");
            return song;
        }

        private async Task<string?> TryDownloadWithProvider(string url, Song song, string musicFolderPath, Provider provider)
        {
            // Реализация метода
            return null;
        }
    }

    #endregion

    #region Пример 3: FfmpegInteractor - проверка файлов

    /// <summary>
    /// БЫЛО: Вложенные условия для проверки существования файлов
    /// </summary>
    public class FfmpegInteractor_BEFORE
    {
        public static async Task<byte[]?> ConvertMp3ToPcmBytes_BEFORE(string file_path)
        {
            if (!File.Exists(file_path))
            {
                await Logger.AddLog("File to convert not found!", LogLevel.ERROR);
                return null;
            }
            else
            {
                string ffmpegPath = Path.Combine(Environment.CurrentDirectory, "appdata", "ffmpeg.exe");

                if (!File.Exists(ffmpegPath))
                {
                    await Logger.AddLog("FFmpeg executable not found!", LogLevel.ERROR);
                    return null;
                }
                else
                {
                    // Основная логика конвертации
                    // ...
                    return null;
                }
            }
        }
    }

    /// <summary>
    /// СТАЛО: Guard clauses для ранних возвратов
    /// </summary>
    public class FfmpegInteractor_AFTER
    {
        public static async Task<byte[]?> ConvertMp3ToPcmBytes_AFTER(string file_path)
        {
            // Guard clause: проверка существования исходного файла
            if (!File.Exists(file_path))
            {
                await Logger.AddLog("File to convert not found!", LogLevel.ERROR);
                return null;
            }

            string ffmpegPath = Path.Combine(Environment.CurrentDirectory, "appdata", "ffmpeg.exe");

            // Guard clause: проверка существования FFmpeg
            if (!File.Exists(ffmpegPath))
            {
                await Logger.AddLog("FFmpeg executable not found!", LogLevel.ERROR);
                return null;
            }

            // Основная логика конвертации (без вложенности)
            // ...
            return null;
        }
    }

    #endregion

    #region Пример 4: CreatorCommands - проверка прав доступа

    /// <summary>
    /// БЫЛО: Вложенные условия для проверки прав доступа
    /// </summary>
    public class CreatorCommands_BEFORE
    {
        private IConfiguration _configuration = null!;

        [Command("reboot")]
        public async Task Reboot_BEFORE(SocketCommandContext context)
        {
            await context.Message.DeleteAsync();

            string? owner_id_str = _configuration["owner_id"];
            if (owner_id_str != null)
            {
                ulong owner_id = ulong.Parse(owner_id_str);

                if (context.User.Id == owner_id)
                {
                    Program.RestartApplication();
                }
            }
        }
    }

    /// <summary>
    /// СТАЛО: Guard clauses для упрощения проверок
    /// </summary>
    public class CreatorCommands_AFTER
    {
        private IConfiguration _configuration = null!;

        [Command("reboot")]
        public async Task Reboot_AFTER(SocketCommandContext context)
        {
            await context.Message.DeleteAsync();

            // Guard clause: проверка наличия owner_id в конфигурации
            string? owner_id_str = _configuration["owner_id"];
            if (owner_id_str == null)
            {
                return;
            }

            // Guard clause: проверка прав доступа
            ulong owner_id = ulong.Parse(owner_id_str);
            if (context.User.Id != owner_id)
            {
                return;
            }

            // Основная логика: перезапуск приложения
            Program.RestartApplication();
        }
    }

    #endregion
}

// Вспомогательные классы и интерфейсы для примеров
public interface IAudioClient
{
    ConnectionState ConnectionState { get; }
    Task StopAsync();
    Task<IAudioClient> ConnectAsync();
}

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting
}

public interface IVoiceChannel
{
    string Name { get; }
    Task<IAudioClient> ConnectAsync();
}

public interface ISongRepository
{
    Task<Song?> GetByIdAsync(int? id);
    Task UpdateAsync(Song song);
}

public class Song
{
    public string? Link { get; set; }
    public string? FilePath { get; set; }
}

public enum Provider
{
    YoutubeExplode,
    YoutubeDLSharp
}

public class SocketCommandContext
{
    public IUser User { get; set; } = null!;
    public IMessage Message { get; set; } = null!;
}

public interface IUser
{
    ulong Id { get; }
}

public interface IMessage
{
    Task DeleteAsync();
}

public class IConfiguration
{
    public string? this[string key] => null;
}

public class Program
{
    public static void RestartApplication() { }
}

public class CommandAttribute : Attribute
{
    public CommandAttribute(string command) { }
}

