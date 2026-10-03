using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDesktop.Application.Security;
using RemoteDesktop.Domain;

namespace RemoteDesktop.Client.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel()
    {
        TemporaryPassword = TemporaryPasswordGenerator.Generate();
    }

    /// <summary>Allocated by the rendezvous server (milestone M1); placeholder until then.</summary>
    [ObservableProperty]
    public partial string LocalDeviceId { get; set; } = "--- --- ---";

    [ObservableProperty]
    public partial string TemporaryPassword { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial string RemoteDeviceId { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial string RemotePassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsWatchMode { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    [RelayCommand]
    private void RefreshPassword() => TemporaryPassword = TemporaryPasswordGenerator.Generate();

    private bool CanConnect() => DeviceId.TryParse(RemoteDeviceId, out _) && RemotePassword.Length > 0;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private void Connect()
    {
        var target = DeviceId.Parse(RemoteDeviceId);
        var mode = IsWatchMode ? SessionMode.View : SessionMode.Control;
        StatusMessage = $"连接 {target.ToDisplayString()}（{(mode == SessionMode.View ? "观看模式" : "远程控制")}）将在 M1/M2 实现。";
    }
}
