using System.Runtime.InteropServices;
using RemoteDesktop.Platform.MacOS.Interop;

namespace RemoteDesktop.Platform.MacOS.Video;

/// <summary>
/// Keeps the native video handle alive for as long as the managed side needs it, and owns the GCHandle the helper
/// calls back through.
/// </summary>
/// <remarks>
/// <para>
/// Both the encoder and the decoder hand this object to the helper as the reference constant of their callback,
/// which is why it exists as one type rather than two: the callback does nothing but note that work is done, and
/// both sides need exactly that.
/// </para>
/// <para>
/// The handle is freed by <see cref="Dispose"/>, which is also what makes the contract safe: the native side
/// promises not to call back after its <c>*_destroy</c> returns, so freeing the GCHandle there is the last thing
/// that touches the helper.
/// </para>
/// </remarks>
internal sealed class MacVideoFrameSink : IDisposable
{
    private readonly object _gate = new();
    private GCHandle _handle;
    private bool _disposed;

    public MacVideoFrameSink() => _handle = GCHandle.Alloc(this);

    /// <summary>The address the helper calls back through.</summary>
    public nint RefCon => GCHandle.ToIntPtr(_handle);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_handle.IsAllocated)
            {
                _handle.Free();
            }
        }
    }
}
