using Microsoft.Extensions.Logging;

namespace RemoteDesktop.Application.Hosting;

/// <summary>Service side of the UI ↔ service pipe protocol: answers one request on an already connected stream.</summary>
public static partial class HostControlHandler
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Reads a request, runs it against <paramref name="control"/> and writes the response. A peer that goes away or
    /// sends garbage just ends the exchange. Failures of <paramref name="control"/> itself that the protocol has no
    /// answer for (for example a camera state query that throws) are not swallowed here: they propagate, and the
    /// caller is responsible for logging them.
    /// </summary>
    public static Task HandleAsync(Stream stream, IHostControl control, ILogger logger, CancellationToken cancellationToken) =>
        HandleAsync(stream, control, logger, authorize: null, cancellationToken);

    /// <summary>Reads one request, authenticates the local caller, then dispatches it.</summary>
    public static async Task HandleAsync(
        Stream stream,
        IHostControl control,
        ILogger logger,
        Action? authorize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            var request = await HostControlProtocol.ReadLineAsync<HostControlRequest>(stream, timeout.Token).ConfigureAwait(false);
            if (request is not null)
            {
                // Windows only permits impersonation after the client has written. The complete request is now available,
                // but no command or secret-bearing operation has been dispatched yet.
                authorize?.Invoke();
            }

            switch (request?.Command)
            {
                case HostControlProtocol.StatusCommand:
                    var status = await control.GetStatusAsync(timeout.Token).ConfigureAwait(false);
                    await HostControlProtocol.WriteLineAsync(stream, status, timeout.Token).ConfigureAwait(false);
                    break;
                case HostControlProtocol.RotateCommand:
                    await control.RotateTemporaryPasswordAsync(timeout.Token).ConfigureAwait(false);
                    await HostControlProtocol.WriteLineAsync(stream, new HostControlRequest("ok"), timeout.Token).ConfigureAwait(false);
                    break;
                case HostControlProtocol.DowngradeCommand when request.Argument is { } sessionId:
                    await AnswerAsync(
                        stream,
                        "downgrade",
                        logger,
                        () => control.DowngradeSessionAsync(sessionId, timeout.Token),
                        timeout.Token).ConfigureAwait(false);
                    break;
                case HostControlProtocol.DisconnectCommand when request.Argument is { } sessionId:
                    await AnswerAsync(
                        stream,
                        "disconnect",
                        logger,
                        () => control.DisconnectSessionAsync(sessionId, timeout.Token),
                        timeout.Token).ConfigureAwait(false);
                    break;
                case HostControlProtocol.CameraStopCommand when request.Argument is { } sessionId:
                    await AnswerAsync(
                        stream,
                        "camera-stop",
                        logger,
                        () => control.StopCameraAsync(sessionId, timeout.Token),
                        timeout.Token).ConfigureAwait(false);
                    break;
                case HostControlProtocol.CameraPermissionCommand:
                    if (!string.Equals(request.Argument, HostControlProtocol.AllowCamera, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(request.Argument, HostControlProtocol.DenyCamera, StringComparison.OrdinalIgnoreCase))
                    {
                        await HostControlProtocol.WriteLineAsync(
                            stream,
                            new HostControlRequest(HostControlProtocol.ErrorCommand, "The camera permission must be 'allow' or 'deny'."),
                            timeout.Token).ConfigureAwait(false);
                        break;
                    }

                    try
                    {
                        await control.SetCameraAccessAsync(
                            string.Equals(request.Argument, HostControlProtocol.AllowCamera, StringComparison.OrdinalIgnoreCase),
                            timeout.Token).ConfigureAwait(false);
                        await HostControlProtocol.WriteLineAsync(stream, new HostControlRequest("ok"), timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
                    {
                        LogRefusedCameraPermission(logger, ex.Message);
                        await HostControlProtocol.WriteLineAsync(
                            stream,
                            new HostControlRequest(HostControlProtocol.ErrorCommand, ex.Message),
                            timeout.Token).ConfigureAwait(false);
                    }

                    break;
                case HostControlProtocol.FixedPasswordCommand:
                    try
                    {
                        await control.SetFixedPasswordAsync(request.Argument, timeout.Token).ConfigureAwait(false);
                        await HostControlProtocol.WriteLineAsync(stream, new HostControlRequest("ok"), timeout.Token).ConfigureAwait(false);
                    }
                    catch (ArgumentException ex)
                    {
                        // The rule is the service's, not the window's; the answer carries the reason back.
                        LogRefusedFixedPassword(logger, ex.Message);
                        await HostControlProtocol.WriteLineAsync(stream, new HostControlRequest(HostControlProtocol.ErrorCommand, ex.Message), timeout.Token).ConfigureAwait(false);
                    }

                    break;
                default:
                    LogUnknownCommand(logger, request?.Command);
                    if (request is not null)
                    {
                        await HostControlProtocol.WriteLineAsync(
                            stream,
                            new HostControlRequest(HostControlProtocol.ErrorCommand, $"Unknown command '{request.Command}'."),
                            timeout.Token).ConfigureAwait(false);
                    }

                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
            // The UI went away or sent garbage; nothing to answer.
        }
    }

    /// <summary>Runs one session command and always writes a reply: ok, or the argument error it was refused with.</summary>
    private static async Task AnswerAsync(
        Stream stream,
        string command,
        ILogger logger,
        Func<Task> run,
        CancellationToken cancellationToken)
    {
        try
        {
            await run().ConfigureAwait(false);
            await HostControlProtocol.WriteLineAsync(stream, new HostControlRequest("ok"), cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            LogRefusedCommand(logger, command, ex.Message);
            await HostControlProtocol.WriteLineAsync(
                stream,
                new HostControlRequest(HostControlProtocol.ErrorCommand, ex.Message),
                cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unknown host control command '{Command}'.")]
    private static partial void LogUnknownCommand(ILogger logger, string? command);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused host control command {Command}: {Reason}")]
    private static partial void LogRefusedCommand(ILogger logger, string command, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a fixed password: {Reason}")]
    private static partial void LogRefusedFixedPassword(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a camera-permission change: {Reason}")]
    private static partial void LogRefusedCameraPermission(ILogger logger, string reason);
}
