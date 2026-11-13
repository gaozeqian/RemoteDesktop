using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Versioning;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Audio;
using RemoteDesktop.Abstractions.Camera;
using RemoteDesktop.Abstractions.Clipboard;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Application;
using RemoteDesktop.Application.Hosting;
using RemoteDesktop.Application.Links;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Client.Setup;
using RemoteDesktop.Client.Storage;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.Camera;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Features.Terminal;
using RemoteDesktop.Media;
using RemoteDesktop.Platform.Linux.Setup;
using RemoteDesktop.Transport;
using RemoteDesktop.Transport.Rendezvous;

namespace RemoteDesktop.Client.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    /// <summary>
    /// The password box a device row keeps after the list is reloaded following a successful connection.
    /// </summary>
    /// <param name="current">What the row held before the reload; equal to <paramref name="previouslySaved"/> when the user never edited it.</param>
    /// <param name="previouslySaved">The secret the store had before this connection.</param>
    /// <param name="saved">The secret the store has now, i.e. the password the connection just succeeded with.</param>
    internal static string PasswordAfterReload(string? current, string? previouslySaved, string? saved) =>
        // null and "" are the same box: no password. Comparing them as distinct would make an empty box look like
        // an edit and leave it empty after the device's first password was remembered.
        string.Equals(current ?? string.Empty, previouslySaved ?? string.Empty, StringComparison.Ordinal)
            ? saved ?? string.Empty
            : current ?? string.Empty;

    /// <summary>
    /// Rebuilds the list from disk. Anything the user typed into a row in this session is carried over: the rows are
    /// recreated on every connect, and without this the detail panel would appear to forget a password that was just
    /// entered, and the selection would point at an object no longer in the list.
    /// </summary>
    public async Task LoadRecentDevicesAsync()
    {
        IReadOnlyList<RecentDeviceEntry> entries;
        try
        {
            entries = await _recentDevicesStore.GetAllAsync();
        }
        catch (Exception ex)
        {
            // Called fire-and-forget at start-up and from the refresh button, so nothing above could report this. The
            // list stays as it is on screen, and the file on disk was left untouched by the failed read.
            LogDeviceListReadFailed(_logger, ex);
            StatusMessage = "无法读取设备列表：读取设备列表文件失败。";
            return;
        }

        var edited = RecentDevices.ToDictionary(item => item.DeviceId, StringComparer.Ordinal);
        var selectedId = SelectedRecentDevice?.DeviceId;

        RecentDevices.Clear();
        foreach (var entry in entries)
        {
            var item = new RecentDeviceItemViewModel(
                entry,
                onSaveAlias: SaveDeviceAliasAsync,
                onRemove: RemoveDeviceAsync);

            // The password lives in the OS-protected secret store, not in the entry: read it back per device so it can
            // prefill the box, and so it is never part of anything that could be synced to an account later.
            item.SavedPassword = await _recentDevicesStore.GetPasswordAsync(entry.DeviceId);
            if (edited.TryGetValue(entry.DeviceId, out var previous))
            {
                item.Password = previous.Password;
                item.RememberPassword = previous.RememberPassword;
                item.Presence = previous.Presence;

                // A successful connection saves the password it used, but the row still holds the box the user left
                // it with. Carrying that box over verbatim would keep sending the old password — a fixed password
                // that has since been cleared, or a temporary one that rotated — so a row the user has not edited is
                // refreshed from the store. An edited box is theirs and is kept.
                item.Password = PasswordAfterReload(previous.Password, previous.SavedPassword, item.SavedPassword);
            }
            else
            {
                item.Password = item.SavedPassword ?? string.Empty;
            }

            RecentDevices.Add(item);
        }

        HasRecentDevices = RecentDevices.Count > 0;
        SelectedRecentDevice = RecentDevices.FirstOrDefault(item => string.Equals(item.DeviceId, selectedId, StringComparison.Ordinal))
            ?? RecentDevices.FirstOrDefault();

        _ = RefreshPresenceAsync();
    }

    /// <summary>Re-reads the list from disk, for the refresh button next to it.</summary>
    [RelayCommand]
    private Task RefreshRecentDevicesAsync() => LoadRecentDevicesAsync();

    /// <summary>
    /// Asks the server which remembered devices are online, and paints the answer onto the rows.
    /// <para>
    /// Every failure mode leaves the affected rows <see cref="DevicePresenceState.Unknown"/> rather than offline: an
    /// older server ignores the message entirely, a throttled one says nothing, and the client cannot tell those from
    /// a device that really is off. Only a sentence from the server is allowed to turn a dot grey.
    /// </para>
    /// </summary>
    private async Task RefreshPresenceAsync()
    {
        if (Interlocked.Exchange(ref _presenceInFlight, 1) != 0)
        {
            return;
        }

        try
        {
            if (_rendezvous.State != RendezvousState.Online)
            {
                return;
            }

            var rows = new List<(RecentDeviceItemViewModel Item, DeviceId Id)>();
            foreach (var item in RecentDevices)
            {
                if (DeviceId.TryParse(item.DeviceId, out var id))
                {
                    rows.Add((item, id));
                }
            }

            if (rows.Count == 0)
            {
                return;
            }

            var unanswered = false;
            foreach (var chunk in rows.Chunk(PresenceChunkSize))
            {
                var online = await _rendezvous.QueryPresenceAsync([.. chunk.Select(pair => pair.Id)], CancellationToken.None);
                if (online is null)
                {
                    unanswered = true;
                    foreach (var (item, _) in chunk)
                    {
                        item.Presence = DevicePresenceState.Unknown;
                    }

                    continue;
                }

                foreach (var (item, id) in chunk)
                {
                    item.Presence = online.Contains(id) ? DevicePresenceState.Online : DevicePresenceState.Offline;
                }
            }

            IsPresenceUnavailable = unanswered;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or PeerConnectException)
        {
            // A timer callback that throws has nowhere to report it, and this client keeps no log file.
            LogPresencePollFailed(_logger, ex);
        }
        finally
        {
            Interlocked.Exchange(ref _presenceInFlight, 0);
        }
    }

    /// <summary>Returns whether the alias reached the disk, so the row can be put back when it did not.</summary>
    private async Task<bool> SaveDeviceAliasAsync(RecentDeviceItemViewModel item)
    {
        try
        {
            await _recentDevicesStore.UpdateAliasAsync(item.DeviceId, item.Alias);
            return true;
        }
        catch (Exception ex)
        {
            LogDeviceListWriteFailed(_logger, ex);
            StatusMessage = "无法保存设备别名：写入设备列表失败。";
            return false;
        }
    }

    private async Task RemoveDeviceAsync(RecentDeviceItemViewModel item)
    {
        try
        {
            await _recentDevicesStore.RemoveAsync(item.DeviceId);
        }
        catch (Exception ex)
        {
            // The row is still in the list on disk, so leaving it on screen keeps the two agreeing.
            LogDeviceListWriteFailed(_logger, ex);
            StatusMessage = "无法删除设备：写入设备列表失败。";
            return;
        }

        RecentDevices.Remove(item);
        HasRecentDevices = RecentDevices.Count > 0;
        if (SelectedRecentDevice == item)
        {
            SelectedRecentDevice = RecentDevices.FirstOrDefault();
        }
    }

}
