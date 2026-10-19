using Microsoft.Extensions.Logging;

namespace RemoteDesktop.Application.Hosting;

/// <summary>Service side of the UI ↔ service pipe protocol: answers one request on an already connected stream.</summary>
public static partial class HostControlHandler
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Reads a request, runs it against <paramref name="control"/> and writes the response. A peer that goes away or
    /// sends garbage just ends the exchange; nothing is thrown at the caller.
    /// </summary>
    public static async Task HandleAsync(Stream stream, IHostControl control, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            var request = await HostControlProtocol.ReadLineAsync<HostControlRequest>(stream, timeout.Token).ConfigureAwait(false);
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
                    await control.DowngradeSessionAsync(sessionId, timeout.Token).ConfigureAwait(false);
                    await HostControlProtocol.WriteLineAsync(stream, new HostControlRequest("ok"), timeout.Token).ConfigureAwait(false);
                    break;
                case HostControlProtocol.DisconnectCommand when request.Argument is { } sessionId:
                    await control.DisconnectSessionAsync(sessionId, timeout.Token).ConfigureAwait(false);
                    await HostControlProtocol.WriteLineAsync(stream, new HostControlRequest("ok"), timeout.Token).ConfigureAwait(false);
                    break;
                case HostControlProtocol.CameraStopCommand when request.Argument is { } sessionId:
                    await control.StopCameraAsync(sessionId, timeout.Token).ConfigureAwait(false);
                    await HostControlProtocol.WriteLineAsync(stream, new HostControlRequest("ok"), timeout.Token).ConfigureAwait(false);
                    break;
                case HostControlProtocol.CameraPermissionCommand:
                    try
                    {
                        await control.SetCameraAccessAsync(
                            string.Equals(request.Argument, HostControlProtocol.AllowCamera, StringComparison.Ordinal),
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
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
            // The UI went away or sent garbage; nothing to answer.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unknown host control command '{Command}'.")]
    private static partial void LogUnknownCommand(ILogger logger, string? command);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a fixed password: {Reason}")]
    private static partial void LogRefusedFixedPassword(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a camera-permission change: {Reason}")]
    private static partial void LogRefusedCameraPermission(ILogger logger, string reason);
}
