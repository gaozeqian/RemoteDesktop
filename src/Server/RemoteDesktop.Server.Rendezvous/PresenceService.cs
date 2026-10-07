using Microsoft.Extensions.Options;
using RemoteDesktop.Signaling.Messages;

namespace RemoteDesktop.Server.Rendezvous;

/// <summary>
/// Answers "which of these devices are online", throttled per requester.
/// </summary>
/// <remarks>
/// This is the first thing a client can ask that the devices being asked about never see. Everything that keeps it
/// from becoming a surveillance tool lives here:
/// <list type="bullet">
/// <item>only a subset of the ids asked about is ever named, so a code that was never registered and a code that is
/// offline are indistinguishable, and the query cannot be used to discover which codes exist;</item>
/// <item>a query over the cap is dropped rather than truncated — a truncated answer would read as "offline" for the
/// ids left out, which is a lie the server made up;</item>
/// <item>throttled, malformed and unsupported queries are all answered with silence, and the client renders silence
/// as unknown;</item>
/// <item>the only telemetry the rendezvous server has is a log, so a client that scrapes leaves a warning behind.
/// </item>
/// </list>
/// </remarks>
public sealed partial class PresenceService(
    DevicePresence presence,
    PresenceRateLimiter limiter,
    IOptions<RendezvousOptions> options,
    ILogger<PresenceService> logger)
{
    /// <summary>One query may name at most this many devices. Over the cap it is dropped, never truncated.</summary>
    public const int MaxDeviceIdsPerQuery = 64;

    public async Task HandleQueryAsync(DeviceSession requester, PresenceQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requester);
        ArgumentNullException.ThrowIfNull(query);

        if (!options.Value.PresenceEnabled || string.IsNullOrEmpty(query.RequestId) || query.DeviceIds.Count > MaxDeviceIdsPerQuery)
        {
            return;
        }

        if (!limiter.TryAcquire(requester))
        {
            LogThrottled(logger, requester.DeviceId, query.DeviceIds.Count);
            return;
        }

        var result = new PresenceResult { RequestId = query.RequestId };
        result.OnlineDeviceIds.Add(presence.FilterOnline(query.DeviceIds));
        LogAnswered(logger, requester.DeviceId, result.OnlineDeviceIds.Count, query.DeviceIds.Count);
        await requester.TrySendAsync(new ServerMessage { PresenceResult = result }, cancellationToken).ConfigureAwait(false);
    }

    public void Forget(DeviceSession session) => limiter.Forget(session);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Device {DeviceId} asked about {Asked} devices; {Online} are online.")]
    private static partial void LogAnswered(ILogger logger, uint deviceId, int online, int asked);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Throttled a presence query from device {DeviceId} for {Asked} devices.")]
    private static partial void LogThrottled(ILogger logger, uint deviceId, int asked);
}
