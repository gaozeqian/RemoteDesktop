using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Platform.MacOS.Interop;

namespace RemoteDesktop.Platform.MacOS;

/// <summary>
/// Keeps the Mac awake while a controller is connected, using a power assertion.
/// </summary>
/// <remarks>
/// The Windows version calls <c>SetThreadExecutionState</c>, which is per-thread and implicit. IOKit is the opposite:
/// an assertion is an object with an id, and it stays in force until it is released or the process dies. Nested
/// acquires are counted here so that two sessions do not have the second release cancel the first one's assertion.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class IOPMAssertionKeepAwake : ISystemKeepAwake
{
    private readonly Lock _gate = new();
    private uint _assertionId;
    private int _count;

    public void Acquire()
    {
        lock (_gate)
        {
            if (++_count != 1)
            {
                return;
            }

            // Both strings are ours to release: the assertion type is built from the literal (see IOKit) rather than
            // looked up, so it is a fresh object like the reason is.
            var assertionType = CoreFoundation.CreateString(IOKit.AssertionTypePreventUserIdleSystemSleep);
            var reason = CoreFoundation.CreateString("Remote Desktop session in progress");
            try
            {
                unsafe
                {
                    var id = 0u;
                    var status = IOKit.IOPMAssertionCreateWithName(
                        assertionType,
                        IOKit.AssertionLevelOn,
                        reason,
                        &id);
                    if (status != 0)
                    {
                        _count = 0;
                        throw new InvalidOperationException($"Could not take a power assertion (IOReturn 0x{status:x8}).");
                    }

                    _assertionId = id;
                }
            }
            finally
            {
                CoreFoundation.CFRelease(reason);
                CoreFoundation.CFRelease(assertionType);
            }
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (--_count > 0)
            {
                return;
            }

            _count = 0;
            if (_assertionId != 0)
            {
                // The assertion is gone whether or not the release reported success, and there is no recovery to
                // attempt if it did not — so the status is deliberately not turned into a failure.
                _ = IOKit.IOPMAssertionRelease(_assertionId);
                _assertionId = 0;
            }
        }
    }
}

/// <summary>
/// Decides whether files and shells may be served. On macOS the host is always a plain process in the user's own
/// session — there is no system context that could be reached (no Session 0, no service running as root with a
/// desktop), so there is nothing to refuse.
/// </summary>
/// <remarks>
/// The port exists because Windows can put an agent on the login screen, where handing out files or shells would give
/// the whole machine away. That situation cannot arise here, so this always allows.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class MacUserContextGuard : IUserContextGuard
{
    public string? GetRefusalReason() => null;
}
