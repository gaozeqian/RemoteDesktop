using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using RemoteDesktop.Abstractions.Input;

namespace RemoteDesktop.Platform.Linux.XdgPortal;

/// <summary>
/// The desktop's permission to control this machine: the keyboard, the pointer and the clipboard.
/// </summary>
/// <remarks>
/// <para>
/// A second session, separate from the one that carries the screen, and shared by everything that needs it. The
/// portal attaches the clipboard to a <em>remote-desktop</em> session and never to a screen-cast one, and a session
/// may only have the clipboard requested once and only before it is started — so there has to be exactly one of
/// these, opened in the right order, rather than one per feature.
/// </para>
/// <para>
/// <b>This one cannot be persistent, and that is the portal's decision rather than a setting.</b> Asking for
/// <c>persist_mode</c> on a remote-desktop session is refused outright — "Remote desktop sessions cannot persist" —
/// so unlike screen sharing there is no remembered grant and the operator is asked on every connection. Control is
/// the more dangerous half of what this product does and the portal is deliberate about not letting it be granted in
/// advance.
/// </para>
/// <para>
/// Opening it asks a question, so it happens when something first needs it — a keypress, a click, a copy — and not
/// when the host starts. Failures are not retried immediately either: a desktop that will not grant control would
/// otherwise be asked again for every event that arrives, which is a dialog per keystroke.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class PortalControlSession(ILoggerFactory loggerFactory, ILogger<PortalControlSession> logger) : IDisposable
{
    /// <summary>How long to leave a failed attempt alone before asking again.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>Two explicit retries this close together would put two system dialogs on the host's screen for what is
    /// one person's click, and a controller could otherwise ask in a loop.</summary>
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _explicitRequest = new(1, 1);

    private PortalConnection? _connection;
    private string? _session;
    private DateTime _nextAttempt;
    private DateTime _lastExplicitRequest;
    private InputAccessStatus _status = new(InputAccessState.NotRequested);

    /// <summary>Where the desktop's permission for control stands. Safe to read from any thread.</summary>
    public InputAccessStatus AccessStatus => Volatile.Read(ref _status);

    /// <summary>Raised when <see cref="AccessStatus"/> changes. May be raised with <see cref="_gate"/> held, so a
    /// handler must not block or call back into this class.</summary>
    public event EventHandler<InputAccessStatus>? AccessStatusChanged;

    /// <summary>Whether control is available, opening the session if it is not open and has not just failed.</summary>
    public bool EnsureReady() => EnsureReady(force: false);

    /// <summary>
    /// Asks the desktop again, ignoring the backoff a refusal left behind. Runs off the caller's thread: the portal
    /// call does not return until the person at this machine answers, and the caller is often a session channel that
    /// has to keep flowing.
    /// </summary>
    public Task<InputAccessStatus> RequestAccessAsync(CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                // One explicit request at a time. A second controller asking while the dialog is up is answered
                // with the current state instead of queueing another question behind it.
                if (!_explicitRequest.Wait(0))
                {
                    return AccessStatus;
                }

                try
                {
                    if (DateTime.UtcNow - _lastExplicitRequest < RetryCooldown)
                    {
                        return AccessStatus;
                    }

                    _lastExplicitRequest = DateTime.UtcNow;
                    _ = EnsureReady(force: true);
                    return AccessStatus;
                }
                finally
                {
                    _explicitRequest.Release();
                }
            },
            cancellationToken);

    private bool EnsureReady(bool force)
    {
        lock (_gate)
        {
            if (_connection is not null && _session is not null)
            {
                Publish(InputAccessState.Granted);
                return true;
            }

            if (!force && DateTime.UtcNow < _nextAttempt)
            {
                return false;
            }

            Publish(InputAccessState.Requesting);

            try
            {
                LogWillAsk(logger);

                var connection = new PortalConnection(loggerFactory.CreateLogger<PortalConnection>());
                var session = connection
                    .OpenRemoteDesktopAsync(withClipboard: true, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                _connection = connection;
                _session = session;
                _nextAttempt = default;
                LogGranted(logger);
                Publish(InputAccessState.Granted);
                return true;
            }
            catch (PortalException error)
            {
                // The desktop answered and said no (or could not show its question). This is an authorization
                // answer rather than a broken mechanism: retrying is meaningful.
                //
                // The backoff starts when the answer arrives, not when the question was asked. A dialog can stay up
                // for minutes; with the deadline measured from the start, the next input after a late refusal would
                // immediately ask again and the controller would never see the refusal it is supposed to report.
                _nextAttempt = DateTime.UtcNow + RetryDelay;
                LogRefused(logger, error.Message);
                Publish(InputAccessState.Denied, error.Message);
                return false;
            }
            catch (InvalidOperationException error)
            {
                // There is no session bus, no app id or no portal to ask. Retrying the same thing will not help until
                // the environment changes, but the controller is told what is missing rather than nothing.
                _nextAttempt = DateTime.UtcNow + RetryDelay;
                LogRefused(logger, error.Message);
                Publish(InputAccessState.Unavailable, error.Message);
                return false;
            }
        }
    }

    private void Publish(InputAccessState state, string? reason = null)
    {
        var status = new InputAccessStatus(state, reason);
        var previous = Interlocked.Exchange(ref _status, status);
        if (previous != status)
        {
            AccessStatusChanged?.Invoke(this, status);
        }
    }

    /// <summary>The descriptor libei speaks on, for injecting input. Ownership passes to the caller.</summary>
    public SafeFileHandle ConnectEis()
    {
        lock (_gate)
        {
            return ConnectionFor().ConnectToEisAsync(SessionFor(), CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    /// <summary>The connection and session, for a caller that has already been told control is available.</summary>
    /// <remarks>
    /// Handed out together because neither is much use alone, and because a caller holding one across a forget would
    /// be holding a handle onto a closed connection. Callers re-read them each time rather than caching.
    /// </remarks>
    internal (PortalConnection Connection, string Session) Current()
    {
        lock (_gate)
        {
            return (ConnectionFor(), SessionFor());
        }
    }

    /// <summary>Throws the grant away, so the next request asks the desktop again.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _session = null;
            _connection?.Dispose();
            _connection = null;
            _nextAttempt = default;

            // The next input asks the desktop again, and the controller is told that the old answer no longer holds.
            Publish(InputAccessState.NotRequested);
        }
    }

    public void Dispose() => Forget();

    private PortalConnection ConnectionFor() =>
        _connection ?? throw new InvalidOperationException("Control has not been granted.");

    private string SessionFor() =>
        _session ?? throw new InvalidOperationException("Control has not been granted.");

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Controlling this machine needs its permission, so a dialog is about to ask. It asks every time, because control cannot be granted in advance.")]
    private static partial void LogWillAsk(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "The desktop granted the keyboard, the pointer and the clipboard.")]
    private static partial void LogGranted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The desktop did not grant control: {Reason}")]
    private static partial void LogRefused(ILogger logger, string reason);
}
