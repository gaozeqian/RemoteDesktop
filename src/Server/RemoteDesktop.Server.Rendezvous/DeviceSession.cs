using System.Net.WebSockets;
using Google.Protobuf;
using RemoteDesktop.Signaling;

namespace RemoteDesktop.Server.Rendezvous;

/// <summary>A registered device's signaling connection. Sends are serialized because WebSocket allows one at a time.</summary>
public sealed class DeviceSession(uint deviceId, string fingerprint, WebSocket socket) : IDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public uint DeviceId { get; } = deviceId;

    public string Fingerprint { get; } = fingerprint;

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
}
