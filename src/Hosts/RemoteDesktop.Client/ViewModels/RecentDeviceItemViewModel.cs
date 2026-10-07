using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDesktop.Client.Storage;

namespace RemoteDesktop.Client.ViewModels;

/// <summary>
/// What the rendezvous server last said about a device. <see cref="Unknown"/> is deliberately not "offline": it
/// means the question was never answered — an older server, a throttled query, no connection — and the UI must not
/// claim more than it knows.
/// </summary>
public enum DevicePresenceState
{
    Unknown,
    Online,
    Offline,
}

public sealed partial class RecentDeviceItemViewModel : ObservableObject
{
    private readonly Func<RecentDeviceItemViewModel, Task<bool>>? _onSaveAlias;
    private readonly Func<RecentDeviceItemViewModel, Task>? _onRemove;

    public RecentDeviceItemViewModel(
        RecentDeviceEntry entry,
        Func<RecentDeviceItemViewModel, Task<bool>>? onSaveAlias = null,
        Func<RecentDeviceItemViewModel, Task>? onRemove = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        DeviceId = entry.DeviceId;
        Alias = entry.Alias;
        LastConnectedUtc = entry.LastConnectedUtc;
        NewAlias = Alias ?? string.Empty;
        _onSaveAlias = onSaveAlias;
        _onRemove = onRemove;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    [NotifyPropertyChangedFor(nameof(FormattedId))]
    public partial string DeviceId { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    [NotifyPropertyChangedFor(nameof(ListTitle))]
    public partial string? Alias { get; set; }

    /// <summary>
    /// The password remembered on this machine for the device, filled in by <see cref="MainWindowViewModel"/> from the
    /// store's secret side. It is not part of <see cref="RecentDeviceEntry"/> and never leaves this machine.
    /// </summary>
    [ObservableProperty]
    public partial string? SavedPassword { get; set; }

    /// <summary>
    /// What to send for this device. Seeded from the secret store when the row is built and edited in the detail
    /// panel, so a row never borrows another row's password: the two used to share one box on the window.
    /// </summary>
    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    /// <summary>Whether a successful connection to this device saves its password on this machine.</summary>
    [ObservableProperty]
    public partial bool RememberPassword { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnline))]
    [NotifyPropertyChangedFor(nameof(IsOffline))]
    [NotifyPropertyChangedFor(nameof(IsPresenceUnknown))]
    [NotifyPropertyChangedFor(nameof(PresenceText))]
    public partial DevicePresenceState Presence { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastConnectedText))]
    public partial DateTimeOffset LastConnectedUtc { get; set; }

    [ObservableProperty]
    public partial bool IsEditingAlias { get; set; }

    [ObservableProperty]
    public partial string NewAlias { get; set; } = string.Empty;

    public string FormattedId => Domain.DeviceId.TryParse(DeviceId, out var id) ? id.ToDisplayString() : DeviceId;

    public string DisplayTitle => string.IsNullOrWhiteSpace(Alias) ? FormattedId : $"{Alias} ({FormattedId})";

    /// <summary>
    /// The heading a device is listed under: its alias when it has one, otherwise the code itself. Kept apart from
    /// <see cref="DisplayTitle"/> so a row never prints the code twice.
    /// </summary>
    public string ListTitle => string.IsNullOrWhiteSpace(Alias) ? FormattedId : Alias;

    public string LastConnectedText => $"上次连接: {LastConnectedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";

    public bool IsOnline => Presence == DevicePresenceState.Online;

    public bool IsOffline => Presence == DevicePresenceState.Offline;

    public bool IsPresenceUnknown => Presence == DevicePresenceState.Unknown;

    public string PresenceText => Presence switch
    {
        DevicePresenceState.Online => "在线",
        DevicePresenceState.Offline => "离线",
        _ => "状态未知",
    };

    [RelayCommand]
    private void StartEditAlias()
    {
        NewAlias = Alias ?? string.Empty;
        IsEditingAlias = true;
    }

    [RelayCommand]
    private async Task SaveAliasAsync()
    {
        var previous = Alias;
        Alias = string.IsNullOrWhiteSpace(NewAlias) ? null : NewAlias.Trim();
        IsEditingAlias = false;
        if (_onSaveAlias is not null && !await _onSaveAlias(this))
        {
            // The list on disk still says otherwise: put the row back rather than let the two quietly disagree.
            Alias = previous;
        }
    }

    [RelayCommand]
    private void CancelEditAlias()
    {
        NewAlias = Alias ?? string.Empty;
        IsEditingAlias = false;
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (_onRemove is not null)
        {
            await _onRemove(this);
        }
    }
}
