using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using System.Text.Json;

namespace Infrastructure;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DiscordMusicDBContext>
{
    public DiscordMusicDBContext CreateDbContext(string[] args) {
        var basePath = Kvasir.Configuration.ApplicationPaths.DataDirectory;
        var configPath = Kvasir.Configuration.ApplicationPaths.ConfigurationFile;

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
