using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDesktop.Application.Hosting;

namespace RemoteDesktop.Client.ViewModels;

/// <summary>Someone who is controlling (or watching) this computer right now, with what the person at the keyboard can do about it.</summary>
public sealed partial class HostSessionItem(HostSessionInfo info, IHostControl control) : ObservableObject
{
    public string Id => info.Id;

    public string Mode => info.Mode;

    public string Description => $"{info.Controller} 正在{(info.Mode == "view" ? "观看" : "控制")}本机";

    public bool CanDowngrade => info.Mode != "view";

    [RelayCommand]
    private Task DowngradeAsync() => TryAsync(() => control.DowngradeSessionAsync(info.Id, CancellationToken.None));

    [RelayCommand]
    private Task DisconnectAsync() => TryAsync(() => control.DisconnectSessionAsync(info.Id, CancellationToken.None));

    // The next status refresh shows the outcome; a service that cannot be reached is shown there as well.
    private static async Task TryAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException)
        {
        }
    }
}
