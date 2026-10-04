using RemoteDesktop.Application.Features;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Control;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Application.Sessions;

/// <summary>
/// What the host keeps about a running session beyond its context: the control channel (several parties write to
/// it), the token that ends the session, and one token per open feature channel so a single kind of channel can be
/// closed when its permission is withdrawn.
/// </summary>
internal sealed class HostedSession(SessionContext context, IChannel control, CancellationTokenSource end) : IDisposable
{
    private readonly SemaphoreSlim _controlWrite = new(1, 1);
    private readonly Lock _lock = new();
    private readonly List<(ChannelType Type, CancellationTokenSource Cts)> _channels = [];

    public SessionContext Context { get; } = context;

    public IChannel Control { get; } = control;

    /// <summary>Cancelling it ends the session.</summary>
    public CancellationTokenSource End { get; } = end;

    /// <summary>Serialized: the ping answers and host-initiated messages share one stream.</summary>
    public async Task WriteControlAsync(ControlMessage message, CancellationToken cancellationToken)
    {
        await _controlWrite.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ControlMessages.WriteAsync(Control, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _controlWrite.Release();
        }
    }

    public void Add(ChannelType type, CancellationTokenSource cts)
    {
        lock (_lock)
        {
            _channels.Add((type, cts));
        }
    }

    public bool HasOpen(ChannelType type)
    {
        lock (_lock)
        {
            return _channels.Any(c => c.Type == type);
        }
    }

    public void Remove(CancellationTokenSource cts)
    {
        lock (_lock)
        {
            _channels.RemoveAll(c => c.Cts == cts);
        }
    }

    /// <summary>Ends every open channel whose type matches <paramref name="shouldEnd"/>.</summary>
    public void Cancel(Func<ChannelType, bool> shouldEnd)
    {
        List<CancellationTokenSource> selected;
        lock (_lock)
        {
            selected = [.. _channels.Where(c => shouldEnd(c.Type)).Select(c => c.Cts)];
        }

        foreach (var cts in selected)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The channel ended on its own at the same moment.
            }
        }
    }

    public void Dispose() => _controlWrite.Dispose();
}
