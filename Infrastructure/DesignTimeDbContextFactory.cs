using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Infrastructure;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DiscordMusicDBContext>
{
    public DiscordMusicDBContext CreateDbContext(string[] args)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appdata\\configuration.json", optional: false)
            .Build();

        DbContextOptionsBuilder<DiscordMusicDBContext> optionsBuilder = new();
        optionsBuilder.UseSqlServer(configuration["connection_string"]);

        return new DiscordMusicDBContext(optionsBuilder.Options);
    }
}

