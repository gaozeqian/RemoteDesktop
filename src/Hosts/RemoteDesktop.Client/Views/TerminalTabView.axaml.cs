using Avalonia.Controls;
using Avalonia.Threading;
using RemoteDesktop.Client.ViewModels;

namespace RemoteDesktop.Client.Views;

/// <summary>Hosts the terminal control of a tab: starts the shell once the size is known and hands the control the session.</summary>
public sealed partial class TerminalTabView : UserControl
{
    private TerminalTabViewModel? _viewModel;

    public TerminalTabView()
    {
        InitializeComponent();
        Terminal.GridSizeChanged += (_, e) => _ = _viewModel?.EnsureStartedAsync(e.Columns, e.Rows);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.SessionStarted -= OnSessionStarted;
        }

        _viewModel = DataContext as TerminalTabViewModel;
        Terminal.Session = null;
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.SessionStarted += OnSessionStarted;

        // A tab that was shown before (the tab control recreates its content when switching) picks its shell up again.
        if (_viewModel.Session is not null)
        {
            Terminal.Session = _viewModel.Session;
            Dispatcher.UIThread.Post(() => Terminal.Focus(), DispatcherPriority.Input);
        }
        else if (Terminal.Columns > 0)
        {
            _ = _viewModel.EnsureStartedAsync(Terminal.Columns, Terminal.Rows);
        }
    }

    private void OnSessionStarted(object? sender, EventArgs e)
    {
        Terminal.Session = _viewModel?.Session;
        Terminal.Focus();
    }
}
