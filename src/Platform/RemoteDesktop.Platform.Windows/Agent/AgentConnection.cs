using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using RemoteDesktop.Abstractions.Agent;

namespace RemoteDesktop.Platform.Windows.Agent;

/// <summary>
/// Service side of the pipes to one launched agent. The pipe exists before the agent starts; only the process the
/// service launched may use it (checked by the client's process id, not just by account), so another program of the
/// same user cannot pose as the agent and read the controller's keystrokes.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class AgentConnection : IAsyncDisposable
{
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ChannelTimeout = TimeSpan.FromSeconds(10);

    private readonly string _pipeName;
    private readonly IReadOnlyList<SecurityIdentifier> _clients;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<Stream>> _pending = new();
    private readonly TaskCompletionSource<int> _expectedProcess = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _controlWrite = new(1, 1);
    private readonly Task _acceptLoop;
    private Stream? _control;

    public AgentConnection(string pipeName, IReadOnlyList<SecurityIdentifier> clients, ILogger logger)
    {
        _pipeName = pipeName;
        _clients = clients;
        _logger = logger;

        // The first instance is created here, before the agent is launched, so it always finds the pipe.
        _acceptLoop = AcceptLoopAsync(WindowsPipes.CreateServer(pipeName, clients));
    }

    public string PipeName => _pipeName;

    /// <summary>Completes when the agent's control connection is established.</summary>
    public Task Connected => _connected.Task;

    /// <summary>Completes when the control connection ends (agent exited or was shut down).</summary>
    public Task Closed => _closed.Task;

    public event EventHandler<DesktopKind>? DesktopChanged;

    public event EventHandler? SecureAttentionRequested;

    /// <summary>Tells the connection which process is the agent; call right after launching it.</summary>
    public void ExpectProcess(int processId) => _expectedProcess.TrySetResult(processId);

    /// <exception cref="AgentUnavailableException">The agent is not connected or went away.</exception>
    public async Task<Stream> OpenChannelAsync(AgentChannelKind kind, CancellationToken cancellationToken)
    {
        var control = _control ?? throw new AgentUnavailableException("The agent is not connected.");
        var id = Guid.NewGuid();
        var completion = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await _controlWrite.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await AgentPipeMessages.WriteMessageAsync(control, AgentPipeMessage.OpenChannel, AgentPipeMessages.OpenChannelPayload(id, kind), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                throw new AgentUnavailableException("The agent went away.", ex);
            }
            finally
            {
                _controlWrite.Release();
            }

            try
            {
                return await completion.Task.WaitAsync(ChannelTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new AgentUnavailableException("The agent did not open the channel.", ex);
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_control is { } control)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                await AgentPipeMessages.WriteMessageAsync(control, AgentPipeMessage.Shutdown, ReadOnlyMemory<byte>.Empty, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // Already gone; the process is killed by the supervisor anyway.
            }
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        FailPending();
        if (_control is { } stream)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        await _acceptLoop.ConfigureAwait(false);
        _stop.Dispose();
        _controlWrite.Dispose();
    }

    private async Task AcceptLoopAsync(NamedPipeServerStream first)
    {
        var server = first;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                var connected = server;

                // A fresh instance is listening before the connected one is handled, so the next client never waits.
                server = WindowsPipes.CreateServer(_pipeName, _clients);
                _ = HandleAsync(connected);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(NamedPipeServerStream stream)
    {
        var handedOver = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(HelloTimeout);

            var expected = await _expectedProcess.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(stream.SafePipeHandle, out var clientProcess) || clientProcess != expected)
            {
                LogForeignClient(_logger, (int)clientProcess, expected);
                return;
            }

            var hello = await AgentPipeMessages.ReadHelloAsync(stream, timeout.Token).ConfigureAwait(false);
            if (hello is null || hello.Value.ProcessId != expected)
            {
                return;
            }

            if (hello.Value.Role == AgentPipeRole.Control)
            {
                if (Interlocked.CompareExchange(ref _control, stream, null) is not null)
                {
                    return;
                }

                handedOver = true;
                await AgentPipeMessages.WriteMessageAsync(stream, AgentPipeMessage.HelloAck, new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
                _connected.TrySetResult();
                await ReadControlAsync(stream).ConfigureAwait(false);
            }
            else if (_pending.TryGetValue(hello.Value.ChannelId, out var completion))
            {
                await AgentPipeMessages.WriteMessageAsync(stream, AgentPipeMessage.HelloAck, new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
                handedOver = completion.TrySetResult(stream);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or OperationCanceledException or EndOfStreamException)
        {
        }
        finally
        {
            if (!handedOver)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ReadControlAsync(Stream control)
    {
        try
        {
            while (await AgentPipeMessages.ReadMessageAsync(control, _stop.Token).ConfigureAwait(false) is { } message)
            {
                switch (message.Type)
                {
                    case AgentPipeMessage.DesktopChanged when message.Payload is [var kind] && Enum.IsDefined((DesktopKind)kind):
                        DesktopChanged?.Invoke(this, (DesktopKind)kind);
                        break;
                    case AgentPipeMessage.SecureAttention:
                        SecureAttentionRequested?.Invoke(this, EventArgs.Empty);
                        break;
                    default:
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or OperationCanceledException)
        {
        }
        finally
        {
            FailPending();
            _closed.TrySetResult();
        }
    }

    private void FailPending()
    {
        foreach (var completion in _pending.Values)
        {
            completion.TrySetException(new AgentUnavailableException("The agent went away."));
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a connection to the agent pipe from process {Client}; expected the agent, process {Expected}.")]
    private static partial void LogForeignClient(ILogger logger, int client, int expected);
}
