using Application.Interfaces;
using Application.Services;
using CS_Discord_Bot.Commands;
using CS_Discord_Bot.Handlers;
using CS_Discord_Bot.music_parts;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Entities.Enums;
using Infrastructure;
using Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using EventHandler = CS_Discord_Bot.Handlers.EventHandler;
using LogLevel = Entities.Enums.LogLevel;

namespace CS_Discord_Bot;

public class Program
{
    public static async Task Main(string[] args)
    {
        IHost host = CreateHostBuilder(args).Build();

        // Инициализируем Logger с конфигурацией
        IConfiguration configuration = host.Services.GetRequiredService<IConfiguration>();
        Logger.Initialize(configuration);

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
                // Отключаем стандартное логирование EF Core, так как используем наш кастомный логгер
                logging.AddFilter("Microsoft.EntityFrameworkCore", Microsoft.Extensions.Logging.LogLevel.None);
                logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", Microsoft.Extensions.Logging.LogLevel.None);
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
        services.AddSingleton<DiscordSocketClient>(provider =>
        {
            DiscordSocketConfig config = new()
            {
                GatewayIntents = GatewayIntents.Guilds |
                                 GatewayIntents.GuildMessages |
                                 GatewayIntents.GuildVoiceStates |
                                 GatewayIntents.MessageContent,
                MessageCacheSize = 1000
            };

            DiscordSocketClient client = new(config);
            client.Log += Logger.AddLog;
            return client;
        });

        services.AddSingleton<CommandService>(provider =>
        {
            CommandService commands = new();
            commands.Log += Logger.AddLog;
            return commands;
        });
    }

    private static void ConfigureDatabaseServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<DiscordMusicDBContext>(options =>
        {
            options.UseSqlServer(configuration["connection_string"]);

            // Используем наш кастомный логгер для EF Core
            options.UseLoggerFactory(LoggerFactory.Create(builder =>
            {
                builder.AddProvider(new Logging.EfCoreLoggerProvider());
                // Устанавливаем минимальный уровень логирования
                builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
            }));
        });
    }

    private static void ConfigureApplicationServices(IServiceCollection services)
    {

        // Регистрируем репозитории
        services.AddScoped<IGuildRepository, Infrastructure.Repository.GuildRepository>();
        services.AddScoped<ISongRepository, Infrastructure.Repository.SongRepository>();
        services.AddScoped<IPlaylistRepository, Infrastructure.Repository.PlaylistRepository>();

        // Регистрируем сервисы
        services.AddSingleton<IAudioDownloaderService, AudioDownloaderService>();


        // MusicClient и MusicView - scoped сервисы, создаются через scope в GuildService
        services.AddScoped<MusicClient>();
        services.AddScoped<MusicView>();

        services.AddSingleton<MusicCommands>();
        services.AddSingleton<GuildService>();
        services.AddSingleton<VideoFinderService>();
        services.AddSingleton<CommandHandler>();
        services.AddSingleton<ComponentHandler>();
        services.AddSingleton<EventHandler>();
        services.AddHostedService<DiscordBotService>();

        //// Регистрируем IConfigurationRoot для обратной совместимости
        //services.AddSingleton<IConfigurationRoot>(provider =>
        //    (IConfigurationRoot)provider.GetRequiredService<IConfiguration>());
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

            DiscordSocketClient client = serviceProvider.GetRequiredService<DiscordSocketClient>();
            await client.StopAsync();
            client.Dispose();

            // Завершаем работу логгера
            Logger.Shutdown();
        }
        catch (Exception ex)
        {
            await Logger.AddLog($"Error during process exit: {ex.Message}", LogCategory.General, LogLevel.ERROR, exception: ex);
        }
    }
}
