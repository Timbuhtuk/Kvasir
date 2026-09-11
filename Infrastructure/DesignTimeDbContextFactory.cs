using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using System.Text.Json;

namespace Infrastructure;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DiscordMusicDBContext>
{
    public DiscordMusicDBContext CreateDbContext(string[] args) {
        // Try to find configuration.json in multiple locations
        var basePath = Directory.GetCurrentDirectory();
        var configPath = Path.Combine(basePath, "appdata", "configuration.json");

        // If not found, try from Kvasir folder
        if (!File.Exists(configPath)) {
            var kvasirPath = Path.Combine(basePath, "..", "Kvasir", "appdata", "configuration.json");
            if (File.Exists(kvasirPath)) {
                basePath = Path.Combine(basePath, "..", "Kvasir");
                configPath = kvasirPath;
            }
        }

        using FileStream configurationStream = File.OpenRead(Path.GetFullPath(configPath));
        using JsonDocument configuration = JsonDocument.Parse(configurationStream);
        string? connectionString = configuration.RootElement
            .GetProperty("connection_string")
            .GetString();

        // Resolve connection string to absolute path
        if (connectionString != null && connectionString.Contains("Data Source=")) {
            var dataSourcePart = connectionString.Substring(connectionString.IndexOf("Data Source=") + "Data Source=".Length);
            if (!Path.IsPathRooted(dataSourcePart)) {
                // Convert relative path to absolute
                var absolutePath = Path.GetFullPath(Path.Combine(basePath, dataSourcePart));
                var directory = Path.GetDirectoryName(absolutePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) {
                    Directory.CreateDirectory(directory);
                }
                connectionString = connectionString.Replace(dataSourcePart, absolutePath);
            }
        }

        DbContextOptionsBuilder<DiscordMusicDBContext> optionsBuilder = new();
        optionsBuilder.UseSqlite(connectionString);

        return new DiscordMusicDBContext(optionsBuilder.Options);
    }
}
