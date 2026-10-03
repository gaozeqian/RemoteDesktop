using Avalonia.Controls;
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
        }
    }
}
