using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

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

    private readonly Lock _gate = new();

    private PortalConnection? _connection;
    private string? _session;
    private DateTime _nextAttempt;

    /// <summary>Whether control is available, opening the session if it is not open and has not just failed.</summary>
    public bool EnsureReady()
    {
        lock (_gate)
        {
            if (_connection is not null && _session is not null)
            {
                return true;
            }

            if (DateTime.UtcNow < _nextAttempt)
            {
                return false;
            }

            _nextAttempt = DateTime.UtcNow + RetryDelay;

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
                LogGranted(logger);
                return true;
            }
            catch (Exception error) when (error is PortalException or InvalidOperationException)
            {
                LogRefused(logger, error.Message);
                return false;
            }
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
    public (PortalConnection Connection, string Session) Current()
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
