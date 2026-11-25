using System.Collections.Concurrent;
using Logging;
using Entities.Enums;
using Entities.Models;
using Application.Interfaces;

namespace RefactoringExamples;

/// <summary>
/// Пример рефакторинга: Replace Temp with Query
/// 
/// Проблема: Временные переменные используются для хранения результатов вычислений,
/// которые можно получить напрямую через запрос или метод.
/// Решение: Заменить временные переменные на методы или LINQ-запросы.
/// </summary>
public class ReplaceTempWithQuery
{
    #region Пример 1: MusicClient - создание элементов очереди

    /// <summary>
    /// БЫЛО: Временная переменная temp используется для создания элемента очереди
    /// </summary>
    public class MusicClient_BEFORE
    {
        private ConcurrentQueue<KeyValuePair<IVoiceChannel, Song>> music_queue = new();
        private ISongRepository _songRepository = null!;
        private IPlaylistRepository _playlistRepository = null!;

        public async Task PlayAsync(IVoiceChannel? voiceChannel, int? song_id = null, int? playlist_id = null)
        {
            if (voiceChannel == null)
            {
                await Logger.AddLog("User not in voice chat", LogLevel.WARNING);
                return;
            }

            if (song_id != null)
            {
                Song? song = await _songRepository.GetByIdAsync(song_id);
                if (song == null)
                    return;

                // Временная переменная temp
                KeyValuePair<IVoiceChannel, Song> temp = new(voiceChannel, song);
                music_queue.Enqueue(temp);
            }
            
            if (playlist_id != null)
            {
                Playlist? playlist = await _playlistRepository.GetWithSongsAsync(playlist_id.Value);
                if (playlist == null)
                    return;
                    
                foreach (Song s in playlist.Songs)
                {
                    // Временная переменная temp используется повторно
                    KeyValuePair<IVoiceChannel, Song> temp = new(voiceChannel, s);
                    music_queue.Enqueue(temp);
                }
            }
        }
    }

    /// <summary>
    /// СТАЛО: Временная переменная заменена на метод или inline создание
    /// </summary>
    public class MusicClient_AFTER
    {
        private ConcurrentQueue<KeyValuePair<IVoiceChannel, Song>> music_queue = new();
        private ISongRepository _songRepository = null!;
        private IPlaylistRepository _playlistRepository = null!;

        public async Task PlayAsync(IVoiceChannel? voiceChannel, int? song_id = null, int? playlist_id = null)
        {
            if (voiceChannel == null)
            {
                await Logger.AddLog("User not in voice chat", LogLevel.WARNING);
                return;
            }

            if (song_id != null)
            {
                Song? song = await _songRepository.GetByIdAsync(song_id);
                if (song == null)
                    return;

                // Вариант 1: Inline создание (если простое)
                music_queue.Enqueue(new KeyValuePair<IVoiceChannel, Song>(voiceChannel, song));
                
                // Или вариант 2: Использование метода-помощника (если логика сложнее)
                // music_queue.Enqueue(CreateQueueItem(voiceChannel, song));
            }
            
            if (playlist_id != null)
            {
                Playlist? playlist = await _playlistRepository.GetWithSongsAsync(playlist_id.Value);
                if (playlist == null)
                    return;
                    
                // Используем LINQ для создания элементов очереди
                var queueItems = playlist.Songs.Select(s => new KeyValuePair<IVoiceChannel, Song>(voiceChannel, s));
                foreach (var item in queueItems)
                {
                    music_queue.Enqueue(item);
                }
            }
        }

        // Опционально: метод-помощник для создания элемента очереди
        private static KeyValuePair<IVoiceChannel, Song> CreateQueueItem(IVoiceChannel channel, Song song)
        {
            return new KeyValuePair<IVoiceChannel, Song>(channel, song);
        }
    }

    #endregion

    #region Пример 2: AudioDownloaderService - получение сообщения об ошибке

    /// <summary>
    /// БЫЛО: Временная переменная errorMessage с условной логикой
    /// </summary>
    public class AudioDownloaderService_BEFORE
    {
        public async Task DownloadWithYoutubeDLSharp_BEFORE(string url, Song song, string musicFolderPath)
        {
            // ... код загрузки ...
            
            if (result.Success)
            {
                // ... успешная обработка ...
            }
            else
            {
                // Временная переменная с условной логикой
                string errorMessage = result.ErrorOutput != null && result.ErrorOutput.Count() > 0
                    ? result.ErrorOutput[0]
                    : "Unknown error";
                    
                await Logger.AddLog($"Download failed: {errorMessage}", LogLevel.ERROR);
                throw new Exception($"YoutubeDLSharp download failed: {errorMessage}");
            }
        }
    }

