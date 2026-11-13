using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Platform.Posix.Interop;

namespace RemoteDesktop.Platform.Linux.XdgPortal;

/// <summary>
/// The desktop's permission to see a screen and to type into it, asked for once and kept.
/// </summary>
/// <remarks>
/// <para>
/// One session, shared by whoever needs it. The portal grants a screen and the input devices on the same handle, so
/// opening one per feature would mean the operator answering the same question twice — and a session opened for
/// screen sharing cannot be given the keyboard afterwards, so the second answer would have to be a second grant
/// rather than an extension of the first.
/// </para>
/// <para>
/// <b>The dialog appears the first time somebody asks, and not before.</b> Nothing here runs at startup: a
/// controller can be shown the list of screens, and a host can sit idle for days, without the desktop being asked
/// for anything. What triggers it is a controller actually wanting to see or touch a screen.
/// </para>
/// <para>
/// The grant is treated as something that can end — a compositor restart, a revoked permission, a desktop that
/// changed its mind all look the same from here — so it is dropped when it stops working and taken again on the next
/// request. That is a normal path rather than an error path.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed partial class PortalGrant : IDisposable
{
    private const string RestoreTokenName = "portal-restore-token";

    private readonly ISecretStore _secrets;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    private PortalConnection? _portal;
    private string? _session;
    private IReadOnlyList<SharedScreen> _screens = [];
    private SafeFileHandle? _remote;

    public PortalGrant(ISecretStore secrets, ILoggerFactory loggerFactory, ILogger<PortalGrant> logger)
    {
        _secrets = secrets;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>Opens the grant if it is not open, and returns the screens it covers.</summary>
    /// <exception cref="PortalException">The desktop refused, or the grant could not be opened.</exception>
    public IReadOnlyList<SharedScreen> EnsureScreens()
    {
        lock (_gate)
        {
            EnsureOpen();
            return _screens;
        }
    }

    /// <summary>
    /// Another descriptor for the same PipeWire connection, ownership of which passes to the caller.
    /// </summary>
    /// <remarks>
    /// Duplicated per caller because each PipeWire context takes ownership of the descriptor it is given, and a
    /// shared one would be closed under the others. Returned as a plain number rather than wrapped in a
    /// <c>SafeFileHandle</c>: PipeWire closes it, and a wrapper here would close it a second time — a descriptor
    /// number that has since been handed to somebody else.
    /// </remarks>
    public int DuplicateRemoteDescriptor()
    {
        lock (_gate)
        {
            EnsureOpen();

            var duplicate = LibC.DuplicateFileDescriptor((int)_remote!.DangerousGetHandle());
            if (duplicate < 0)
            {
                throw new InvalidOperationException("The desktop's descriptor could not be duplicated.");
            }

            return duplicate;
        }
    }

    /// <summary>Throws the grant away, so the next request asks the desktop again.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            ForgetLocked();
        }
    }

    public void Dispose() => Forget();

    private void EnsureOpen()
    {
        if (_portal is not null && _remote is { IsInvalid: false, IsClosed: false })
        {
            return;
        }

        ForgetLocked();

        var restoreToken = _secrets.Load(RestoreTokenName) is { Length: > 0 } stored
            ? Encoding.UTF8.GetString(stored)
            : null;

        if (restoreToken is null)
        {
            LogWillAsk(_logger);
        }

        var portal = new PortalConnection(_loggerFactory.CreateLogger<PortalConnection>());
        try
        {
            var (session, screens) = portal
                .OpenScreenCastAsync(
                    restoreToken,
                    token => _secrets.Save(RestoreTokenName, Encoding.UTF8.GetBytes(token)),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var remote = portal.OpenPipeWireRemoteAsync(session, CancellationToken.None).GetAwaiter().GetResult();

            _portal = portal;
            _session = session;
            _screens = screens;
            _remote = remote;

            LogGranted(_logger, screens.Count);
        }
        catch
        {
            portal.Dispose();
            throw;
        }
    }

    private void ForgetLocked()
    {
        _remote?.Dispose();
        _remote = null;
        _screens = [];
        _session = null;
        _portal?.Dispose();
        _portal = null;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Sharing a screen and controlling it needs the desktop's permission, so a dialog is about to ask. The answer is remembered.")]
    private static partial void LogWillAsk(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "The desktop granted {Count} screen(s).")]
    private static partial void LogGranted(ILogger logger, int count);
}
