using System.IO.Pipes;

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
        await RequestAsync<HostStatus>(HostControlProtocol.StatusCommand, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidDataException("The host service sent no status.");

    public async Task RotateTemporaryPasswordAsync(CancellationToken cancellationToken) =>
        _ = await RequestAsync<HostControlRequest>(HostControlProtocol.RotateCommand, cancellationToken).ConfigureAwait(false);

    private async Task<T?> RequestAsync<T>(string command, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await using (pipe.ConfigureAwait(false))
        {
            await pipe.ConnectAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
            await HostControlProtocol.WriteLineAsync(pipe, new HostControlRequest(command), cancellationToken).ConfigureAwait(false);
            return await HostControlProtocol.ReadLineAsync<T>(pipe, cancellationToken).ConfigureAwait(false);
        }
    }
}
