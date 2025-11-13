using Entities.Enums;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using EFCoreLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Logging;

/// <summary>
/// Провайдер логгера для Entity Framework Core, который использует нашу систему логирования
/// </summary>
public class EfCoreLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName)
    {
        // Проверяем, что это логи от EF Core
        if (categoryName.StartsWith("Microsoft.EntityFrameworkCore"))
        {
            return new EfCoreLogger(categoryName);
        }
        // Для других категорий возвращаем пустой логгер
        return new NullLogger();
    }

    public void Dispose() { }
}

/// <summary>
/// Логгер для Entity Framework Core, который перенаправляет логи в нашу систему логирования
/// </summary>
public class EfCoreLogger : ILogger
{
    private readonly string _categoryName;

    public EfCoreLogger(string categoryName)
    {
        _categoryName = categoryName;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(EFCoreLogLevel logLevel)
    {
        // Проверяем уровень логирования нашей системы
        int ourLevel = logLevel switch
        {
            EFCoreLogLevel.Error or EFCoreLogLevel.Critical => 1,
            EFCoreLogLevel.Warning => 2,
            _ => 3
        };
        return Logger.LoggingLevel >= ourLevel;
    }

    public void Log<TState>(EFCoreLogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        string message = formatter(state, exception);
        
        // Преобразуем LogLevel из Microsoft.Extensions.Logging в наш LogLevel
        Entities.Enums.LogLevel ourLogLevel = logLevel switch
        {
            EFCoreLogLevel.Error or EFCoreLogLevel.Critical => Entities.Enums.LogLevel.ERROR,
            EFCoreLogLevel.Warning => Entities.Enums.LogLevel.WARNING,
            _ => Entities.Enums.LogLevel.INFO
        };

        // Используем нашу систему логирования с категорией Database
        // guildId и guildName автоматически возьмутся из LogContext, если они там установлены
        // Fire-and-forget паттерн, чтобы не блокировать выполнение EF Core
        _ = Task.Run(async () =>
        {
            try
            {
                // Пытаемся извлечь guildId из SQL-запроса, если он есть в параметрах
                ulong? guildIdFromQuery = ExtractGuildIdFromMessage(message);
                
                await Logger.AddLog(message, LogCategory.Database, ourLogLevel, 
                    guildId: guildIdFromQuery ?? LogContext.GuildId, 
                    guildName: LogContext.GuildName, 
                    exception: exception);
            }
            catch
            {
                // Игнорируем ошибки логирования, чтобы не нарушать работу EF Core
            }
        });
    }

    /// <summary>
    /// Пытается извлечь guildId из SQL-запроса, если он содержит параметр Discord_id
    /// </summary>
    private static ulong? ExtractGuildIdFromMessage(string message)
    {
        try
        {
            // Ищем паттерн типа @__discordId_0='?' или Discord_id = @__discordId_0
            // Это типичный паттерн для EF Core параметров
            if (message.Contains("Discord_id") || message.Contains("discordId"))
            {
                // Пытаемся найти значение в параметрах
                // Формат: [Parameters=[@__discordId_0='123456789' (Precision = 20)]
                var match = System.Text.RegularExpressions.Regex.Match(
                    message, 
                    @"@__discordId_\d+='(\d+)'",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                
                if (match.Success && match.Groups.Count > 1)
                {
                    if (ulong.TryParse(match.Groups[1].Value, out ulong guildId))
                    {
                        return guildId;
                    }
                }
            }
        }
        catch
        {
            // Игнорируем ошибки парсинга
        }
        
        return null;
    }
}

/// <summary>
/// Пустой логгер для категорий, которые мы не обрабатываем
/// </summary>
public class NullLogger : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(EFCoreLogLevel logLevel) => false;
    public void Log<TState>(EFCoreLogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}

