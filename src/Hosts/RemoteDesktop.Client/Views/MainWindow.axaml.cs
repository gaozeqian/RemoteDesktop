using Avalonia.Controls;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Client.ViewModels;

namespace RemoteDesktop.Client.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.RemoteDesktopOpened += (_, desktop) => new RemoteDesktopWindow { DataContext = desktop }.Show();

            viewModel.StandaloneFileManagerOpened += (_, item) =>
            {
                var (session, files) = item;
                var window = new FileManagerWindow { DataContext = files };
                window.Closed += (_, _) => CloseStandalone(files.DisposeAsync, session);
                window.Show();
            };

            viewModel.StandaloneTerminalOpened += (_, item) =>
            {
                var (session, terminal) = item;
                var window = new TerminalWindow { DataContext = terminal };
                window.Closed += (_, _) => CloseStandalone(terminal.DisposeAsync, session);
                window.Show();
            };

            viewModel.StandaloneCameraOpened += (_, item) =>
            {
                var (session, camera) = item;
                var window = new CameraWindow { DataContext = camera };
                window.Closed += (_, _) => CloseStandalone(camera.DisposeAsync, session);
                window.Show();
            };
        }
    }

    private static void CloseStandalone(Func<ValueTask> disposeFeature, RemoteSession session) =>
        _ = CloseStandaloneAsync(disposeFeature, session);

    /// <summary>
    /// Tears down the feature and then the session behind a standalone window. Neither step may escape onto the UI
    /// thread, and the session must be closed even when the feature refuses to shut down: otherwise the host keeps a
    /// connection this window was the only way to close.
    /// </summary>
    private static async Task CloseStandaloneAsync(Func<ValueTask> disposeFeature, RemoteSession session)
    {
        try
        {
            await disposeFeature();
        }
        catch (Exception)
        {
            // The window is gone; there is nothing left to report this to.
        }

        try
        {
            await session.DisposeAsync();
        }
        catch (Exception)
        {
            // The session is being abandoned either way.
        }
    }
}
