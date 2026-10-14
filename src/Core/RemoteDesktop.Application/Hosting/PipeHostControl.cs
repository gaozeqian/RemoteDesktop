namespace RemoteDesktop.Application.Hosting;

/// <summary>UI side of the background service's control pipe.</summary>
public sealed class PipeHostControl(string pipeName = HostControlProtocol.PipeName) : IHostControl
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(800);

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

    public async Task SetFixedPasswordAsync(string? password, CancellationToken cancellationToken) =>
        _ = await RequestAsync<HostControlRequest>(HostControlProtocol.FixedPasswordCommand, password, cancellationToken).ConfigureAwait(false);

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
            return await HostControlProtocol.ReadLineAsync<T>(stream, cancellationToken).ConfigureAwait(false);
        }
    }
}
