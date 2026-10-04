using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDesktop.Features.Terminal;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Client.ViewModels;

/// <summary>One tab of the terminal window: a remote shell that is started once the tab knows how big it is.</summary>
public sealed partial class TerminalTabViewModel(IPeerConnection connection, string? shell, int number) : ObservableObject, IAsyncDisposable
{
    private int _starting;

    public event EventHandler? CloseRequested;

    public TerminalSession? Session { get; private set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Create(CultureInfo.InvariantCulture, $"{number}: {shell ?? "shell"}");

    /// <summary>Why there is nothing to type into (failed to open, shell ended, connection lost); empty while it works.</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    /// <summary>Raised on the UI thread when <see cref="Session"/> has been created.</summary>
    public event EventHandler? SessionStarted;

    /// <summary>Starts the shell at the given size; only the first call does anything.</summary>
    public async Task EnsureStartedAsync(int columns, int rows)
    {
        if (Interlocked.Exchange(ref _starting, 1) != 0)
        {
            return;
        }

        try
        {
            var session = await TerminalSession.StartAsync(connection, shell, columns, rows, CancellationToken.None);
            session.Emulator.TitleChanged += (_, _) => Dispatcher.UIThread.Post(() => UpdateTitle(session));
            session.Ended += (_, _) => Dispatcher.UIThread.Post(() => OnEnded(session));
            Session = session;
            UpdateTitle(session);
            SessionStarted?.Invoke(this, EventArgs.Empty);
        }
        catch (TerminalException ex)
        {
            Status = $"无法打开终端：{ex.Message}";
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    public async ValueTask DisposeAsync()
    {
        if (Session is { } session)
        {
            await session.DisposeAsync();
        }
    }

    private void UpdateTitle(TerminalSession session)
    {
        // Shells put the path of their own executable in the title by default, which says nothing; a real title (a running
        // program, a working directory a prompt sets) is more useful than the shell's name.
        var title = session.Emulator.Title;
        var useful = !string.IsNullOrWhiteSpace(title) && !title.Contains(":\\", StringComparison.Ordinal);
        Title = string.Create(CultureInfo.InvariantCulture, $"{number}: {(useful ? title : session.Shell)}");
    }

    private void OnEnded(TerminalSession session) =>
        Status = session.ExitCode is { } code ? $"shell 已退出（返回值 {code}）。关闭此标签或新建一个。" : "连接已中断。被控端锁屏、注销或断网都会结束终端。";
}

/// <summary>The terminal window: tabs of remote shells on the host, and the choice of which shell a new tab starts.</summary>
public sealed partial class TerminalWindowViewModel(IPeerConnection connection) : ObservableObject, IAsyncDisposable
{
    private int _counter;

    public ObservableCollection<TerminalTabViewModel> Tabs { get; } = [];

    public ObservableCollection<string> Shells { get; } = [];

    [ObservableProperty]
    public partial TerminalTabViewModel? SelectedTab { get; set; }

    [ObservableProperty]
    public partial string? SelectedShell { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    /// <summary>The last tab was closed; the window has nothing left to show.</summary>
    public event EventHandler? Empty;

    public async Task InitializeAsync()
    {
        try
        {
            foreach (var shell in await TerminalClient.ListShellsAsync(connection, CancellationToken.None))
            {
                Shells.Add(shell);
            }

            SelectedShell = Shells.FirstOrDefault();
        }
        catch (TerminalException ex)
        {
            Message = ex.Message;
            return;
        }

        NewTab();
    }

    [RelayCommand]
    private void NewTab()
    {
        var tab = new TerminalTabViewModel(connection, SelectedShell, ++_counter);
        tab.CloseRequested += (_, _) => _ = CloseTabAsync(tab);
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    public async Task CloseTabAsync(TerminalTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        Tabs.RemoveAt(index);
        if (ReferenceEquals(SelectedTab, tab) || SelectedTab is null)
        {
            SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        }

        await tab.DisposeAsync();
        if (Tabs.Count == 0)
        {
            Empty?.Invoke(this, EventArgs.Empty);
        }
    }

    public async ValueTask DisposeAsync()
    {
        var tabs = Tabs.ToList();
        Tabs.Clear();
        foreach (var tab in tabs)
        {
            await tab.DisposeAsync();
        }
    }
}
