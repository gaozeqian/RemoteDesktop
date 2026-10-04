using Avalonia.Controls;
using Avalonia.Input;
using RemoteDesktop.Client.ViewModels;

namespace RemoteDesktop.Client.Views;

/// <summary>A folder listing with its tool bar. Selection and double-click are handled here because the list control exposes them as events only.</summary>
public sealed partial class FilePaneView : UserControl
{
    public FilePaneView()
    {
        InitializeComponent();
        Files.SelectionChanged += (_, _) =>
        {
            if (DataContext is FilePaneViewModel pane)
            {
                pane.SetSelection(Files.SelectedItems?.OfType<FileItemViewModel>() ?? []);
            }
        };
        Files.DoubleTapped += (_, e) =>
        {
            if (DataContext is FilePaneViewModel pane && (e.Source as Control)?.DataContext is FileItemViewModel item)
            {
                pane.OpenCommand.Execute(item);
            }
        };
        PathBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is FilePaneViewModel pane)
            {
                pane.GoCommand.Execute(null);
            }
        };
    }
}
