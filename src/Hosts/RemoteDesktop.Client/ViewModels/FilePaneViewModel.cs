using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDesktop.Features.FileTransfer;

namespace RemoteDesktop.Client.ViewModels;

/// <summary>A file or folder in a pane.</summary>
public sealed class FileItemViewModel(FileEntryInfo entry)
{
    public FileEntryInfo Entry { get; } = entry;

    public string Name => Entry.Name;

    public bool IsDirectory => Entry.IsDirectory;

    /// <summary>
    /// Hidden items are shown, but their icon and name are dimmed. The size and date keep full opacity: dimming
    /// the whole row pushed the metadata below 4.5:1 in the light theme.
    /// </summary>
    public double ContentOpacity => Entry.Attributes.HasFlag(FileEntryAttributes.Hidden) ? 0.5 : 1;

    public string SizeText => Entry.IsDirectory ? string.Empty : SizeFormat.Bytes(Entry.Size);

    public string ModifiedText => Entry.Modified == DateTimeOffset.MinValue
        ? string.Empty
        : Entry.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>One side of the file manager: a path, the folder's content and the operations on it. Works the same for the local and the remote computer.</summary>
public sealed partial class FilePaneViewModel(IFileSource source, string title) : ObservableObject, IDisposable
{
    private CancellationTokenSource? _load;
    private string _parent = string.Empty;
    private IReadOnlyList<FileItemViewModel> _selection = [];

    public string Title { get; } = title;

    public ObservableCollection<FileItemViewModel> Items { get; } = [];

    public ObservableCollection<PlaceInfo> Places { get; } = [];

    /// <summary>The folder being shown; empty while the drives are listed.</summary>
    [ObservableProperty]
    public partial string Path { get; set; } = string.Empty;

    /// <summary>What the user typed in the path box.</summary>
    [ObservableProperty]
    public partial string PathInput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial bool ConfirmingDelete { get; set; }

    [ObservableProperty]
    public partial PlaceInfo? SelectedPlace { get; set; }

    public IReadOnlyList<FileEntryInfo> SelectedEntries => [.. _selection.Select(i => i.Entry)];

    public bool CanGoUp => Path.Length > 0;

    public bool HasSelection => _selection.Count > 0;

    public bool HasExactlyOneSelected => _selection.Count == 1;

    /// <summary>Where new files from the other side would go.</summary>
    public bool HasFolder => Path.Length > 0;

    public string ConfirmText => string.Create(CultureInfo.InvariantCulture, $"确定删除所选的 {_selection.Count} 项？文件夹会连同内容一起删除。");

    public void Dispose()
    {
        _load?.Cancel();
        _load?.Dispose();
    }

    public string Combine(string name) => source.Combine(Path, name);

    public void SetSelection(IEnumerable<FileItemViewModel> selected)
    {
        _selection = [.. selected];
        ConfirmingDelete = false;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasExactlyOneSelected));
        OnPropertyChanged(nameof(ConfirmText));
        if (_selection.Count == 1)
        {
            NewName = _selection[0].Name;
        }
    }

    public async Task InitializeAsync()
    {
        try
        {
            var places = await source.GetPlacesAsync(CancellationToken.None);
            Places.Clear();
            foreach (var place in places)
            {
                Places.Add(place);
            }
        }
        catch (FileOperationException ex)
        {
            Message = ex.Message;
            return;
        }

        var start = Places.FirstOrDefault(p => !p.IsDrive);
        await NavigateAsync(start?.Path ?? string.Empty);
    }

    public async Task NavigateAsync(string path)
    {
        _load?.Cancel();
        var load = _load = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            var contents = await source.ListAsync(path, load.Token);
            if (load.IsCancellationRequested)
            {
                return;
            }

            Items.Clear();
            foreach (var entry in contents.Entries)
            {
                Items.Add(new FileItemViewModel(entry));
            }

            _parent = contents.Parent;
            Path = contents.Path;
            PathInput = contents.Path;
            Message = contents.Truncated ? "文件夹内项目过多，只显示了一部分。" : null;
            ConfirmingDelete = false;
            OnPropertyChanged(nameof(CanGoUp));
            OnPropertyChanged(nameof(HasFolder));
        }
        catch (FileOperationException ex)
        {
            // Stay where we are and say why.
            if (!load.IsCancellationRequested)
            {
                Message = ex.Message;
                PathInput = Path;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_load, load))
            {
                IsBusy = false;
            }
        }
    }

    partial void OnSelectedPlaceChanged(PlaceInfo? value)
    {
        if (value is not null && !string.Equals(value.Path, Path, StringComparison.OrdinalIgnoreCase))
        {
            _ = NavigateAsync(value.Path);
        }
    }

    [RelayCommand]
    private Task GoAsync() => NavigateAsync(PathInput.Trim());

    [RelayCommand]
    private Task UpAsync() => NavigateAsync(_parent);

    [RelayCommand]
    public Task RefreshAsync() => NavigateAsync(Path);

    [RelayCommand]
    private Task OpenAsync(FileItemViewModel? item) =>
        item is { IsDirectory: true } ? NavigateAsync(source.Combine(Path, item.Name)) : Task.CompletedTask;

    [RelayCommand]
    private Task NewFolderAsync() => RunAsync(async () =>
    {
        await source.MakeDirectoryAsync(source.Combine(Path, NewName.Trim()), CancellationToken.None);
        NewName = string.Empty;
    });

    [RelayCommand]
    private Task RenameAsync() => _selection.Count != 1
        ? Task.CompletedTask
        : RunAsync(async () =>
        {
            await source.RenameAsync(source.Combine(Path, _selection[0].Name), NewName.Trim(), CancellationToken.None);
            NewName = string.Empty;
        });

    [RelayCommand]
    private void AskDelete() => ConfirmingDelete = _selection.Count > 0;

    [RelayCommand]
    private void CancelDelete() => ConfirmingDelete = false;

    [RelayCommand]
    private Task ConfirmDeleteAsync()
    {
        var doomed = _selection.ToList();
        ConfirmingDelete = false;
        return RunAsync(async () =>
        {
            foreach (var item in doomed)
            {
                await source.DeleteAsync(source.Combine(Path, item.Name), recursive: item.IsDirectory, CancellationToken.None);
            }
        });
    }

    // A folder operation, then a refresh either way (a partly done delete must still show what is left).
    private async Task RunAsync(Func<Task> operation)
    {
        IsBusy = true;
        try
        {
            await operation();
            Message = null;
        }
        catch (FileOperationException ex)
        {
            Message = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }

        var message = Message;
        await NavigateAsync(Path);
        Message ??= message;
    }
}

internal static class SizeFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(double bytes)
    {
        var unit = 0;
        while (bytes >= 1024 && unit < Units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes:0} {Units[unit]}")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes:0.#} {Units[unit]}");
    }
}
