namespace RemoteDesktop.Client.Storage;

/// <summary>
/// One device in the local device list. Deliberately holds no password: a remembered password is a secret of this
/// machine (see <see cref="IRecentDevicesStore.GetPasswordAsync"/>) and must never travel with an entry if the list
/// is later synced to an account.
/// </summary>
public sealed record RecentDeviceEntry
{
    public required string DeviceId { get; init; }

    public string? Alias { get; init; }

    /// <summary>When this device was last connected to.</summary>
    public DateTimeOffset LastConnectedUtc { get; init; }

    /// <summary>
    /// When the user last changed the entry itself (its alias, or removing it) — not when it was last connected to.
    /// </summary>
    public required DateTimeOffset UpdatedAtUtc { get; init; }

    /// <summary>
    /// Set when the user removed the device. The entry stays behind as a tombstone and is hidden from
    /// <see cref="IRecentDevicesStore.GetAllAsync"/>. Connecting to the device again revives it.
    /// </summary>
    public DateTimeOffset? DeletedAtUtc { get; init; }
}
