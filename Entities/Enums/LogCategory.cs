namespace Entities.Enums;

/// <summary>
/// Категории логов для разделения по типам операций
/// </summary>
public enum LogCategory
{
    /// <summary>
    /// Общие логи
    /// </summary>
    General,
    
    /// <summary>
    /// Логи базы данных
    /// </summary>
    Database,
    
    /// <summary>
    /// Логи сервисов
    /// </summary>
    Service,
    
    /// <summary>
    /// Логи музыкального клиента
    /// </summary>
    Music,
    
    /// <summary>
    /// Логи Discord API
    /// </summary>
    Discord,
    
    /// <summary>
    /// Логи обработчиков команд
    /// </summary>
    Command,
    
    /// <summary>
    /// Логи обработчиков компонентов
    /// </summary>
    Component,
    
    /// <summary>
    /// Логи обработчиков событий
    /// </summary>
    Event,
    
    /// <summary>
    /// Логи загрузки аудио
    /// </summary>
    AudioDownload,
    
    /// <summary>
    /// Логи поиска видео
    /// </summary>
    VideoFinder,
    
    /// <summary>
    /// Логи FFmpeg
    /// </summary>
    FFmpeg
}

