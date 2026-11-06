using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Protocol.File;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Client.ViewModels;

public sealed record ConflictChoice(string Label, ConflictPolicy Policy)
{
    // The ComboBox automation peer uses ToString for the collapsed value; without this it read
    // "ConflictChoice { Label = …, Policy = … }" instead of the choice.
    public override string ToString() => Label;
}

/// <summary>One row of the transfer list.</summary>
public sealed partial class TransferItemViewModel : ObservableObject
{
    private readonly TransferJob _job;
    private readonly TransferManager _manager;
    private int _refreshQueued;

    public TransferItemViewModel(TransferJob job, TransferManager manager)
    {
        _job = job;
        _manager = manager;
        job.Changed += (_, _) =>
        {
            // Many chunks arrive per second; one refresh per UI turn is plenty.
            if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    Volatile.Write(ref _refreshQueued, 0);
                    Refresh();
                });
            }
        };
        Refresh();
    }

    public TransferJob Job => _job;

    public string Title => $"{(_job.Direction == TransferDirection.Upload ? "↑" : "↓")} {_job.FileName}";

    // The transfer list's automation peer asks ToString for the row's accessible name; without this it is the type
    // name ("TransferItemViewModel").
    public override string ToString() => Title;

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    /// <summary>0 to 100.</summary>
    [ObservableProperty]
    public partial double Percent { get; set; }

    [ObservableProperty]
    public partial bool CanPause { get; set; }

    [ObservableProperty]
    public partial bool CanResume { get; set; }

    [ObservableProperty]
    public partial bool CanCancel { get; set; }

    [ObservableProperty]
    public partial bool IsFinished { get; set; }

    [ObservableProperty]
    public partial string ResumeLabel { get; set; } = "继续";

    [RelayCommand]
    private void Pause() => _manager.Pause(_job);

    [RelayCommand]
    private void Resume() => _manager.Resume(_job);

    [RelayCommand]
    private void Cancel() => _manager.Cancel(_job);

    private void Refresh()
    {
        var state = _job.State;
        Percent = _job.Fraction * 100;
        Status = state switch
        {
            TransferState.Queued => "排队中",
            TransferState.Running => "传输中",
            TransferState.Paused => "已暂停",
            TransferState.Completed => "已完成",
            TransferState.Skipped => "已跳过（已存在）",
            TransferState.Failed => $"失败：{_job.Error}",
            _ => "已取消",
        };
        var speed = state == TransferState.Running && _job.BytesPerSecond > 0 ? $" · {SizeFormat.Bytes(_job.BytesPerSecond)}/s" : string.Empty;
        Detail = string.Create(CultureInfo.InvariantCulture, $"{SizeFormat.Bytes(_job.TransferredBytes)} / {SizeFormat.Bytes(_job.TotalBytes)}{speed}");
        CanPause = state is TransferState.Queued or TransferState.Running;
        CanResume = state is TransferState.Paused or TransferState.Failed;
        ResumeLabel = state == TransferState.Failed ? "重试" : "继续";
        CanCancel = state is TransferState.Queued or TransferState.Running or TransferState.Paused or TransferState.Failed;
        IsFinished = state is TransferState.Completed or TransferState.Skipped or TransferState.Cancelled;
    }
}

/// <summary>The file manager window: this computer's files on one side, the remote computer's on the other, and the transfers between them.</summary>
public sealed partial class FileManagerViewModel : ObservableObject, IAsyncDisposable
{
    private readonly RemoteFileSource _remote;
    private readonly TransferManager _manager;
    private int _refreshQueued;

