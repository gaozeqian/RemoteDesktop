namespace RemoteDesktop.Application.Hosting;

/// <summary>UI side of the background service's control pipe.</summary>
public sealed class PipeHostControl(string pipeName = HostControlProtocol.PipeName) : IHostControl
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(800);

    /// <summary>
    /// How long the service has to answer once connected. The pipe name is global on Windows, so another local
    /// process can own an instance and simply never reply; without this the client waited before its window appeared,
    /// with no way to tell a wedged service from a slow one.
    /// </summary>
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>True when a host service answers on its control pipe.</summary>
    public static async Task<bool> IsAvailableAsync(string pipeName, CancellationToken cancellationToken)
    {
        try
        {
            await new PipeHostControl(pipeName).GetStatusAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<HostStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        await RequestAsync<HostStatus>(HostControlProtocol.StatusCommand, argument: null, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidDataException("The host service sent no status.");

    public async Task RotateTemporaryPasswordAsync(CancellationToken cancellationToken) =>
        _ = await RequestAsync<HostControlRequest>(HostControlProtocol.RotateCommand, argument: null, cancellationToken).ConfigureAwait(false);

    public async Task DowngradeSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        _ = await RequestAsync<HostControlRequest>(HostControlProtocol.DowngradeCommand, sessionId, cancellationToken).ConfigureAwait(false);

    public async Task DisconnectSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        _ = await RequestAsync<HostControlRequest>(HostControlProtocol.DisconnectCommand, sessionId, cancellationToken).ConfigureAwait(false);

    public async Task StopCameraAsync(string sessionId, CancellationToken cancellationToken) =>
        _ = await RequestAsync<HostControlRequest>(HostControlProtocol.CameraStopCommand, sessionId, cancellationToken).ConfigureAwait(false);

    public async Task SetFixedPasswordAsync(string? password, CancellationToken cancellationToken)
    {
        var answer = await RequestAsync<HostControlRequest>(HostControlProtocol.FixedPasswordCommand, password, cancellationToken)
            .ConfigureAwait(false);

        // The service validates the password against the policy it was built with, and answers "error" with the
        // reason. Discarding that answer made a refused password look saved; the window's own validation is a
        // courtesy, not the authority.
        if (answer is null)
        {
            throw new InvalidDataException("The host service sent no answer to the fixed-password command.");
        }

        if (string.Equals(answer.Command, HostControlProtocol.ErrorCommand, StringComparison.Ordinal))
        {
            throw new ArgumentException(answer.Argument ?? "The host refused the fixed password.");
        }
    }

    public async Task SetCameraAccessAsync(bool allowed, CancellationToken cancellationToken)
    {
        var answer = await RequestAsync<HostControlRequest>(
            HostControlProtocol.CameraPermissionCommand,
            allowed ? HostControlProtocol.AllowCamera : HostControlProtocol.DenyCamera,
            cancellationToken).ConfigureAwait(false);

        if (answer is null)
        {
            throw new InvalidDataException("The host service sent no answer to the camera-permission command.");
        }

        if (string.Equals(answer.Command, HostControlProtocol.ErrorCommand, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(answer.Argument ?? "The host refused the camera permission.");
        }
    }

    private async Task<T?> RequestAsync<T>(string command, string? argument, CancellationToken cancellationToken)
    {
        // The address is a named pipe on Windows and a Unix domain socket elsewhere; see HostControlEndpoint for why
        // the .NET named pipe implementation is not used on the other side of that line.
        var stream = await HostControlEndpoint
            .ConnectAsync(HostControlEndpoint.Resolve(pipeName), ConnectTimeout, cancellationToken)
            .ConfigureAwait(false);

        await using (stream.ConfigureAwait(false))
        {
            await HostControlProtocol.WriteLineAsync(stream, new HostControlRequest(command, argument), cancellationToken).ConfigureAwait(false);
            using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            responseTimeout.CancelAfter(ResponseTimeout);
            try
            {
                return await HostControlProtocol.ReadLineAsync<T>(stream, responseTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"The host service did not answer within {ResponseTimeout.TotalSeconds:0} seconds.");
            }
        }
    }
}
