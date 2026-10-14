using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Platform.Linux.XdgPortal;
using RemoteDesktop.Platform.Posix.Interop;

namespace RemoteDesktop.Platform.Linux.Display;

/// <summary>
/// Starts a screen-cast session and hands out one capturer per screen the operator agreed to share.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is where the desktop asks a person for permission, and it asks the first time a controller actually wants
/// to see a screen.</b> Not at startup, and not when the display list is read — the list comes from the compositor
/// and costs nothing, so a controller can be told what screens exist long before anybody has agreed to share one. The
/// dialog appears at the moment it means something.
/// </para>
/// <para>
/// One session serves every capturer, because the dialog grants a set of screens at once and asking again for a
/// second one would be a second prompt for something already granted. The descriptor is duplicated per capturer:
/// each PipeWire context takes ownership of the one it is given, and a shared descriptor would be closed under the
/// others.
/// </para>
/// <para>
/// The session is treated as something that can end. A compositor restart, a revoked grant and a desktop that
/// simply decided otherwise all look the same from here — the stream stops — so the session is dropped and the next
/// request starts a new one. That is the normal path, not an error path.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class PipeWireScreenCapturerFactory : IScreenCapturerFactory, IDisposable
{
    private const string RestoreTokenName = "portal-restore-token";

    private readonly ILogger<PipeWireScreenCapturerFactory> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ISecretStore _secrets;
    private readonly Lock _gate = new();

    private PortalConnection? _portal;
    private string? _session;
    private IReadOnlyList<SharedScreen> _screens = [];
    private SafeFileHandle? _remote;

    public PipeWireScreenCapturerFactory(
        ISecretStore secrets,
        ILoggerFactory loggerFactory,
        ILogger<PipeWireScreenCapturerFactory> logger)
    {
        _secrets = secrets;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public IScreenCapturer Create(DisplayInfo display)
    {
        ArgumentNullException.ThrowIfNull(display);

        lock (_gate)
        {
            EnsureSession();

            var screen = Match(display);

            var duplicate = LibC.DuplicateFileDescriptor((int)_remote!.DangerousGetHandle());
            if (duplicate < 0)
            {
                throw new InvalidOperationException("The portal's descriptor could not be duplicated for a new capturer.");
            }

            LogStarting(_logger, display.Id, screen.NodeId, display.Bounds.Width, display.Bounds.Height);

            return new PipeWireScreenCapturer(
                duplicate,
                screen.NodeId,
                display,
                display.Bounds.Width,
                display.Bounds.Height,
                _loggerFactory.CreateLogger<PipeWireScreenCapturer>());
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            ForgetSession();
        }
    }

    /// <summary>Opens a screen-cast session, or does nothing if there already is one.</summary>
    private void EnsureSession()
    {
        if (_portal is not null && _remote is { IsInvalid: false, IsClosed: false })
        {
            return;
        }

        ForgetSession();

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
                .OpenScreenCastAsync(restoreToken, token => _secrets.Save(RestoreTokenName, Encoding.UTF8.GetBytes(token)), CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var remote = portal.OpenPipeWireRemoteAsync(session, CancellationToken.None).GetAwaiter().GetResult();

            _portal = portal;
            _session = session;
            _screens = screens;
            _remote = remote;

            LogScreens(_logger, screens.Count);
        }
        catch
        {
            portal.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Finds the portal's stream for a screen the compositor listed.
    /// </summary>
    /// <remarks>
    /// The two lists do not use the same units. The compositor describes a screen in device pixels, which is what
    /// <see cref="DisplayInfo.Bounds"/> carries; the portal describes the same screen in the units the compositor
    /// lays windows out in, which are scaled. The scale is therefore applied to the portal's position before the two
    /// are compared — without it, a screen at any scale other than one would never be found, and the common
    /// single-screen desktop would still work by falling through to the only stream there is.
    /// </remarks>
    private SharedScreen Match(DisplayInfo display)
    {
        if (_screens.Count == 0)
        {
            throw new InvalidOperationException("The desktop granted no screens to share.");
        }

        var scale = display.Scale <= 0 ? 1.0 : display.Scale;

        foreach (var screen in _screens)
        {
            var x = (int)Math.Round(screen.X * scale);
            var y = (int)Math.Round(screen.Y * scale);

            if (x == display.Bounds.X && y == display.Bounds.Y)
            {
                return screen;
            }
        }

        if (_screens.Count == 1)
        {
            // One screen was granted and one was asked for; they are the same screen whatever the arithmetic says.
            // This is the ordinary case, and the alternative is refusing to capture over a rounding difference.
            return _screens[0];
        }

        throw new InvalidOperationException(
            $"The desktop granted {_screens.Count} screens and none of them is '{display.Id}'.");
    }

    private void ForgetSession()
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
        Message = "Sharing a screen needs the desktop's permission, so a dialog is about to ask for it. The answer is remembered.")]
    private static partial void LogWillAsk(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "The desktop granted {Count} screen(s) to share.")]
    private static partial void LogScreens(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Capturing '{Display}' from PipeWire node {Node} at {Width}x{Height}.")]
    private static partial void LogStarting(ILogger logger, string display, uint node, int width, int height);
}
