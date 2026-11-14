using RemoteDesktop.Abstractions.Display;

namespace RemoteDesktop.Platform.Shared;

/// <summary>
/// Caches the display list for a short time, so a stream of pointer events does not enumerate the desktop on every one.
/// </summary>
/// <remarks>
/// The cache first lived three times: Windows and macOS refreshed every two seconds and Linux every five, each with
/// its own copy of the same timestamp check. The lifetime stays a parameter because the difference is real — libei's
/// enumeration is a portal round trip — but the loop that implements it now exists once. The enumerator is called
/// while this object's lock is held; each platform's enumerator is itself safe to call from an input thread.
/// </remarks>
internal sealed class DisplayLookupCache(IDisplayEnumerator displays, TimeSpan lifetime, TimeProvider? time = null)
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private IReadOnlyList<DisplayInfo> _displays = [];
    private DateTimeOffset _loadedAt;

    /// <summary>The current display list, refreshed when it is empty or older than the lifetime.</summary>
    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_displays.Count == 0 || now - _loadedAt > lifetime)
            {
                _displays = displays.GetDisplays();
                _loadedAt = now;
            }

            return _displays;
        }
    }
}
