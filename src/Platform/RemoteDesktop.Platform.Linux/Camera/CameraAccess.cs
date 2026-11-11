using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Camera;
using RemoteDesktop.Platform.Linux.XdgPortal;
using Tmds.DBus.Protocol;

namespace RemoteDesktop.Platform.Linux.Camera;

/// <summary>
/// The explicit camera grant a Linux host needs because its desktop cannot show the access dialog to it.
/// </summary>
/// <remarks>
/// <para>
/// <c>org.freedesktop.portal.Camera.AccessCamera</c> asks the desktop to show a system access dialog. GNOME Shell
/// refuses that dialog unless the requesting process is the focused application, and the portal frontend passes an
/// empty parent window, so a background host — the normal Linux shape — can never satisfy it. The portal does not
/// remember a refusal in that case either, so every attempt asks the same unanswerable question.
/// </para>
/// <para>
/// The portal still reads its permission store before asking, and an explicit <c>yes</c> there answers the request
/// without a dialog. That is the grant this class manages: the person allows it once in the client's settings, and
/// the host writes it for the app id the portal derives for the host process. Revoking writes <c>no</c>, which is a
/// deliberate refusal rather than "ask again" — asking again is the broken path.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed partial class CameraAccess : ICameraAccess, IDisposable
{
    /// <summary>How long a read is believed before the store is asked again.</summary>
    /// <remarks>
    /// The host status poll runs every second; a D-Bus round trip per poll would be wasteful and would make the
    /// settings page's status the busiest thing on the session. A grant or refusal updates the cache itself, so the
    /// only delay this adds is for a change made by another process.
    /// </remarks>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    private readonly WaylandCameraEnumerator _enumerator;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CameraAccess> _logger;
    private readonly string? _appId;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private PortalConnection? _connection;
    private CameraAccessState? _state;
    private DateTime _checkedAt;
    private bool _disposed;

    public CameraAccess(
        WaylandCameraEnumerator enumerator,
        ILoggerFactory loggerFactory,
        ILogger<CameraAccess> logger)
    {
        _enumerator = enumerator;
        _loggerFactory = loggerFactory;
        _logger = logger;

        // Read once: a process cannot move between systemd units, and the answer is a property of how it was started.
        _appId = PortalAppId.FromCurrentProcess();
        if (_appId is null)
        {
            _state = CameraAccessState.Unsupported;
            LogNoAppId(logger);
        }
    }

    public async Task<CameraAccessState> GetStateAsync(CancellationToken cancellationToken)
    {
        if (_appId is null)
        {
            return CameraAccessState.Unsupported;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state is { } cached && DateTime.UtcNow - _checkedAt < CacheLifetime)
            {
                return cached;
            }

            try
            {
                var permissions = await Connection().GetCameraPermissionAsync(_appId, cancellationToken).ConfigureAwait(false);
                _state = permissions.Contains("yes", StringComparer.Ordinal)
                    ? CameraAccessState.Allowed
                    : permissions.Contains("no", StringComparer.Ordinal)
                        ? CameraAccessState.Denied
                        : CameraAccessState.Unset;
            }
            catch (DBusErrorReplyException)
            {
                // The store reports "nothing recorded" as an error. That is the first-run state, not a failure.
                _state = CameraAccessState.Unset;
            }
            catch (Exception error) when (error is DBusExceptionBase or PortalException)
            {
                // PortalException is the "the portals cannot be asked at all" answer — no session bus, or one that
                // cannot be reached — and it belongs in the same bucket as a D-Bus failure: the state is unknown, not
                // denied.
                LogReadFailed(_logger, error.Message);
                _state = CameraAccessState.Unknown;
            }

            _checkedAt = DateTime.UtcNow;
            return _state.Value;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAllowedAsync(bool allowed, CancellationToken cancellationToken)
    {
        if (_appId is null)
        {
            throw new InvalidOperationException(
                "This process has no desktop app id, so the camera portal has nothing to grant. " +
                "Run the installed host service, or start the client from the application menu.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await Connection().SetCameraPermissionAsync(_appId, allowed, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is DBusExceptionBase or PortalException)
            {
                // Same pairing as above, and the wording has to be honest for both causes: the permission store
                // refused the write, or there is no portal to write to. The type stays InvalidOperationException
                // because both the window and the host-control channel surface that one to the user.
                throw new InvalidOperationException(
                    $"The desktop did not accept the camera {(allowed ? "grant" : "revocation")}: {error.Message}",
                    error);
            }

            _state = allowed ? CameraAccessState.Allowed : CameraAccessState.Denied;
            _checkedAt = DateTime.UtcNow;

            // The enumerator keeps a list and a refusal backoff. Both were produced under the old answer.
            _enumerator.Forget();

            if (allowed)
            {
                LogGranted(_logger, _appId);
            }
            else
            {
                LogRefused(_logger, _appId);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection?.Dispose();
        _gate.Dispose();
    }

    private PortalConnection Connection() =>
        _connection ??= new PortalConnection(_loggerFactory.CreateLogger<PortalConnection>());

    [LoggerMessage(Level = LogLevel.Information, Message = "Camera access will be granted for app id '{AppId}'.")]
    private static partial void LogGranted(ILogger logger, string appId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Camera access will be refused for app id '{AppId}'.")]
    private static partial void LogRefused(ILogger logger, string appId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "This process has no desktop app id, so camera access cannot be granted to it. " +
                  "Start the installed host service or the client from the application menu.")]
    private static partial void LogNoAppId(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The desktop's camera permission could not be read: {Reason}")]
    private static partial void LogReadFailed(ILogger logger, string reason);
}