    public FileManagerViewModel(IPeerConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var local = new LocalFileSource();
        _remote = new RemoteFileSource(connection);
        _manager = new TransferManager(connection, local, _remote);
        LocalPane = new FilePaneViewModel(local, "本机");
        RemotePane = new FilePaneViewModel(_remote, "远程");
        Conflicts = [new("覆盖已有文件", ConflictPolicy.Overwrite), new("跳过已有文件", ConflictPolicy.Skip), new("保留两者（重命名）", ConflictPolicy.Rename)];
        SelectedConflict = Conflicts[0];
        _manager.JobAdded += (_, job) => Dispatcher.UIThread.Post(() => Transfers.Add(Watch(new TransferItemViewModel(job, _manager))));
        Transfers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTransfers));
    }

    public FilePaneViewModel LocalPane { get; }

    public FilePaneViewModel RemotePane { get; }

    public ObservableCollection<TransferItemViewModel> Transfers { get; } = [];

    /// <summary>Whether the transfer list has anything in it, so the empty state and the list can take turns.</summary>
    public bool HasTransfers => Transfers.Count > 0;

    public IReadOnlyList<ConflictChoice> Conflicts { get; }

    [ObservableProperty]
    public partial ConflictChoice SelectedConflict { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    public Task InitializeAsync() => Task.WhenAll(LocalPane.InitializeAsync(), RemotePane.InitializeAsync());

    [RelayCommand]
    private Task UploadAsync() => EnqueueAsync(TransferDirection.Upload, LocalPane, RemotePane);

    [RelayCommand]
    private Task DownloadAsync() => EnqueueAsync(TransferDirection.Download, RemotePane, LocalPane);

    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var item in Transfers.Where(t => t.IsFinished).ToList())
        {
            Transfers.Remove(item);
        }
    }

    /// <summary>Files dropped from outside (e.g. the Windows Explorer) go to the folder the remote pane is showing.</summary>
    public async Task DropAsync(IEnumerable<string> paths)
    {
        if (!RemotePane.HasFolder)
        {
            Message = "请先在远程一侧打开要存放文件的文件夹。";
            return;
        }

        try
        {
            foreach (var folder in paths.GroupBy(p => Path.GetDirectoryName(p) ?? string.Empty))
            {
                var items = folder.Select(ToEntry).Where(e => e is not null).Select(e => e!).ToList();
                await _manager.EnqueueAsync(TransferDirection.Upload, folder.Key, items, RemotePane.Path, SelectedConflict.Policy, CancellationToken.None);
            }

            Message = null;
        }
        catch (FileOperationException ex)
        {
            Message = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Message = ex.Message;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
        await _remote.DisposeAsync();
        LocalPane.Dispose();
        RemotePane.Dispose();
    }

    private async Task EnqueueAsync(TransferDirection direction, FilePaneViewModel from, FilePaneViewModel to)
    {
        if (!to.HasFolder)
        {
            Message = $"请先在{to.Title}一侧打开要存放文件的文件夹。";
            return;
        }

        var items = from.SelectedEntries;
        if (items.Count == 0)
        {
            Message = $"请先在{from.Title}一侧选择要传输的文件或文件夹。";
            return;
        }

        try
        {
            await _manager.EnqueueAsync(direction, from.Path, items, to.Path, SelectedConflict.Policy, CancellationToken.None);
            Message = null;
        }
        catch (FileOperationException ex)
        {
            Message = ex.Message;
        }
    }

    // Once everything has settled, the folders show what arrived.
    private TransferItemViewModel Watch(TransferItemViewModel item)
    {
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TransferItemViewModel.Status) && !_manager.Jobs.Any(j => j.IsActive) &&
                Interlocked.Exchange(ref _refreshQueued, 1) == 0)
            {
                _ = RefreshPanesAsync();
            }
        };
        return item;
    }

    private async Task RefreshPanesAsync()
    {
        await Task.Delay(300);
        Volatile.Write(ref _refreshQueued, 0);
        if (!_manager.Jobs.Any(j => j.IsActive))
        {
            await Task.WhenAll(LocalPane.RefreshAsync(), RemotePane.RefreshAsync());
        }
    }

    private static FileEntryInfo? ToEntry(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                return new FileEntryInfo(Path.GetFileName(path), IsDirectory: true, 0, Directory.GetLastWriteTimeUtc(path), FileEntryAttributes.None);
            }

            var file = new FileInfo(path);
            return file.Exists ? new FileEntryInfo(file.Name, IsDirectory: false, file.Length, file.LastWriteTimeUtc, FileEntryAttributes.None) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
