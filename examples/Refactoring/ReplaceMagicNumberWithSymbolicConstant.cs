using System.Diagnostics;
using Logging;
using Entities.Enums;

namespace RefactoringExamples;

/// <summary>
/// Пример рефакторинга: Replace Magic Number with Symbolic Constant
/// 
/// Проблема: В коде используются "магические числа" - числовые константы без явного значения.
/// Решение: Заменить их на именованные константы с понятными названиями.
/// </summary>
public class ReplaceMagicNumberWithSymbolicConstant
{
    #region Пример 1: FfmpegInteractor - аудио параметры и размер буфера

    /// <summary>
    /// БЫЛО: Магические числа в аргументах FFmpeg и размере буфера
    /// </summary>
    public static async Task<byte[]?> ConvertMp3ToPcmBytes_BEFORE(string file_path)
    {
        if (!File.Exists(file_path))
        {
            await Logger.AddLog("File to convert not found!", LogLevel.ERROR);
            return null;
        }

        string ffmpegPath = Path.Combine(Environment.CurrentDirectory, "appdata", "ffmpeg.exe");

        if (!File.Exists(ffmpegPath))
        {
            await Logger.AddLog("FFmpeg executable not found!", LogLevel.ERROR);
            return null;
        }

        ProcessStartInfo processStartInfo = new()
        {
            FileName = ffmpegPath,
            // Магические числа: 48000 (sample rate), 2 (channels), 8192 (buffer size)
            Arguments = $"-i \"{file_path}\" -f s16le -ar 48000 -ac 2 -",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var ffmpegProcess = new Process { StartInfo = processStartInfo };
        using var outputStream = new MemoryStream();

        if (!ffmpegProcess.Start())
        {
            await Logger.AddLog("FFMPEG STARTUP ERROR", LogLevel.ERROR);
            return null;
        }

        ffmpegProcess.BeginErrorReadLine();

        // Магическое число: 8192 байта для буфера
        byte[] buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = await ffmpegProcess.StandardOutput.BaseStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await outputStream.WriteAsync(buffer, 0, bytesRead);
        }

        await ffmpegProcess.WaitForExitAsync();

        if (ffmpegProcess.ExitCode != 0)
        {
            await Logger.AddLog($"FFmpeg conversion failed with exit code {ffmpegProcess.ExitCode}", LogLevel.ERROR);
            return null;
        }

        await Logger.AddLog("FFMPEG - conversion completed");
        return outputStream.ToArray();
    }

    /// <summary>
    /// СТАЛО: Магические числа заменены на именованные константы
    /// </summary>
    // Константы для аудио параметров
    private const int AUDIO_SAMPLE_RATE_HZ = 48000;  // Стандартная частота дискретизации для Discord
    private const int AUDIO_CHANNELS = 2;              // Стерео (2 канала)
    private const int BUFFER_SIZE_BYTES = 8192;       // Размер буфера для чтения потока (8 KB)

