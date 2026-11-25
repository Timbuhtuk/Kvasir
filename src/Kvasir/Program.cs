using Kvasir.Media;
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
using Kvasir.Configuration;

namespace CS_Discord_Bot;

public class Program
{

    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;
        ApplicationPaths.EnsureConfiguration();
        Directory.SetCurrentDirectory(ApplicationPaths.DataDirectory);
        IHost host;
        try { host = CreateHostBuilder(args, ApplicationPaths.ConfigurationFile).Build(); }
        catch (MissingDiscordTokenException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 2;
            return;
        }

        IConfiguration configuration = host.Services.GetRequiredService<IConfiguration>();
        Logger.Initialize(configuration);
        await host.Services.GetRequiredService<ISoundboardLibrary>().InitializeAsync();

        using (IServiceScope scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<DiscordMusicDBContext>().Database.MigrateAsync();
            IReadOnlyList<Application.Models.DiagnosticCheck> checks = await scope.ServiceProvider.GetRequiredService<IMusicDiagnostics>().CheckAsync();
            foreach (var check in checks.Where(c => !c.Healthy))
                await Logger.AddLog($"Startup: {check.Name}: {check.Detail}", LogLevel.Warning);
        }

        // Register command modules
        RegisterCommands(host);

        try
        {
            await host.StartAsync();
            await WaitForShutdownAsync(host);
        }
        finally
        {
            await ((IAsyncDisposable)host).DisposeAsync();
            Logger.Shutdown();
        }
    }

    private static async Task WaitForShutdownAsync(IHost host)
    {
        Task applicationStopped = host.WaitForShutdownAsync();
        if (Environment.GetEnvironmentVariable("KVASIR_CONTROL_STDIN") != "1")
        {
            await applicationStopped;
            return;
        }

        while (!applicationStopped.IsCompleted)
        {
            Task<string?> commandReceived = Console.In.ReadLineAsync();
            if (await Task.WhenAny(applicationStopped, commandReceived) == applicationStopped) break;
            string? command = await commandReceived;
            // EOF means the controlling desktop process has closed its input pipe.
            if (command == null || string.Equals(command, "stop", StringComparison.OrdinalIgnoreCase))
            {
                // WaitForShutdownAsync owns StopAsync. Request shutdown once;
                // stopping here as well closes the Discord gateway twice.
                host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
                break;
            }
        }

        await applicationStopped;
    }

    internal static IHostBuilder CreateHostBuilder(string[] args, string configurationFile) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((context, config) =>
            {
                config.AddJsonFile(configurationFile, optional: false, reloadOnChange: true);
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
        string[]? tokens = configuration.GetSection("tokens").Get<string[]>();
        string? token = tokens?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token)) token = configuration["KVASIR_DISCORD_TOKEN"];

        if (string.IsNullOrWhiteSpace(token))
            throw new MissingDiscordTokenException();

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

    internal static void RegisterCommands(IHost host)
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

        services.AddSingleton<MediaActivity>();
        services.AddTransient<IAudioDownloaderService, SongAudioDownloader>();
        services.AddTransient<IVideoFinderService, VideoFinderService>();

        services.AddSingleton<GuildService>();
        services.AddSingleton<IMusicPreferences, MusicPreferences>();
        services.AddSingleton<IMediaCache, MediaCacheService>();
        services.AddSingleton<TrackService>();
        services.AddSingleton<PlaylistImportService>();
        services.AddSingleton<IMusicLibrary, MusicLibraryService>();
        services.AddSingleton<IMusicDiagnostics, MusicDiagnostics>();
        services.AddSingleton<IAudioNormalizer, AudioNormalizer>();
        services.AddSingleton<ISoundEffectDecoder, SoundEffectDecoder>();
        services.AddSingleton<ISoundboardLibrary, SoundboardLibrary>();
        services.AddSingleton<CS_Discord_Bot.Commands.MusicPanelActions>();
        services.AddHostedService<CS_Discord_Bot.DesktopControl.DesktopControlService>();
        services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromMinutes(5));
    }

    private sealed class MissingDiscordTokenException : InvalidOperationException
    {
        public MissingDiscordTokenException() : base($"Не указан Discord-токен. Заполните массив tokens в {ApplicationPaths.ConfigurationFile}.")
        {
        }
    }

    public static void RestartApplication()
    {
        Process.Start(Process.GetCurrentProcess().MainModule!.FileName);
        Environment.Exit(0);
    }

}
