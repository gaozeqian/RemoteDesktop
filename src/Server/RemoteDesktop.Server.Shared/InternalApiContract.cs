namespace RemoteDesktop.Server.Shared;

/// <summary>
/// What a node says about itself at <c>GET /internal/v1/info</c>. The console uses it to show what it is talking to
/// before trusting any number that node reports.
/// </summary>
/// <param name="Name">The name the node is registered under in the console.</param>
/// <param name="Role">"rendezvous" or "relay".</param>
/// <param name="Version">The server build, so a mixed-version fleet is visible rather than inferred.</param>
/// <param name="StartedAtUnixMilliseconds">When the process started; uptime is computed by the reader, not sent.</param>
public sealed record NodeInfo(
    string Name,
    string Role,
    string Version,
    long StartedAtUnixMilliseconds);

/// <summary>
/// One device as the signaling server is willing to describe it. No fingerprint, no name, no address: the console needs
/// to answer "is this code registered and when was it last seen", and nothing else.
/// </summary>
/// <param name="DeviceId">The nine-digit id handed to the device.</param>
/// <param name="FirstSeenUnixMilliseconds">When the id was first allocated.</param>
/// <param name="LastSeenUnixMilliseconds">The last registration by that device, to within about a minute.</param>
/// <param name="Online">Whether it holds a signaling connection right now.</param>
/// <param name="Revoked">Whether an operator revoked it; the server refuses its next registration.</param>
public sealed record NodeDeviceSummary(
    uint DeviceId,
    long FirstSeenUnixMilliseconds,
    long LastSeenUnixMilliseconds,
    bool Online,
    bool Revoked);

/// <summary>
/// A page of devices from <c>GET /internal/v1/devices</c>. Cursor-based because the registry can hold millions of rows
/// and an offset query gets slower the further the reader scrolls.
/// </summary>
/// <param name="Devices">At most the requested limit, ordered by device id.</param>
/// <param name="NextCursor">Opaque value for the next page; <c>null</c> at the end.</param>
public sealed record NodeDevicePage(
    IReadOnlyList<NodeDeviceSummary> Devices,
    string? NextCursor);
