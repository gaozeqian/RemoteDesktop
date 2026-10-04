using Avalonia.Controls;
using RemoteDesktop.Client.ViewModels;

namespace RemoteDesktop.Client.Views;

/// <summary>Tabs of remote shells. Closing the last tab closes the window.</summary>
public sealed partial class TerminalWindow : Window
{
    public TerminalWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is TerminalWindowViewModel viewModel)
        {
            viewModel.Empty += (_, _) => Close();
        }
    }
}
