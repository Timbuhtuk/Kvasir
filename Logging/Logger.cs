using Discord;
using Entities.Enums;
using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Logging;

/// <summary>
/// Логгер с поддержкой категорий, гильдий и сохранением в файлы
/// </summary>
public static class Logger
{
    private static readonly object _logLock = new();
    private static readonly ConcurrentQueue<LogEntry> _logQueue = new();
    private static readonly string _logsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
    private static readonly string _generalLogsDirectory = Path.Combine(_logsDirectory, "general");
    private static readonly string _guildLogsDirectory = Path.Combine(_logsDirectory, "guilds");
    
    private static Timer? _flushTimer;
    
    public static int LoggingLevel { get; set; } = 3; // 0 - no logging, 1 - only error, 2 - warnings, 3 - all
    public static bool EnableFileLogging { get; set; } = true;
    public static bool EnableConsoleLogging { get; set; } = true;
    
    public const ConsoleColor _DEFAULT_OUTLINE_COLOR = ConsoleColor.Cyan;
    public static ConsoleColor outline_color = ConsoleColor.Cyan;
    
    /// <summary>
    /// Автоматически определяет глубину логирования на основе стека вызовов
    /// Считает количество уровней вложенности методов до вызова Logger.AddLog
    /// </summary>
    private static int CalculateDepth()
    {
        try
        {
            StackTrace stackTrace = new StackTrace(skipFrames: 1, fNeedFileInfo: false);
            int depth = 0;
            bool foundLoggerCall = false;
            
            // Проходим по стеку вызовов снизу вверх
            for (int i = 0; i < stackTrace.FrameCount; i++)
            {
                StackFrame? frame = stackTrace.GetFrame(i);
                if (frame == null)
                    continue;

                MethodBase? method = frame.GetMethod();
                if (method == null)
                    continue;

                string? declaringTypeName = method.DeclaringType?.FullName;
                
                // Пропускаем методы самого Logger и внутренние методы логирования
                if (declaringTypeName != null && declaringTypeName.StartsWith("Logging.Logger"))
                {
                    foundLoggerCall = true;
                    continue;
                }
                
                // Пропускаем методы из System, Microsoft и других системных библиотек
                if (declaringTypeName != null && 
                    (declaringTypeName.StartsWith("System.") || 
                     declaringTypeName.StartsWith("Microsoft.") ||
                     declaringTypeName.StartsWith("Discord.")))
                {
                    continue;
                }
                
                // Если мы уже прошли вызов Logger, считаем остальные методы как глубину
                if (foundLoggerCall)
                {
                    depth++;
                }
                
                // Ограничиваем максимальную глубину для производительности
                if (depth >= 15)
                    break;
            }
            
            return depth;
        }
        catch
        {
            // В случае ошибки возвращаем 0
            return 0;
        }
    }

