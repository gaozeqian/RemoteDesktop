using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDesktop.Client.Storage;

namespace RemoteDesktop.Client.ViewModels;

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
    public partial string? Alias { get; set; }

    /// <summary>
    /// The password remembered on this machine for the device, filled in by <see cref="MainWindowViewModel"/> from the
    /// store's secret side. It is not part of <see cref="RecentDeviceEntry"/> and never leaves this machine.
    /// </summary>
    [ObservableProperty]
    public partial string? SavedPassword { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastConnectedText))]
    public partial DateTimeOffset LastConnectedUtc { get; set; }

    [ObservableProperty]
    public partial bool IsEditingAlias { get; set; }

    [ObservableProperty]
    public partial string NewAlias { get; set; } = string.Empty;

    public string FormattedId => Domain.DeviceId.TryParse(DeviceId, out var id) ? id.ToDisplayString() : DeviceId;

    public string DisplayTitle => string.IsNullOrWhiteSpace(Alias) ? FormattedId : $"{Alias} ({FormattedId})";

    public string LastConnectedText => $"上次连接: {LastConnectedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";

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
