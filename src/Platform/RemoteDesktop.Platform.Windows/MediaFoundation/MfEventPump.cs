using System.Collections.Concurrent;
using System.Runtime.Versioning;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace RemoteDesktop.Platform.Windows.MediaFoundation;

/// <summary>
/// The blocking <c>GetEvent</c> the pump waits on, behind a seam so a test can hold it open the way a wedged driver
/// does without a real Media Foundation transform.
/// </summary>
[SupportedOSPlatform("windows")]
internal interface IMfEventSource : IDisposable
{
    /// <summary>Blocks until the next event and returns its type.</summary>
    int GetEvent();
}

/// <summary>The real generator; owns the media event it reads.</summary>
[SupportedOSPlatform("windows")]
internal sealed class MfMediaEventSource(IMFMediaEventGenerator generator) : IMfEventSource
{
    public int GetEvent()
    {
        using var mediaEvent = generator.GetEvent(0);
        return (int)mediaEvent.EventType;
    }

    public void Dispose() => generator.Dispose();
}

/// <summary>
/// Async (hardware) MFTs announce "need input" / "have output" as events. A dedicated thread blocks on the event
/// generator and queues the event types, so the encoder can wait with a timeout. The thread ends when the transform
/// is shut down (<c>GetEvent</c> then fails).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MfEventPump : IDisposable
{
    private readonly BlockingCollection<int> _events = [];
    private readonly IMfEventSource _source;
    private readonly Thread _thread;
    private int _disposed;

    public MfEventPump(IMFMediaEventGenerator generator)
        : this(new MfMediaEventSource(generator))
    {
    }

    internal MfEventPump(IMfEventSource source)
    {
        _source = source;
        _thread = new Thread(Run) { IsBackground = true, Name = "MFT events" };
        _thread.Start();
    }

    public bool TryTake(out int eventType, TimeSpan timeout) => _events.TryTake(out eventType, timeout);

    public bool TryTake(out int eventType) => _events.TryTake(out eventType);

    private void Run()
    {
        try
        {
            while (true)
            {
                _events.Add(_source.GetEvent());
            }
        }
        catch (SharpGenException)
        {
            // MF_E_SHUTDOWN after the transform was shut down.
        }
        catch (InvalidOperationException)
        {
            // Collection completed during disposal.
        }
    }

    /// <summary>Call after the transform has been shut down, so the blocked <c>GetEvent</c> returns.</summary>
    /// <remarks>
    /// The generator is a COM object and the pump thread is its only user. Releasing it while the thread is still
    /// inside <c>GetEvent</c> is a use-after-free: a driver that never returns from a shutdown would turn a leaked
    /// encoder into a crashed process. When the thread does not stop within the budget, both the generator and the
    /// queue are deliberately left for process exit; the encoder is unusable at that point either way.
    /// <para>
    /// Completing the queue first is what unblocks <see cref="TryTake(out int, TimeSpan)"/> immediately, including
    /// on the wedged path; a thread that does return later exits through the collection-completed catch.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        _events.CompleteAdding();

        if (!_thread.Join(TimeSpan.FromSeconds(2)))
        {
            return;
        }

        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _source.Dispose();
            _events.Dispose();
        }
    }
}
