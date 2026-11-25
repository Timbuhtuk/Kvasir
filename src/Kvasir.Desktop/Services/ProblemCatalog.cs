using Kvasir.Desktop.Localization;
namespace Kvasir.Desktop.Services;

public enum ProblemAction { None, Token, Settings, Channel, Diagnostics }
public sealed record DesktopProblem(string Code, string Title, string Cause, string Recovery, ProblemAction Action = ProblemAction.None, string? ActionText = null);

public static class ProblemCatalog
{
    public static DesktopProblem Describe(string text, string? code = null)
    {
        bool Has(params string[] terms) => terms.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
        if (code is null or "playback")
        {
            string fallback = code ?? "unexpected";
            if (Has("401: Unauthorized", "401 (Unauthorized)", "Authentication failed", "4004", "invalid token", "401 Unauthorized")) code = "token-invalid";
            else if (Has("4014", "Disallowed intent", "privileged intent")) code = "intents";
            else if (Has("SQLite", "SqliteException", "database is locked")) code = "database";
            else if (Has("JsonException", "JSON", "конфигураци")) code = "configuration";
            else if (Has("DllNotFound", "Unable to load DLL") || (Has("FileNotFound", "Win32Exception", "not found", "не найден") && Has("ffmpeg", "yt-dlp", "opus.dll", "libdave.dll"))) code = "dependency";
            else if (Has("Выберите голосовой канал", "Сначала войдите в голосовой", "Голосовой канал изменился")) code = "voice-channel";
            else if (Has("FFmpeg не завершил обработку")) code = "conversion-timeout";
            else if (Has("Трек не найден", "источник недоступен", "Источник аудио недоступен", "неполный аудиофайл", "download", "загрузить", "video unavailable", "sign in", "audio stream")) code = "source";
            else if (Has("Missing Permissions", "Missing Access", "50013", "50001", "роль DJ") || (Has("403") && Has("NetCord", "Discord"))) code = "permissions";
            else if (Has("Timeout", "timed out", "TaskCanceledException", "Связь", "Ответ не получен", "HttpRequestException", "SocketException", "No such host", "NameResolution", "pipe", "Поток закрыт")) code = "connection";
            else if (Has("UnauthorizedAccessException", "Access to the path", "доступ к записи", "disk full", "нет места", "IOException")) code = "storage";
            else if (Has("выберите трек", "Выберите плейлист", "ничего не играет", "начала воспроизведения", "Позиция", "Укажите", "Заполните", "Лимит", "исчерпан")) code = "input";
            else code = fallback;
        }
        return code switch
        {
            "token-missing" => new(code, UiText.Get("TokenMissingTitle"), UiText.Get("TokenMissingCause"), UiText.Get("TokenMissingRecovery"), ProblemAction.Token, UiText.Get("EnterToken")),
            "token-invalid" => new(code, UiText.Get("TokenInvalidTitle"), UiText.Get("TokenInvalidCause"), UiText.Get("TokenInvalidRecovery"), ProblemAction.Token, UiText.Get("ReplaceToken")),
            "configuration" => new(code, UiText.Get("ConfigurationErrorTitle"), UiText.Get("ConfigurationErrorCause"), UiText.Get("ConfigurationErrorRecovery"), ProblemAction.Settings, UiText.Get("OpenSettings")),
            "dependency" => new(code, UiText.Get("DependencyErrorTitle"), UiText.Get("DependencyErrorCause"), UiText.Get("DependencyErrorRecovery"), ProblemAction.Diagnostics, UiText.Get("OpenService")),
            "voice-channel" => new(code, UiText.Get("VoiceChannelMissingTitle"), UiText.Get("VoiceChannelMissingCause"), UiText.Get("VoiceChannelMissingRecovery"), ProblemAction.Channel, UiText.Get("ChooseChannel")),
            "permissions" => new(code, UiText.Get("PermissionsErrorTitle"), UiText.Get("PermissionsErrorCause"), UiText.Get("PermissionsErrorRecovery")),
            "storage" => new(code, UiText.Get("StorageErrorTitle"), UiText.Get("StorageErrorCause"), UiText.Get("StorageErrorRecovery"), ProblemAction.Settings, UiText.Get("OpenSettings")),
            "database" => new(code, UiText.Get("DatabaseErrorTitle"), UiText.Get("DatabaseErrorCause"), UiText.Get("DatabaseErrorRecovery"), ProblemAction.Diagnostics, UiText.Get("OpenService")),
            "source" => new(code, UiText.Get("SourceErrorTitle"), UiText.Get("SourceErrorCause"), UiText.Get("SourceErrorRecovery"), ProblemAction.Diagnostics, UiText.Get("OpenService")),
            "sound-import" => new(code, UiText.Get("EffectImportErrorTitle"), text.Trim()[..Math.Min(text.Trim().Length, 500)], UiText.Get("EffectImportErrorRecovery")),
            "soundboard" => new(code, UiText.Get("SoundboardErrorTitle"), text.Trim()[..Math.Min(text.Trim().Length, 500)], UiText.Get("SoundboardErrorRecovery")),
            "source-timeout" => new(code, UiText.Get("SourceTimeoutTitle"), UiText.Get("SourceTimeoutCause"), UiText.Get("SourceTimeoutRecovery")),
            "conversion-timeout" => new(code, UiText.Get("ConversionTimeoutTitle"), UiText.Get("ConversionTimeoutCause"), UiText.Get("ConversionTimeoutRecovery"), ProblemAction.Diagnostics, UiText.Get("OpenService")),
            "source-restricted" => new(code, UiText.Get("SourceRestrictedTitle"), UiText.Get("SourceRestrictedCause"), UiText.Get("SourceRestrictedRecovery")),
            "connection" => new(code, UiText.Get("ConnectionErrorTitle"), UiText.Get("ConnectionErrorCause"), UiText.Get("ConnectionErrorRecovery")),
            "intents" => new(code, UiText.Get("IntentsErrorTitle"), UiText.Get("IntentsErrorCause"), UiText.Get("IntentsErrorRecovery")),
            "input" => new(code, UiText.Get("InputErrorTitle"), text, UiText.Get("InputErrorRecovery")),
            "playback" => new(code, UiText.Get("PlaybackErrorTitle"), UiText.Get("PlaybackErrorCause"), UiText.Get("PlaybackErrorRecovery"), ProblemAction.Diagnostics, UiText.Get("OpenService")),
            "import-partial" => new(code, UiText.Get("ImportPartialTitle"), text, UiText.Get("ImportPartialRecovery")),
            "process-exit" => new(code, UiText.Get("ProcessExitTitle"), UiText.Get("ProcessExitCause"), UiText.Get("ProcessExitRecovery")),
            _ => new("unexpected", UiText.Get("UnexpectedErrorTitle"), string.IsNullOrWhiteSpace(text) ? UiText.Get("UnexpectedErrorCause") : text.Trim()[..Math.Min(text.Trim().Length, 400)], UiText.Get("UnexpectedErrorRecovery")),
        };
    }
}
