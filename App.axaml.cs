using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using System;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Monkasa.Views;

namespace Monkasa;

public partial class App : Application
{
    private readonly IServiceProvider? _services;

    public App()
    {
    }

    public App(IServiceProvider services)
    {
        _services = services;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = _services?.GetService<MainWindow>() ?? new MainWindow();
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
