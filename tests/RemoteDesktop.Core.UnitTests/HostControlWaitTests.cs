using System.Diagnostics;
using System.IO.Pipes;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Application.Hosting;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The start-up wait that keeps a client from hosting incoming sessions when the background service was merely a
/// second slower to create its control socket.
/// </summary>
public class HostControlWaitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // On macOS a pipe is a Unix socket under a per-user temp directory that is already about 70 characters long
    // (/var/folders/xx/.../T/remotedesktop-runner/), and a socket path is limited to 104. A full GUID in the name
    // pushed it over, so the name stays short; 12 hex digits are plenty for a test that runs once per process.
    private static string ShortUniqueName() => "rd-hcw-" + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task A_service_that_starts_after_the_first_probe_is_found()
    {
        var pipeName = ShortUniqueName();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var server = ServeAfterAsync(pipeName, TimeSpan.FromMilliseconds(300), stop.Token);

        Assert.True(await PipeHostControl.WaitUntilAvailableAsync(pipeName, TimeSpan.FromSeconds(3), Environment.ProcessPath, Ct));

        await stop.CancelAsync();
        await server;
    }

    [Fact]
    public async Task Waiting_gives_up_at_the_deadline()
    {
        var pipeName = ShortUniqueName();
        var elapsed = Stopwatch.StartNew();

        Assert.False(await PipeHostControl.WaitUntilAvailableAsync(pipeName, TimeSpan.FromMilliseconds(500), Environment.ProcessPath, Ct));

        Assert.InRange(elapsed.Elapsed, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(5));
    }

    private static async Task ServeAfterAsync(string pipeName, TimeSpan delay, CancellationToken stop)
    {
        try
        {
            await Task.Delay(delay, stop);

            if (OperatingSystem.IsWindows())
            {
                await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(stop);
                await HostControlHandler.HandleAsync(pipe, new FakeHostControl(), NullLogger.Instance, stop);
            }
            else
            {
                await using var listener = new HostControlSocketListener(HostControlEndpoint.Resolve(pipeName));
                await using var stream = await listener.AcceptAsync(stop);
                await HostControlHandler.HandleAsync(stream, new FakeHostControl(), NullLogger.Instance, stop);
            }
        }
        catch (OperationCanceledException)
        {
            // The test is done; the server side of one answer has nothing left to do.
        }
    }

    private sealed class FakeHostControl : IHostControl
    {
        public Task<HostStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new HostStatus(123456789, "Online", "pw", [], "Running (User)", true));

        public Task RotateTemporaryPasswordAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DowngradeSessionAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DisconnectSessionAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopCameraAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetFixedPasswordAsync(string? password, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetCameraAccessAsync(bool allowed, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
