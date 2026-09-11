using Application.Interfaces;
using Application.Services;
using CS_Discord_Bot.Connectors;
using Connectors.Media;
using Entities.Enums;
using Infrastructure;
using Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services;
using NetCord.Hosting.Services.ApplicationCommands;
using NetCord.Hosting.Services.ComponentInteractions;
using System.Diagnostics;

namespace CS_Discord_Bot;

public class Program
{

    public static async Task Main(string[] args)
    {
        IHost host = CreateHostBuilder(args).Build();

        IConfiguration configuration = host.Services.GetRequiredService<IConfiguration>();
        Logger.Initialize(configuration);

        // Register command modules
        RegisterCommands(host);

        AppDomain.CurrentDomain.ProcessExit += (sender, e) => OnProcessExit(host.Services);

        await host.RunAsync();
    }

    private static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((context, config) =>
            {
                config.SetBasePath(Directory.GetCurrentDirectory())
                      .AddJsonFile("appdata\\configuration.json", optional: false, reloadOnChange: true);
            })
            .ConfigureLogging(logging =>
            {
                logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.None);
                logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None);

                // Disable NetCord logging to reduce overhead
                logging.AddFilter("NetCord", LogLevel.Warning);
                logging.AddFilter("NetCord.Gateway", LogLevel.Warning);
                logging.AddFilter("NetCord.Gateway.Voice", LogLevel.Warning);
                logging.AddFilter("NetCord.Rest", LogLevel.Warning);
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .ConfigureServices((context, services) =>
            {
                IConfiguration configuration = context.Configuration;

                ConfigureDiscordServices(services, configuration);
                ConfigureDatabaseServices(services, configuration);
                ConfigureApplicationServices(services);
            })
            .UseConsoleLifetime();

    private static void ConfigureDiscordServices(IServiceCollection services, IConfiguration configuration)
    {
        string? token = configuration["KVASIR_DISCORD_TOKEN"];

        // Backward compatibility for existing local configurations.
        if (string.IsNullOrWhiteSpace(token))
        {
            string[]? tokens = configuration.GetSection("tokens").Get<string[]>();
            token = tokens?.FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Discord bot token is required. Set the KVASIR_DISCORD_TOKEN environment variable.");

        // NetCord configuration
        services
            .AddDiscordGateway(options =>
            {
                // Set token from configuration
                options.Token = token;

                // Configure intents as needed
                options.Intents = GatewayIntents.Guilds
                              | GatewayIntents.GuildMessages
                              | GatewayIntents.DirectMessages
                              | GatewayIntents.MessageContent
                              | GatewayIntents.GuildVoiceStates
                              | GatewayIntents.GuildMessageReactions
                              | GatewayIntents.DirectMessageReactions;
            })
            .AddApplicationCommands()
            .AddComponentInteractions<NetCord.ButtonInteraction, NetCord.Services.ComponentInteractions.ButtonInteractionContext>()
            .AddComponentInteractions<NetCord.StringMenuInteraction, NetCord.Services.ComponentInteractions.StringMenuInteractionContext>()
            .AddComponentInteractions<NetCord.UserMenuInteraction, NetCord.Services.ComponentInteractions.UserMenuInteractionContext>()
            .AddComponentInteractions<NetCord.RoleMenuInteraction, NetCord.Services.ComponentInteractions.RoleMenuInteractionContext>()
            .AddComponentInteractions<NetCord.MentionableMenuInteraction, NetCord.Services.ComponentInteractions.MentionableMenuInteractionContext>()
            .AddComponentInteractions<NetCord.ChannelMenuInteraction, NetCord.Services.ComponentInteractions.ChannelMenuInteractionContext>()
            .AddComponentInteractions<NetCord.ModalInteraction, NetCord.Services.ComponentInteractions.ModalInteractionContext>()
            .AddGatewayHandlers(typeof(Program).Assembly);

        // RestClient is automatically registered by AddDiscordGateway
        // GatewayClient is automatically registered by AddDiscordGateway
    }

    private static void RegisterCommands(IHost host)
    {
        // Register command modules from assembly
        host.AddModules(typeof(Program).Assembly);
    }

    private static void ConfigureDatabaseServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<DiscordMusicDBContext>(options =>
        {
            var connectionString = configuration["connection_string"];
            if (string.IsNullOrEmpty(connectionString))
                return;

            options.UseSqlite(connectionString);

            if (
                Enum.TryParse(configuration["logging:dbLogLevel"]!, ignoreCase: true, out LogLevel dbLogLevel) &&
                dbLogLevel != LogLevel.None
            )
            {
                options.UseLoggerFactory(LoggerFactory.Create(builder =>
                {
                    builder.AddProvider(new EfCoreLoggerProvider());
                    builder.SetMinimumLevel(dbLogLevel);
                }));
            }
        });
    }

    private static void ConfigureApplicationServices(IServiceCollection services)
    {
        services.AddScoped<IGuildRepository, Infrastructure.Repository.GuildRepository>();
        services.AddScoped<ISongRepository, Infrastructure.Repository.SongRepository>();
        services.AddScoped<IPlaylistRepository, Infrastructure.Repository.PlaylistRepository>();
        services.AddSingleton<IBotConnector, NetCordBotConnector>();

        services.AddTransient<IAudioDownloaderService, AudioDownloaderService>();
        services.AddTransient<IVideoFinderService, VideoFinderService>();

        services.AddSingleton<GuildService>();
    }

    public static void RestartApplication()
    {
        Process.Start(Process.GetCurrentProcess().MainModule!.FileName);
        Environment.Exit(0);
    }

    private static async void OnProcessExit(IServiceProvider serviceProvider)
    {
        try
        {
            GuildService guildService = serviceProvider.GetRequiredService<GuildService>();
            await guildService.DisposeAsync();

            // DSharpPlus cleanup is handled by DSharpPlusBotService.StopAsync
            // No additional cleanup needed here

            Logger.Shutdown();
        }
        catch (Exception ex)
        {
            await Logger.AddLog($"Error during process exit: {ex.Message}", LogCategory.General, Microsoft.Extensions.Logging.LogLevel.Error, exception: ex);
        }
    }
}
