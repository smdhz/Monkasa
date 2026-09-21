using System;
using Avalonia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monkasa.Models;
using Monkasa.Services;
using Monkasa.ViewModels;
using Monkasa.Views;
using NLog;
using NLog.Extensions.Logging;

namespace Monkasa;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        LogManager.Setup().LoadConfigurationFromFile("NLog.config");
        var nlogConfiguration = LogManager.Configuration
            ?? throw new InvalidOperationException("NLog configuration was not loaded.");
        nlogConfiguration.Variables["monkasaLogFile"] = AppLogService.GetLogFilePath();

        using var host = CreateHostBuilder(args).Build();
        var logger = host.Services.GetRequiredService<ILogger<Program>>();
        try
        {
            host.Start();
            logger.LogInformation("Monkasa started. Log file: {LogFile}", AppLogService.GetLogFilePath());
            _ = host.Services.GetRequiredService<DbStorageService>().RunBackgroundCleanupAsync(
                host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
            BuildAvaloniaApp(host.Services).StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Monkasa stopped unexpectedly");
            throw;
        }
        finally
        {
            logger.LogInformation("Monkasa stopped");
            host.StopAsync().GetAwaiter().GetResult();
            LogManager.Shutdown();
        }
    }

    public static AppBuilder BuildAvaloniaApp(IServiceProvider services)
        => AppBuilder.Configure(() => new App(services))
            .UsePlatformDetect()
            .WithInterFont();

    private static IHostBuilder CreateHostBuilder(string[] args)
        => Host.CreateDefaultBuilder(args)
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace);
                logging.AddNLog();
            })
            .ConfigureServices(services =>
            {
                var databasePath = DbStorageService.GetDatabasePath();
                services.AddDbContextFactory<MonkasaDbContext>(
                    options => options.UseSqlite($"Data Source={databasePath}"));
                services.AddSingleton<AppLogService>();
                services.AddSingleton<FileSystemService>();
                services.AddSingleton<DbStorageService>();
                if (OperatingSystem.IsMacOS())
                {
                    services.AddSingleton<ISystemThumbnailProvider, MacSystemThumbnailProvider>();
                }
                else if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
                {
                    services.AddSingleton<ISystemThumbnailProvider, WindowsSystemThumbnailProvider>();
                }
                else
                {
                    services.AddSingleton<ISystemThumbnailProvider, NullSystemThumbnailProvider>();
                }
                services.AddSingleton<ThumbnailService>();
                services.AddSingleton<MainWindowViewModel>();
                services.AddSingleton<MainWindow>();
            });
}
