using Application.Interfaces;
using Application.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure;

public sealed class MusicDiagnostics(IServiceScopeFactory scopes, IConfiguration config) : IMusicDiagnostics
{
    public async Task<IReadOnlyList<DiagnosticCheck>> CheckAsync()
    {
        List<DiagnosticCheck> checks = new();
        foreach (string name in new[] { "ffmpeg.exe", "yt-dlp.exe", "opus.dll", "libdave.dll" })
        {
            bool exists = File.Exists(Kvasir.Configuration.ApplicationPaths.RuntimeFile(name));
            checks.Add(new(name, exists, exists ? "найден" : "отсутствует: поместите файл в appdata"));
        }
        using var scope = scopes.CreateScope();
        try { bool connected = await scope.ServiceProvider.GetRequiredService<DiscordMusicDBContext>().Database.CanConnectAsync(); checks.Add(new("SQLite", connected, connected ? "соединение установлено" : "проверьте файл базы и connection_string")); }
        catch (Exception) { checks.Add(new("SQLite", false, "база недоступна; проверьте права и connection_string")); }
        string folder = Path.GetFullPath(config["music_client:music_folder"] ?? "music");
        string probe = Path.Combine(folder, Guid.NewGuid() + ".probe");
        try { Directory.CreateDirectory(folder); await File.WriteAllTextAsync(probe, ""); File.Delete(probe); checks.Add(new("Папка музыки", true, "чтение и запись доступны")); }
        catch (Exception) { checks.Add(new("Папка музыки", false, "нет доступа к записи; проверьте права и свободное место")); }
        return checks;
    }
}