    /// <summary>
    /// СТАЛО: Временная переменная заменена на метод-запрос
    /// </summary>
    public class AudioDownloaderService_AFTER
    {
        public async Task DownloadWithYoutubeDLSharp_AFTER(string url, Song song, string musicFolderPath)
        {
            // ... код загрузки ...
            
            if (result.Success)
            {
                // ... успешная обработка ...
            }
            else
            {
                // Используем метод-запрос вместо временной переменной
                string errorMessage = GetErrorMessage(result.ErrorOutput);
                    
                await Logger.AddLog($"Download failed: {errorMessage}", LogLevel.ERROR);
                throw new Exception($"YoutubeDLSharp download failed: {errorMessage}");
            }
        }

        /// <summary>
        /// Метод-запрос для получения сообщения об ошибке
        /// </summary>
        private static string GetErrorMessage(IEnumerable<string>? errorOutput)
        {
            return errorOutput?.FirstOrDefault() ?? "Unknown error";
        }
    }

    #endregion

    #region Пример 3: DiscordBotService - фильтрация сообщений бота

    /// <summary>
    /// БЫЛО: Временная переменная botMessages для хранения отфильтрованных сообщений
    /// </summary>
    public class DiscordBotService_BEFORE
    {
        public async Task CleanupAnchorChannelsAsync_BEFORE()
        {
            ulong botUserId = _client.CurrentUser.Id;
            
            await foreach (IReadOnlyCollection<IMessage> messages in messageBatches)
            {
                // Временная переменная для хранения результата LINQ-запроса
                List<IMessage> botMessages = messages.Where(m => m.Author.Id == botUserId).ToList();

                foreach (IMessage message in botMessages)
                {
                    try
                    {
                        await message.DeleteAsync();
                    }
                    catch (Exception ex)
                    {
                        // обработка ошибок
                    }
                }
            }
        }
    }

    /// <summary>
    /// СТАЛО: Временная переменная заменена на прямой запрос в foreach
    /// </summary>
    public class DiscordBotService_AFTER
    {
        public async Task CleanupAnchorChannelsAsync_AFTER()
        {
            ulong botUserId = _client.CurrentUser.Id;
            
            await foreach (IReadOnlyCollection<IMessage> messages in messageBatches)
            {
                // Используем запрос напрямую в foreach без временной переменной
                foreach (IMessage message in messages.Where(m => m.Author.Id == botUserId))
                {
                    try
                    {
                        await message.DeleteAsync();
                    }
                    catch (Exception ex)
                    {
                        // обработка ошибок
                    }
                }
            }
        }
    }

    #endregion

    #region Пример 4: Сложный случай - вычисление с несколькими шагами

    /// <summary>
    /// БЫЛО: Несколько временных переменных для промежуточных вычислений
    /// </summary>
    public class ComplexCalculation_BEFORE
    {
        public double CalculateTotalPrice(List<OrderItem> items, double taxRate)
        {
            // Временная переменная для суммы без налога
            double subtotal = items.Sum(item => item.Price * item.Quantity);
            
            // Временная переменная для налога
            double tax = subtotal * taxRate;
            
            // Временная переменная для итоговой суммы
            double total = subtotal + tax;
            
            return total;
        }
    }

    /// <summary>
    /// СТАЛО: Временные переменные заменены на методы-запросы
    /// </summary>
    public class ComplexCalculation_AFTER
    {
        public double CalculateTotalPrice(List<OrderItem> items, double taxRate)
        {
            // Используем методы-запросы вместо временных переменных
            return GetSubtotal(items) + GetTax(items, taxRate);
        }

        private static double GetSubtotal(List<OrderItem> items)
        {
            return items.Sum(item => item.Price * item.Quantity);
        }

        private static double GetTax(List<OrderItem> items, double taxRate)
        {
            return GetSubtotal(items) * taxRate;
        }
    }

    #endregion
}

// Вспомогательные классы для примеров
public interface IVoiceChannel { }
public interface IMessage 
{ 
    ulong Id { get; }
    IUser Author { get; }
    Task DeleteAsync();
}

public interface IUser
{
    ulong Id { get; }
}

public class OrderItem
{
    public double Price { get; set; }
    public int Quantity { get; set; }
}