    public static void Initialize(IConfiguration configuration)
    {
        if (int.TryParse(configuration["logging"], out int level))
        {
            LoggingLevel = level;
        }
        
        if (bool.TryParse(configuration["logging:enableFileLogging"], out bool enableFile))
        {
            EnableFileLogging = enableFile;
        }
        
        if (bool.TryParse(configuration["logging:enableConsoleLogging"], out bool enableConsole))
        {
            EnableConsoleLogging = enableConsole;
        }

        if (EnableFileLogging)
        {
            // Создаем директории для логов
            Directory.CreateDirectory(_logsDirectory);
            Directory.CreateDirectory(_generalLogsDirectory);
            Directory.CreateDirectory(_guildLogsDirectory);
            
            // Запускаем таймер для периодической записи логов в файлы
            _flushTimer = new Timer(FlushLogsToFiles, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>
    /// Определяет категорию лога на основе атрибута класса или пути файла
    /// </summary>
    private static LogCategory DetermineCategory(string filePath, string caller)
    {
        // Сначала пытаемся найти категорию через атрибут класса
        LogCategory? categoryFromAttribute = GetCategoryFromAttribute();
        if (categoryFromAttribute.HasValue)
        {
            return categoryFromAttribute.Value;
        }

        // Если атрибут не найден, используем логику по пути файла
        if (string.IsNullOrEmpty(filePath))
            return LogCategory.General;

        // Нормализуем путь для кроссплатформенности
        string normalizedPath = filePath.Replace('\\', '/').ToLowerInvariant();
        string fileName = Path.GetFileNameWithoutExtension(filePath);

        // Определяем категорию по пути и имени файла
        if (normalizedPath.Contains("/music_parts/") || normalizedPath.Contains("\\music_parts\\"))
        {
            if (fileName.Contains("MusicClient"))
                return LogCategory.Music;
            if (fileName.Contains("AudioDownloader"))
                return LogCategory.AudioDownload;
            return LogCategory.Music;
        }

        if (normalizedPath.Contains("/handlers/") || normalizedPath.Contains("\\handlers\\"))
        {
            if (fileName.Contains("CommandHandler") || fileName.Contains("Command"))
                return LogCategory.Command;
            if (fileName.Contains("ComponentHandler") || fileName.Contains("Component"))
                return LogCategory.Component;
            if (fileName.Contains("EventHandler") || fileName.Contains("Event"))
                return LogCategory.Event;
        }

        if (normalizedPath.Contains("/services/") || normalizedPath.Contains("\\services\\"))
        {
            if (fileName.Contains("VideoFinder"))
                return LogCategory.VideoFinder;
            if (fileName.Contains("DiscordBot") || fileName.Contains("Discord"))
                return LogCategory.Discord;
            return LogCategory.Service;
        }

        if (normalizedPath.Contains("/repository/") || normalizedPath.Contains("\\repository\\") ||
            normalizedPath.Contains("/infrastructure/") || normalizedPath.Contains("\\infrastructure\\"))
        {
            return LogCategory.Database;
        }

        if (fileName.Contains("Ffmpeg") || fileName.Contains("FFmpeg"))
        {
            return LogCategory.FFmpeg;
        }

        if (normalizedPath.Contains("/commands/") || normalizedPath.Contains("\\commands\\"))
        {
            return LogCategory.Command;
        }

        // Проверяем по имени вызывающего метода
        if (caller.Contains("Database") || caller.Contains("Repository") || caller.Contains("Context"))
        {
            return LogCategory.Database;
        }

        return LogCategory.General;
    }

    /// <summary>
    /// Получает категорию лога из атрибута класса через рефлексию
    /// </summary>
    private static LogCategory? GetCategoryFromAttribute()
    {
        try
        {
            // Получаем стек вызовов
            // Пропускаем: GetCategoryFromAttribute, DetermineCategory, Logger.AddLog
            StackTrace stackTrace = new StackTrace(skipFrames: 3, fNeedFileInfo: false);
            
            // Проходим по стеку вызовов, начиная с вызывающего метода
            for (int i = 0; i < stackTrace.FrameCount; i++)
            {
                StackFrame? frame = stackTrace.GetFrame(i);
                if (frame == null)
                    continue;

                MethodBase? method = frame.GetMethod();
                if (method == null)
                    continue;

                // Получаем тип, к которому принадлежит метод
                Type? declaringType = method.DeclaringType;
                if (declaringType == null)
                    continue;

                // Проверяем атрибут на типе
                var attribute = declaringType.GetCustomAttribute<LogCategoryAttribute>();
                if (attribute != null)
                {
                    return attribute.Category;
                }

                // Также проверяем базовые классы (для наследования)
                Type? baseType = declaringType.BaseType;
                while (baseType != null && baseType != typeof(object))
                {
                    var baseAttribute = baseType.GetCustomAttribute<LogCategoryAttribute>();
                    if (baseAttribute != null)
                    {
                        return baseAttribute.Category;
                    }
                    baseType = baseType.BaseType;
                }
            }
        }
        catch
        {
            // В случае ошибки рефлексии возвращаем null, чтобы использовать fallback логику
        }

        return null;
    }

    /// <summary>
    /// Добавить лог с указанием категории (если нужно переопределить автоматическое определение)
    /// </summary>
    public static async Task AddLog(
        string message,
        LogCategory category,
        LogLevel msgType = LogLevel.INFO,
        ulong? guildId = null,
        string? guildName = null,
        [CallerMemberName] string caller = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0,
        Exception? exception = null)
    {
        await AddLogInternal(message, category, msgType, guildId, guildName, null, caller, file, line, exception);
    }
    
    /// <summary>
    /// Добавить лог с поддержкой именованных параметров для гильдии
    /// </summary>
    public static async Task AddLog(
        string message,
        LogLevel msgType = LogLevel.INFO,
        ulong? guildId = null,
        string? guildName = null,
        Exception? exception = null,
        [CallerMemberName] string caller = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        // Автоматически определяем категорию
        await AddLogInternal(message, null, msgType, guildId, guildName, null, caller, file, line, exception);
    }

    /// <summary>
    /// Добавить лог из Discord.LogMessage
    /// </summary>
    public static async Task AddLog(LogMessage log)
    {
        // Для Discord логов категория всегда Discord
        await AddLogInternal(
            log.Exception == null ? log.Message : log.Exception.Message, 
            LogCategory.Discord,
            log.Exception == null ? LogLevel.INFO : LogLevel.ERROR,
            exception: log.Exception);
    }

    /// <summary>
    /// Внутренний метод для добавления лога
    /// </summary>
    private static async Task AddLogInternal(
        string message,
        LogCategory? category = null,
        LogLevel level = LogLevel.INFO,
        ulong? guildId = null,
        string? guildName = null,
        ConsoleColor? color = null,
        [CallerMemberName] string caller = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0,
        Exception? exception = null)
    {
        await Task.Yield();

        // Если guildId не указан явно, используем из контекста
        guildId ??= LogContext.GuildId;
        guildName ??= LogContext.GuildName;

        // Автоматически определяем категорию, если не указана
        LogCategory determinedCategory = category ?? DetermineCategory(file, caller);

        int logLevel = level == LogLevel.ERROR ? 1 : level == LogLevel.WARNING ? 2 : 3;
        
        if (LoggingLevel < logLevel)
            return;

        string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string fileShort = Path.GetFileName(file);
        int threadId = Thread.CurrentThread.ManagedThreadId;

        // Определяем цвет для консоли
        ConsoleColor consoleColor = color ?? 
            (level == LogLevel.ERROR ? ConsoleColor.Red : 
             level == LogLevel.WARNING ? ConsoleColor.Yellow : 
             ConsoleColor.White);

        // Автоматически определяем глубину через стек вызовов
        int calculatedDepth = CalculateDepth();
        
        // Создаем структурированную запись лога
        var logEntry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Category = determinedCategory,
            GuildId = guildId,
            GuildName = guildName,
            Message = message,
            Caller = caller,
            File = fileShort,
            Line = line,
            ThreadId = threadId,
            Exception = exception?.ToString(),
            Depth = calculatedDepth
        };

        // Добавляем в очередь для записи в файлы
        if (EnableFileLogging)
        {
            _logQueue.Enqueue(logEntry);
        }

        // Выводим в консоль
        if (EnableConsoleLogging)
        {
            await WriteToConsole(logEntry, consoleColor, time, fileShort);
        }
    }

    private static async Task WriteToConsole(LogEntry entry, ConsoleColor color, string time, string fileShort)
    {
        await Task.Yield();
        
        string offset = "";
        for (int q = 0; q < entry.Depth; q++)
            offset += " |";
        if (entry.Depth > 0)
            offset += "->";
        else if (color == ConsoleColor.White)
            color = outline_color;

        string categoryStr = $"[{entry.Category}]";
        string guildStr = entry.GuildId.HasValue ? $"[Guild: {entry.GuildName ?? entry.GuildId.ToString()}]" : "";
        string levelStr = entry.Level == LogLevel.ERROR ? "ERR" : entry.Level == LogLevel.WARNING ? "WAR" : "INF";

        lock (_logLock)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{time}]");
            Console.ForegroundColor = outline_color;
            Console.Write($"{offset}");
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.Write($"{categoryStr} ");
            if (!string.IsNullOrEmpty(guildStr))
            {
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.Write($"{guildStr} ");
            }
            Console.ForegroundColor = color;
            Console.Write($"{entry.Message} ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{levelStr}][{entry.ThreadId}][{entry.Caller} @ {fileShort}:{entry.Line}]");
            Console.WriteLine();
            Console.ResetColor();
        }
    }

