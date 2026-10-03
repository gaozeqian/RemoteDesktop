using System.Collections.Concurrent;
using System.Runtime.Versioning;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace RemoteDesktop.Platform.Windows.MediaFoundation;

/// <summary>
/// Async (hardware) MFTs announce "need input" / "have output" as events. A dedicated thread blocks on the event
/// generator and queues the event types, so the encoder can wait with a timeout. The thread ends when the transform
/// is shut down (<c>GetEvent</c> then fails).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MfEventPump : IDisposable
{
    private readonly BlockingCollection<int> _events = [];
    private readonly IMFMediaEventGenerator _generator;
    private readonly Thread _thread;

    public MfEventPump(IMFMediaEventGenerator generator)
    {
        _generator = generator;
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
                using var mediaEvent = _generator.GetEvent(0);
                _events.Add((int)mediaEvent.EventType);
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
    public void Dispose()
    {
        _thread.Join(TimeSpan.FromSeconds(2));
        _events.CompleteAdding();
        _generator.Dispose();
        _events.Dispose();
    }
}
