using System;
using Avalonia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monkasa.Models;
using Monkasa.Services;
using Monkasa.ViewModels;
using Monkasa.Views;

namespace Monkasa;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var host = CreateHostBuilder(args).Build();
        host.Start();
        _ = host.Services.GetRequiredService<SqliteThumbnailCacheStore>();

        try
        {
            BuildAvaloniaApp(host.Services).StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            host.StopAsync().GetAwaiter().GetResult();
        }
    }

    public static AppBuilder BuildAvaloniaApp(IServiceProvider services)
        => AppBuilder.Configure(() => new App(services))
            .UsePlatformDetect()
            .WithInterFont();

    private static IHostBuilder CreateHostBuilder(string[] args)
        => Host.CreateDefaultBuilder(args)
            .ConfigureServices(services =>
            {
                var databasePath = SqliteThumbnailCacheStore.GetDatabasePath();
                services.AddDbContextFactory<MonkasaDbContext>(
                    options => options.UseSqlite($"Data Source={databasePath}"));
                services.AddSingleton<IFileSystemService, FileSystemService>();
                services.AddSingleton<SqliteThumbnailCacheStore>();
                services.AddSingleton<IThumbnailService, ThumbnailService>();
                services.AddSingleton<MainWindowViewModel>();
                services.AddSingleton<MainWindow>();
            });
}
