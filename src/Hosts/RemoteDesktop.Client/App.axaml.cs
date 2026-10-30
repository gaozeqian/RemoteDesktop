using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RemoteDesktop.Client.ViewModels;
using RemoteDesktop.Client.Views;

namespace RemoteDesktop.Client;

public sealed partial class App(IServiceProvider services) : Avalonia.Application
{
    private IDisposable? _linkListener;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            // Opening a link while this window is open starts a second process, which writes the link into this one's
            // pipe and exits; without that, the two clients would fight over the same controller identity. Only one
            // client can own the pipe — the others get null and simply never receive links.
            _linkListener = LinkActivation.TryListen(link => Dispatcher.UIThread.Post(() =>
            {
                viewModel.ApplyConnectionLink(link);
                desktop.MainWindow?.Activate();
            }));

            desktop.Exit += (_, _) =>
            {
                _linkListener?.Dispose();
                _linkListener = null;
            };

            // The host owns the process's quit signals: the console lifetime stops the application when the
            // session asks everything to quit — a logout, `systemctl --user stop`, or a plain SIGTERM. Avalonia
            // has an event loop of its own and would otherwise keep the process alive with a stopped host
            // behind it. A registration made after the signal has already arrived runs immediately, so this also
            // covers a quit that lands while the window is being built.
            services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(
                () => Dispatcher.UIThread.Post(() => desktop.Shutdown()));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
