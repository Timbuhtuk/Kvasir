namespace Logging;

/// <summary>
/// Контекст логирования для текущего async потока
/// </summary>
public static class LogContext
{
    private static readonly AsyncLocal<ulong?> _guildId = new();
    private static readonly AsyncLocal<string?> _guildName = new();

    /// <summary>
    /// ID текущей гильдии в контексте выполнения
    /// </summary>
    public static ulong? GuildId
    {
        get => _guildId.Value;
        set => _guildId.Value = value;
    }

    /// <summary>
    /// Имя текущей гильдии в контексте выполнения
    /// </summary>
    public static string? GuildName
    {
        get => _guildName.Value;
        set => _guildName.Value = value;
    }

    /// <summary>
    /// Установить контекст гильдии
    /// </summary>
    public static void SetGuild(ulong? guildId, string? guildName = null)
    {
        GuildId = guildId;
        GuildName = guildName;
    }

    /// <summary>
    /// Очистить контекст гильдии
    /// </summary>
    public static void Clear()
    {
        GuildId = null;
        GuildName = null;
    }
}

