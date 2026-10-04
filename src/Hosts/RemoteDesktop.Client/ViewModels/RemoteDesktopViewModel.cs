using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Client.ViewModels;

/// <summary>One remote desktop window: the video stream, the input path and the session they belong to.</summary>
public sealed partial class RemoteDesktopViewModel : ObservableObject, IAsyncDisposable
{
    private readonly RemoteSession _session;
    private readonly InputSender? _input;
    private readonly DispatcherTimer _statusTimer;
    private int _closed;

    public RemoteDesktopViewModel(RemoteSession session, DesktopViewer viewer, InputSender? input)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(viewer);

        _session = session;
        Viewer = viewer;
        _input = input;
        CanControl = input is not null;

        var mode = session.Context.Grant.Mode == SessionMode.View ? "观看" : "控制";
        Title = $"{session.Context.Connection.RemoteDevice.ToDisplayString()}（{mode}）";

        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStatus());
        _statusTimer.Start();

        viewer.Closed += (_, _) => Dispatcher.UIThread.Post(() => _ = CloseAsync());
        session.Closed += (_, _) => Dispatcher.UIThread.Post(() => _ = CloseAsync());
        UpdateStatus();
    }

    public DesktopViewer Viewer { get; }

    /// <summary>False in watch mode: input is neither captured nor sent.</summary>
    public bool CanControl { get; }

    public string Title { get; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "正在等待画面…";

    /// <summary>Raised once when the window should close.</summary>
    public event EventHandler? Closed;

    public void SendInput(InputEvent inputEvent) => _input?.Send(inputEvent);

    [RelayCommand]
    private Task DisconnectAsync() => CloseAsync();

    [RelayCommand]
    private void SendSecureAttention() => SendInput(new SecureAttentionEvent());

    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _statusTimer.Stop();
        Closed?.Invoke(this, EventArgs.Empty);
        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _statusTimer.Stop();
        if (_input is not null)
        {
            await _input.DisposeAsync();
        }

        await Viewer.DisposeAsync();
        await _session.DisposeAsync();
    }

    private void UpdateStatus()
    {
        var info = Viewer.StreamInfo;
        if (info is null)
        {
            return;
        }

        var stats = Viewer.Statistics;
        var path = _session.Path == ConnectionPath.Direct ? "直连" : "中继";
        var rtt = _session.RoundTripTime is { } value ? $" · RTT {value.TotalMilliseconds:0} ms" : string.Empty;
        var latency = stats.LatencyMilliseconds is { } ms ? $" · 画面延迟 {ms:0} ms" : string.Empty;
        var encoder = info.HardwareEncoder ? "硬件编码" : "软件编码";
        StatusText = string.Create(CultureInfo.InvariantCulture,
            $"{info.Width}×{info.Height} · {stats.FramesPerSecond:0} fps · {stats.Kbps / 1000:0.0} Mbps · {encoder}（{info.EncoderName}）· 解码 {stats.DecodeMilliseconds:0.0} ms{latency} · {path}{rtt}");
    }
}
