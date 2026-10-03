using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Client.ViewModels;
using RemoteDesktop.Client.Views;

namespace RemoteDesktop.Client;

public sealed partial class App(IServiceProvider services) : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>() };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
