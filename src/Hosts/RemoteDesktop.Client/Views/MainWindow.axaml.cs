using Avalonia.Controls;
using Avalonia.Input.Platform;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Client.ViewModels;

namespace RemoteDesktop.Client.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        CopyDeviceCodeButton.Click += async (_, _) => await CopyAsync(static viewModel => viewModel.LocalDeviceId);
        CopyPasswordButton.Click += async (_, _) => await CopyAsync(static viewModel => viewModel.TemporaryPassword);
        CopySelectedDeviceCodeButton.Click += async (_, _) =>
            await CopyAsync(static viewModel => viewModel.SelectedRecentDevice?.FormattedId ?? string.Empty);
        CopyDataDirectoryButton.Click += async (_, _) => await CopyAsync(static viewModel => viewModel.DataDirectory);
        CopyClientDeviceIdButton.Click += async (_, _) => await CopyAsync(static viewModel => viewModel.ClientDeviceId);
    }

    /// <summary>
    /// Copies through the window's clipboard, not through the session's <c>IClipboard</c> port: that one mirrors the
    /// clipboard to the peer, so reading the device code into it would ship the code to the controlled machine.
    /// </summary>
    private async Task CopyAsync(Func<MainWindowViewModel, string> select)
    {
        if (DataContext is not MainWindowViewModel viewModel || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(select(viewModel));
        }
        catch (Exception)
        {
            // Another process can hold the clipboard open; a copy that did not happen is not worth reporting twice.
        }
    }

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
