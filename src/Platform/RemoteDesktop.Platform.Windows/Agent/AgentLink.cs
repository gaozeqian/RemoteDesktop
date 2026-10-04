using RemoteDesktop.Domain;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Agent;

namespace RemoteDesktop.Platform.Windows.Agent;

/// <summary>A feature channel the service wants the agent to serve.</summary>
public readonly record struct AgentChannelRequest(AgentChannelKind Kind, Guid ChannelId, SessionId Session);

/// <summary>
/// Agent side of the pipes to the host service: one control connection, plus one data connection per feature channel.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentLink : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly string _pipeName;
    private readonly NamedPipeClientStream _control;
    private readonly SemaphoreSlim _controlWrite = new(1, 1);

    private AgentLink(string pipeName, NamedPipeClientStream control)
    {
        _pipeName = pipeName;
        _control = control;
    }

    /// <exception cref="IOException">The service refused the connection.</exception>
    public static async Task<AgentLink> ConnectAsync(string pipeName, CancellationToken cancellationToken)
    {
        var control = await ConnectPipeAsync(pipeName, AgentPipeRole.Control, Guid.Empty, cancellationToken).ConfigureAwait(false);
        return new AgentLink(pipeName, control);
    }

    /// <summary>Channel requests from the service, until it shuts the agent down or the pipe closes.</summary>
    public async IAsyncEnumerable<AgentChannelRequest> ReadRequestsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await ReadMessageOrEndAsync(cancellationToken).ConfigureAwait(false) is { } message)
        {
            if (message.Type == AgentPipeMessage.Shutdown)
            {
                yield break;
            }

            if (message.Type == AgentPipeMessage.OpenChannel && message.Payload.Length == AgentPipeMessages.OpenChannelPayloadLength && Enum.IsDefined((AgentChannelKind)message.Payload[16]))
            {
                yield return new AgentChannelRequest((AgentChannelKind)message.Payload[16], new Guid(message.Payload.AsSpan(0, 16), bigEndian: true), new SessionId(new Guid(message.Payload.AsSpan(17, 16), bigEndian: true)));
            }
        }
    }

    /// <summary>Connects the data pipe of a requested channel; the stream is the channel's byte stream.</summary>
    public async Task<Stream> OpenChannelAsync(AgentChannelRequest request, CancellationToken cancellationToken) =>
        await ConnectPipeAsync(_pipeName, AgentPipeRole.Data, request.ChannelId, cancellationToken).ConfigureAwait(false);

    public Task ReportDesktopAsync(DesktopKind desktop, CancellationToken cancellationToken) =>
        SendAsync(AgentPipeMessage.DesktopChanged, new[] { (byte)desktop }, cancellationToken);

    /// <summary>Asks the service to raise Ctrl+Alt+Del, which only it is allowed to do.</summary>
    public Task RequestSecureAttentionAsync(CancellationToken cancellationToken) =>
        SendAsync(AgentPipeMessage.SecureAttention, ReadOnlyMemory<byte>.Empty, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _control.DisposeAsync().ConfigureAwait(false);
        _controlWrite.Dispose();
    }

    private async Task SendAsync(AgentPipeMessage type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await _controlWrite.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AgentPipeMessages.WriteMessageAsync(_control, type, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _controlWrite.Release();
        }
    }

    private async ValueTask<(AgentPipeMessage Type, byte[] Payload)?> ReadMessageOrEndAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await AgentPipeMessages.ReadMessageAsync(_control, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
        {
            return null;
        }
    }

    private static async Task<NamedPipeClientStream> ConnectPipeAsync(string pipeName, AgentPipeRole role, Guid channelId, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
            await AgentPipeMessages.WriteHelloAsync(pipe, role, Environment.ProcessId, channelId, cancellationToken).ConfigureAwait(false);
            var ack = await AgentPipeMessages.ReadMessageAsync(pipe, cancellationToken).ConfigureAwait(false);
            if (ack is not { Type: AgentPipeMessage.HelloAck, Payload: [1] })
            {
                throw new IOException("The host service refused the agent connection.");
            }

            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