    public static async Task<byte[]?> ConvertMp3ToPcmBytes_AFTER(string file_path)
    {
        if (!File.Exists(file_path))
        {
            await Logger.AddLog("File to convert not found!", LogLevel.ERROR);
            return null;
        }

        string ffmpegPath = Path.Combine(Environment.CurrentDirectory, "appdata", "ffmpeg.exe");

        if (!File.Exists(ffmpegPath))
        {
            await Logger.AddLog("FFmpeg executable not found!", LogLevel.ERROR);
            return null;
        }

        ProcessStartInfo processStartInfo = new()
        {
            FileName = ffmpegPath,
            // Используем константы вместо магических чисел
            Arguments = $"-i \"{file_path}\" -f s16le -ar {AUDIO_SAMPLE_RATE_HZ} -ac {AUDIO_CHANNELS} -",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var ffmpegProcess = new Process { StartInfo = processStartInfo };
        using var outputStream = new MemoryStream();

        if (!ffmpegProcess.Start())
        {
            await Logger.AddLog("FFMPEG STARTUP ERROR", LogLevel.ERROR);
            return null;
        }

        ffmpegProcess.BeginErrorReadLine();

        // Используем константу для размера буфера
        byte[] buffer = new byte[BUFFER_SIZE_BYTES];
        int bytesRead;
        while ((bytesRead = await ffmpegProcess.StandardOutput.BaseStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await outputStream.WriteAsync(buffer, 0, bytesRead);
        }

        await ffmpegProcess.WaitForExitAsync();

        if (ffmpegProcess.ExitCode != 0)
        {
            await Logger.AddLog($"FFmpeg conversion failed with exit code {ffmpegProcess.ExitCode}", LogLevel.ERROR);
            return null;
        }

        await Logger.AddLog("FFMPEG - conversion completed");
        return outputStream.ToArray();
    }

    #endregion

    #region Пример 2: MusicClient - виртуальные плейлисты

    /// <summary>
    /// БЫЛО: Магические числа для ID виртуальных плейлистов
    /// </summary>
    public class MusicClient_BEFORE
    {
        // Магические числа: -1 и -2 для виртуальных плейлистов
        public Playlist playback_history { get; protected set; } = new Playlist() { Name = "History", Id = -1, AuthorId = 0 };
        public Playlist popular_songs { get; protected set; } = new Playlist() { Name = "Popular", Id = -2, AuthorId = 0 };
    }

    /// <summary>
    /// СТАЛО: Магические числа заменены на константы
    /// </summary>
    public class MusicClient_AFTER
    {
        // Константы для виртуальных плейлистов
        private const int HISTORY_PLAYLIST_ID = -1;
        private const int POPULAR_PLAYLIST_ID = -2;
        private const int DEFAULT_AUTHOR_ID = 0;

        public Playlist playback_history { get; protected set; } = new Playlist() 
        { 
            Name = "History", 
            Id = HISTORY_PLAYLIST_ID, 
            AuthorId = DEFAULT_AUTHOR_ID 
        };
        
        public Playlist popular_songs { get; protected set; } = new Playlist() 
        { 
            Name = "Popular", 
            Id = POPULAR_PLAYLIST_ID, 
            AuthorId = DEFAULT_AUTHOR_ID 
        };
    }

    #endregion

    #region Пример 3: DiscordBotService - размер батча и задержка

    /// <summary>
    /// БЫЛО: Магические числа для размера батча и задержки
    /// </summary>
    public class DiscordBotService_BEFORE
    {
        public async Task CleanupAnchorChannelsAsync()
        {
            // ... код ...
            
            int deletedInChannel = 0;
            const int batchSize = 100;  // Магическое число

            IAsyncEnumerable<IReadOnlyCollection<IMessage>> messageBatches = channel.GetMessagesAsync(batchSize);
            await foreach (IReadOnlyCollection<IMessage> messages in messageBatches)
            {
                // ... код ...
                
                foreach (IMessage message in botMessages)
                {
                    try
                    {
                        await message.DeleteAsync();
                        deletedInChannel++;
                        totalDeleted++;

                        // Магическое число: 100 мс задержка
                        await Task.Delay(100);
                    }
                    catch (Exception ex)
                    {
                        // обработка ошибок
                    }
                }

                // Магическое число используется повторно
                if (messages.Count < batchSize)
                    break;
            }
        }
    }

    /// <summary>
    /// СТАЛО: Магические числа заменены на константы
    /// </summary>
    public class DiscordBotService_AFTER
    {
        // Константы для работы с сообщениями
        private const int MESSAGE_BATCH_SIZE = 100;           // Размер батча для получения сообщений
        private const int MESSAGE_DELETE_DELAY_MS = 100;       // Задержка между удалениями (rate limit protection)

        public async Task CleanupAnchorChannelsAsync()
        {
            // ... код ...
            
            int deletedInChannel = 0;

            // Используем константу вместо магического числа
            IAsyncEnumerable<IReadOnlyCollection<IMessage>> messageBatches = channel.GetMessagesAsync(MESSAGE_BATCH_SIZE);
            await foreach (IReadOnlyCollection<IMessage> messages in messageBatches)
            {
                // ... код ...
                
                foreach (IMessage message in botMessages)
                {
                    try
                    {
                        await message.DeleteAsync();
                        deletedInChannel++;
                        totalDeleted++;

                        // Используем константу для задержки
                        await Task.Delay(MESSAGE_DELETE_DELAY_MS);
                    }
                    catch (Exception ex)
                    {
                        // обработка ошибок
                    }
                }

                // Используем константу для проверки
                if (messages.Count < MESSAGE_BATCH_SIZE)
                    break;
            }
        }
    }

    #endregion
}

// Вспомогательные классы для примеров
public class Playlist
{
    public string Name { get; set; } = string.Empty;
    public int Id { get; set; }
    public int AuthorId { get; set; }
}

