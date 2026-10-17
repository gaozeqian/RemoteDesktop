using System.Net.WebSockets;
using Google.Protobuf;
using RemoteDesktop.Signaling;

namespace RemoteDesktop.Server.Rendezvous;

/// <summary>A registered device's signaling connection. Sends are serialized because WebSocket allows one at a time.</summary>
public sealed class DeviceSession(uint deviceId, string fingerprint, uint capabilities, WebSocket socket) : IDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public uint DeviceId { get; } = deviceId;

    public string Fingerprint { get; } = fingerprint;

    /// <summary>What the device declared it understands, from <see cref="RemoteDesktop.Signaling.ClientCapabilities"/>.</summary>
    public uint Capabilities { get; } = capabilities;

    /// <summary>Whether the device declared <paramref name="capability"/>; an older client simply has not.</summary>
    public bool Supports(uint capability) => (Capabilities & capability) == capability;

    public async Task SendAsync(IMessage message, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WebSocketMessaging.SendAsync(socket, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Best effort: a failed send means the device is going away and its own loop will clean up.</summary>
    public async Task TrySendAsync(IMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    public void Abort() => socket.Abort();

    public void Dispose() => _sendLock.Dispose();
}

/// <summary>Online devices by id.</summary>
public sealed class DevicePresence
{
    private readonly Dictionary<uint, DeviceSession> _sessions = [];
    private readonly Lock _lock = new();

    /// <summary>Registers a session; an older session of the same device (e.g. after a network change) is aborted.</summary>
    public void Add(DeviceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        DeviceSession? previous;
        lock (_lock)
        {
            _sessions.TryGetValue(session.DeviceId, out previous);
            _sessions[session.DeviceId] = session;
        }

        previous?.Abort();
    }

    public void Remove(DeviceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_lock)
        {
            if (_sessions.TryGetValue(session.DeviceId, out var current) && current == session)
            {
                _sessions.Remove(session.DeviceId);
            }
        }
    }

    public DeviceSession? Find(uint deviceId)
    {
        lock (_lock)
        {
            return _sessions.GetValueOrDefault(deviceId);
        }
    }

    /// <summary>Devices online right now (for metrics and tests).</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _sessions.Count;
            }
        }
    }

    /// <summary>
    /// The subset of <paramref name="deviceIds"/> that is online right now, in the order asked. Only ever a subset:
    /// a code that was never registered and a code that is simply offline produce the same answer, so this cannot be
    /// used to find out whether a device exists.
    /// </summary>
    public List<uint> FilterOnline(IReadOnlyList<uint> deviceIds)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);

        var online = new List<uint>(deviceIds.Count);
        lock (_lock)
        {
            foreach (var id in deviceIds)
            {
                if (_sessions.ContainsKey(id))
                {
                    online.Add(id);
                }
            }
        }

        return online;
    }
}
