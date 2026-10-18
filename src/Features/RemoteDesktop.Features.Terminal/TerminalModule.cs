using System.ComponentModel;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Abstractions.Terminal;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Framing;
using RemoteDesktop.Protocol.Terminal;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Features.Terminal;

/// <summary>
/// Host side of the terminal: one channel is one terminal. The controller may ask which shells exist and then opens one;
/// keystrokes and output travel as raw frames afterwards. The shell runs as the user this process runs as and ends
/// with the channel, so a dropped connection never leaves a shell behind.
/// </summary>
public sealed partial class TerminalModule(
    IPtyHost pty,
    IUserContextGuard guard,
    ILogger<TerminalModule> logger) : IFeatureModule
{
    /// <summary>Terminals open at the same time on this host; a controller cannot use shells to exhaust the machine.</summary>
    public const int MaxTerminals = 8;

    private const int MaxRequestBytes = 64 * 1024;
    private const int MaxColumns = 1000;
    private const int MaxRows = 500;

    private int _active;

    public ChannelType ChannelType => ChannelType.Terminal;

    public Permissions RequiredPermission => Permissions.Terminal;

    public async Task HandleAsync(IChannel channel, SessionContext session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(session);

        var stream = channel.Stream;
        try
        {
            OpenTerminal? open = null;
            while (open is null)
            {
                var request = await ProtobufFrames.ReadAsync(stream, TerminalRequest.Parser, MaxRequestBytes, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                if (guard.GetRefusalReason() is { } reason)
                {
                    await FailAsync(stream, TerminalError.NotAllowed, reason, cancellationToken).ConfigureAwait(false);
                    return;
                }

                switch (request.BodyCase)
                {
                    case TerminalRequest.BodyOneofCase.ListShells:
                        var shells = new ShellList();
                        shells.Shells.Add(pty.GetAvailableShells());
                        await SendAsync(stream, new TerminalEvent { Shells = shells }, cancellationToken).ConfigureAwait(false);
                        break;
                    case TerminalRequest.BodyOneofCase.Open:
                        open = request.Open;
                        break;
                    default:
                        await FailAsync(stream, TerminalError.Unsupported, "Open a terminal first.", cancellationToken).ConfigureAwait(false);
                        return;
                }
            }

            await RunAsync(stream, open, session, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or OperationCanceledException)
        {
            // The controller went away; the shell (if any) has been ended by RunAsync.
        }
    }

    private async Task RunAsync(Stream stream, OpenTerminal open, SessionContext session, CancellationToken cancellationToken)
    {
        var available = pty.GetAvailableShells();
        string? shell = null;
        if (open.Shell.Length > 0)
        {
            shell = available.FirstOrDefault(s => string.Equals(s, open.Shell, StringComparison.OrdinalIgnoreCase));
            if (shell is null)
            {
                await FailAsync(stream, TerminalError.ShellUnavailable, $"'{open.Shell}' is not available on this computer.", cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        if (Interlocked.Increment(ref _active) > MaxTerminals)
        {
            Interlocked.Decrement(ref _active);
            await FailAsync(stream, TerminalError.TooMany, "Too many terminals are open on this computer.", cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var columns = (int)Math.Clamp(open.Columns, 1, MaxColumns);
            var rows = (int)Math.Clamp(open.Rows, 1, MaxRows);
            IPtySession terminal;
            try
            {
                terminal = pty.Start(new PtyStartOptions(shell, [], null, columns, rows));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or Win32Exception or UnauthorizedAccessException)
            {
                LogStartFailed(logger, session.Id, ex);
                await FailAsync(stream, TerminalError.StartFailed, ex.Message, cancellationToken).ConfigureAwait(false);
                return;
            }

            var name = shell ?? (available.Count > 0 ? available[0] : string.Empty);
            LogOpened(logger, session.Id, name, columns, rows);
            await SendAsync(stream, new TerminalEvent { Opened = new TerminalOpened { Shell = name } }, cancellationToken).ConfigureAwait(false);
            await PumpAsync(stream, terminal, session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    private async Task PumpAsync(Stream stream, IPtySession terminal, SessionContext session, CancellationToken cancellationToken)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var output = PumpOutputAsync(terminal.Output, stream, run.Token);
        var input = PumpInputAsync(stream, terminal, run.Token);
        try
        {
            var first = await Task.WhenAny(output, input).ConfigureAwait(false);
            if (first == output && output.IsCompletedSuccessfully)
            {
                // The shell ended and everything it printed has been passed on.
                var code = await terminal.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                await SendAsync(stream, new TerminalEvent { Exited = new TerminalExited { ExitCode = code } }, cancellationToken).ConfigureAwait(false);
                LogExited(logger, session.Id, code);
            }
            else
            {
                LogClosed(logger, session.Id);
            }
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            LogClosed(logger, session.Id);
        }
        finally
        {
            await run.CancelAsync().ConfigureAwait(false);

            // Ending the terminal ends the shell and its output, which lets the pumps finish. Disposing it is what
            // unblocks a read that is already in flight, so a pump can end with the descriptor gone; that is the
            // expected way this ends. The failure still has to be observed: an exception nobody looks at is
            // rethrown on the finalizer thread and recorded as a crash even though the session simply ended.
            await terminal.DisposeAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(output, input).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }
    }

    private static async Task PumpOutputAsync(Stream output, Stream channel, CancellationToken cancellationToken)
    {
        var buffer = new byte[TerminalDataFrame.MaxLength];
        int read;
        while ((read = await output.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await TerminalDataFrame.WriteAsync(channel, buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task PumpInputAsync(Stream channel, IPtySession terminal, CancellationToken cancellationToken)
    {
        while (await FrameCodec.ReadAsync(channel, MaxRequestBytes, cancellationToken).ConfigureAwait(false) is { } frame)
        {
            if (frame.MessageType == TerminalDataFrame.MessageType)
            {
                await terminal.Input.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
                await terminal.Input.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (frame.MessageType == ProtobufFrames.EnvelopeMessageType)
            {
                var request = TerminalRequest.Parser.ParseFrom(frame.Payload.Span);
                if (request.BodyCase == TerminalRequest.BodyOneofCase.Resize)
                {
                    terminal.Resize((int)Math.Clamp(request.Resize.Columns, 1, MaxColumns), (int)Math.Clamp(request.Resize.Rows, 1, MaxRows));
                }
            }
        }
    }

    private static ValueTask SendAsync(Stream stream, TerminalEvent message, CancellationToken cancellationToken) =>
        ProtobufFrames.WriteAsync(stream, message, cancellationToken);

    private static ValueTask FailAsync(Stream stream, TerminalError error, string message, CancellationToken cancellationToken) =>
        SendAsync(stream, new TerminalEvent { Failed = new TerminalFailed { Error = error, Message = message } }, cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId}: terminal opened ({Shell}, {Columns}x{Rows}).")]
    private static partial void LogOpened(ILogger logger, SessionId sessionId, string shell, int columns, int rows);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId}: the shell exited with code {ExitCode}.")]
    private static partial void LogExited(ILogger logger, SessionId sessionId, int exitCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId}: terminal closed by the controller or the connection.")]
    private static partial void LogClosed(ILogger logger, SessionId sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session {SessionId}: the shell could not be started.")]
    private static partial void LogStartFailed(ILogger logger, SessionId sessionId, Exception exception);
}