    private static async void FlushLogsToFiles(object? state)
    {
        if (!EnableFileLogging || _logQueue.IsEmpty)
            return;

        var logsToWrite = new List<LogEntry>();
        while (_logQueue.TryDequeue(out var logEntry))
        {
            logsToWrite.Add(logEntry);
        }

        if (logsToWrite.Count == 0)
            return;

        try
        {
            // Группируем логи по категориям и гильдиям
            var groupedLogs = logsToWrite.GroupBy(log => new
            {
                Category = log.Category,
                GuildId = log.GuildId
            });

            foreach (var group in groupedLogs)
            {
                string fileName;
                string directory;

                if (group.Key.GuildId.HasValue)
                {
                    // Логи для конкретной гильдии
                    directory = Path.Combine(_guildLogsDirectory, group.Key.GuildId.ToString()!);
                    Directory.CreateDirectory(directory);
                    fileName = Path.Combine(directory, $"{group.Key.Category}_{DateTime.Now:yyyy-MM-dd}.json");
                }
                else
                {
                    // Общие логи по категориям
                    directory = Path.Combine(_generalLogsDirectory, group.Key.Category.ToString());
                    Directory.CreateDirectory(directory);
                    fileName = Path.Combine(directory, $"{DateTime.Now:yyyy-MM-dd}.json");
                }

                // Читаем существующие логи
                var existingLogs = new List<LogEntry>();
                if (File.Exists(fileName))
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(fileName);
                        existingLogs = JsonSerializer.Deserialize<List<LogEntry>>(json) ?? new List<LogEntry>();
                    }
                    catch
                    {
                        // Если файл поврежден, начинаем заново
                        existingLogs = new List<LogEntry>();
                    }
                }

                // Добавляем новые логи
                existingLogs.AddRange(group);

                // Сохраняем обратно
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                var jsonContent = JsonSerializer.Serialize(existingLogs, options);
                await File.WriteAllTextAsync(fileName, jsonContent);
            }
        }
        catch (Exception ex)
        {
            // В случае ошибки записи, выводим в консоль
            Console.WriteLine($"[ERROR] Failed to write logs to file: {ex.Message}");
        }
    }

    public static void Shutdown()
    {
        _flushTimer?.Dispose();
        // Финальная запись оставшихся логов
        FlushLogsToFiles(null);
    }

    /// <summary>
    /// Получить логи для конкретной гильдии
    /// </summary>
    public static async Task<List<LogEntry>> GetGuildLogsAsync(ulong guildId, LogCategory? category = null, DateTime? fromDate = null, DateTime? toDate = null)
    {
        var logs = new List<LogEntry>();
        var guildDir = Path.Combine(_guildLogsDirectory, guildId.ToString());
        
        if (!Directory.Exists(guildDir))
            return logs;

        var searchPattern = category.HasValue ? $"{category}*.json" : "*.json";
        var files = Directory.GetFiles(guildDir, searchPattern);

        foreach (var file in files)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var fileLogs = JsonSerializer.Deserialize<List<LogEntry>>(json) ?? new List<LogEntry>();
                
                // Фильтруем по датам
                if (fromDate.HasValue || toDate.HasValue)
                {
                    fileLogs = fileLogs.Where(log =>
                        (!fromDate.HasValue || log.Timestamp >= fromDate.Value) &&
                        (!toDate.HasValue || log.Timestamp <= toDate.Value)
                    ).ToList();
                }
                
                logs.AddRange(fileLogs);
            }
            catch
            {
                // Пропускаем поврежденные файлы
            }
        }

        return logs.OrderBy(log => log.Timestamp).ToList();
    }

    /// <summary>
    /// Получить логи по категории
    /// </summary>
    public static async Task<List<LogEntry>> GetCategoryLogsAsync(LogCategory category, DateTime? fromDate = null, DateTime? toDate = null)
    {
        var logs = new List<LogEntry>();
        var categoryDir = Path.Combine(_generalLogsDirectory, category.ToString());
        
        if (!Directory.Exists(categoryDir))
            return logs;

        var files = Directory.GetFiles(categoryDir, "*.json");

        foreach (var file in files)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var fileLogs = JsonSerializer.Deserialize<List<LogEntry>>(json) ?? new List<LogEntry>();
                
                // Фильтруем по датам
                if (fromDate.HasValue || toDate.HasValue)
                {
                    fileLogs = fileLogs.Where(log =>
                        (!fromDate.HasValue || log.Timestamp >= fromDate.Value) &&
                        (!toDate.HasValue || log.Timestamp <= toDate.Value)
                    ).ToList();
                }
                
                logs.AddRange(fileLogs);
            }
            catch
            {
                // Пропускаем поврежденные файлы
            }
        }

        return logs.OrderBy(log => log.Timestamp).ToList();
    }
}
