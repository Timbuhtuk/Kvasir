namespace Entities.Enums;

/// <summary>
/// Log categories for separating by operation types
/// </summary>
public enum LogCategory
{
    /// <summary>
    /// General logs
    /// </summary>
    General,
    
    /// <summary>
    /// Database logs
    /// </summary>
    Database,
    
    /// <summary>
    /// Service logs
    /// </summary>
    Service,
    
    /// <summary>
    /// Music client logs
    /// </summary>
    Music,
    
    /// <summary>
    /// Discord API logs
    /// </summary>
    Discord,
    
    /// <summary>
    /// Command handler logs
    /// </summary>
    Command,
    
    /// <summary>
    /// Component handler logs
    /// </summary>
    Component,
    
    /// <summary>
    /// Event handler logs
    /// </summary>
    Event,
    
    /// <summary>
    /// Audio download logs
    /// </summary>
    AudioDownload,
    
    /// <summary>
    /// Video finder logs
    /// </summary>
    VideoFinder,
    
    /// <summary>
    /// FFmpeg logs
    /// </summary>
    FFmpeg
}
