using Avalonia.Platform.Storage;
using Avalonia.Controls;
using Avalonia.Input;
using RemoteDesktop.Client.ViewModels;

namespace RemoteDesktop.Client.Views;

/// <summary>The two-pane file manager. Dropping files from the operating system's file manager onto the remote side uploads them.</summary>
public sealed partial class FileManagerWindow : Window
{
    public FileManagerWindow()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(RemotePaneView, true);
        RemotePaneView.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        RemotePaneView.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private static void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.TryGetFiles() is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not FileManagerViewModel viewModel || e.DataTransfer.TryGetFiles() is not { Length: > 0 } files)
        {
            return;
        }

        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
        try
        {
            await viewModel.DropAsync(paths);
        }
#pragma warning disable CA1031 // This is an async void event handler: an exception here would take the process down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            viewModel.Message = $"无法上传拖入的文件：{ex.Message}";
        }
    }
}
