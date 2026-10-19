using System.IO.Pipes;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Application.Hosting;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>The UI ↔ service channel: the UI's client against the service's handler over the real transport.</summary>
public class HostControlTests
{
    public HostControlTests() =>
        Assert.SkipUnless(
            OperatingSystem.IsWindows(),
            "Windows only: macOS has no service/agent split to control (see docs/ports/README.md, \"进程模型：没有 Session 0\"), and .NET maps a " +
            "named pipe onto a Unix socket whose path cannot exceed 104 characters -- the temp path alone is already longer.");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_ui_reads_the_status_and_rotates_the_password_through_the_pipe()
    {
        var control = new FakeHostControl();
        var pipeName = $"rd-hostcontrol-{Guid.NewGuid():N}";
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var server = Serve(pipeName, control, stop.Token);
        var client = new PipeHostControl(pipeName);

        var status = await client.GetStatusAsync(Ct);
        Assert.Equal(123456789, status.DeviceId);
        Assert.Equal("Online", status.ServerState);
        Assert.Equal("pw-1", status.TemporaryPassword);
        Assert.Equal([new HostSessionInfo("abc", "987 654 321", "view", "direct", CameraActive: true)], status.Sessions);
        Assert.False(status.HasFixedPassword);
        Assert.True(status.IsService);

        await client.RotateTemporaryPasswordAsync(Ct);
        Assert.Equal("pw-2", (await client.GetStatusAsync(Ct)).TemporaryPassword);

        await stop.CancelAsync();
        await server;
    }

    [Fact]
    public async Task The_ui_can_downgrade_or_disconnect_a_named_session()
    {
        var control = new FakeHostControl();
        var pipeName = $"rd-hostcontrol-{Guid.NewGuid():N}";
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var server = Serve(pipeName, control, stop.Token);
        var client = new PipeHostControl(pipeName);

        await client.DowngradeSessionAsync("session-1", Ct);
        await client.DisconnectSessionAsync("session-2", Ct);

        Assert.Equal(["session-1"], control.Downgraded);
        Assert.Equal(["session-2"], control.Disconnected);

        await stop.CancelAsync();
        await server;
    }

    [Fact]
    public async Task The_ui_can_set_and_clear_fixed_password()
    {
        var control = new FakeHostControl();
        var pipeName = $"rd-hostcontrol-{Guid.NewGuid():N}";
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var server = Serve(pipeName, control, stop.Token);
        var client = new PipeHostControl(pipeName);

        Assert.False((await client.GetStatusAsync(Ct)).HasFixedPassword);

        await client.SetFixedPasswordAsync("my-unattended-pass", Ct);
        Assert.Equal("my-unattended-pass", control.FixedPassword);
        Assert.True((await client.GetStatusAsync(Ct)).HasFixedPassword);

        await client.SetFixedPasswordAsync(null, Ct);
        Assert.Null(control.FixedPassword);
        Assert.False((await client.GetStatusAsync(Ct)).HasFixedPassword);

        await stop.CancelAsync();
        await server;
    }

    [Fact]
    public async Task A_refused_fixed_password_reaches_the_caller_instead_of_looking_saved()
    {
        // The service answers "error" when its policy refuses the password. The client used to read the answer and
        // discard it, so the window closed as if the password had been stored while the host still had the old one.
        var control = new FakeHostControl { RefuseFixedPassword = true };
        var pipeName = $"rd-hostcontrol-{Guid.NewGuid():N}";
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var server = Serve(pipeName, control, stop.Token);
        var client = new PipeHostControl(pipeName);

        var refusal = await Assert.ThrowsAsync<ArgumentException>(() => client.SetFixedPasswordAsync("short", Ct));
        Assert.Contains("at least", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(control.FixedPassword);

        await stop.CancelAsync();
        await server;
    }

    [Fact]
    public async Task The_ui_can_stop_the_camera_of_one_session()
    {
        var control = new FakeHostControl();
        var pipeName = $"rd-hostcontrol-{Guid.NewGuid():N}";
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var server = Serve(pipeName, control, stop.Token);
        var client = new PipeHostControl(pipeName);

        await client.StopCameraAsync("session-3", Ct);

        Assert.Equal(["session-3"], control.CameraStopped);

        await stop.CancelAsync();
        await server;
    }

    [Fact]
    public async Task Availability_probe_tells_a_present_service_from_an_absent_one()
    {
        var pipeName = $"rd-hostcontrol-{Guid.NewGuid():N}";
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        Assert.False(await PipeHostControl.IsAvailableAsync(pipeName, Ct));

        var server = Serve(pipeName, new FakeHostControl(), stop.Token);
        Assert.True(await PipeHostControl.IsAvailableAsync(pipeName, Ct));

        await stop.CancelAsync();
        await server;
    }

    [Fact]
    public async Task Garbage_and_oversized_requests_are_ignored_without_hurting_the_server()
    {
        var control = new FakeHostControl();
        var pipeName = $"rd-hostcontrol-{Guid.NewGuid():N}";
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var server = Serve(pipeName, control, stop.Token);

        // No newline and too long: the server hangs up (possibly while the client is still writing).
        Assert.False(await GetsAnswerAsync(pipeName, new byte[HostControlProtocol.MaxLineBytes + 10].Select(_ => (byte)'x').ToArray()));

        // A command nobody defined: no answer either.
        Assert.False(await GetsAnswerAsync(pipeName, "{\"command\":\"format-disk\"}\n"u8.ToArray()));

        Assert.Equal(123456789, (await new PipeHostControl(pipeName).GetStatusAsync(Ct)).DeviceId); // still serving
        Assert.Equal(0, control.Rotations);

        await stop.CancelAsync();
        await server;
    }

    private static async Task<bool> GetsAnswerAsync(string pipeName, byte[] request)
    {
        await using var pipe = await HostControlEndpoint.ConnectAsync(HostControlEndpoint.Resolve(pipeName), TimeSpan.FromSeconds(5), Ct);
        try
        {
            await pipe.WriteAsync(request, Ct);
            return await pipe.ReadAsync(new byte[16], Ct) > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Accepts one connection after another, like the service's control server — and over the same transport, so that
    /// what is exercised here is the pair that ships rather than two halves that happen to meet on one platform. The
    /// address is resolved the same way on both ends; only the socket itself differs.
    /// </summary>
    private static async Task Serve(string pipeName, IHostControl control, CancellationToken stop)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                while (!stop.IsCancellationRequested)
                {
                    await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await pipe.WaitForConnectionAsync(stop);
                    await HostControlHandler.HandleAsync(pipe, control, NullLogger.Instance, stop);
                }
            }
            else
            {
                await using var listener = new HostControlSocketListener(HostControlEndpoint.Resolve(pipeName));
                while (!stop.IsCancellationRequested)
                {
                    await using var stream = await listener.AcceptAsync(stop);
                    await HostControlHandler.HandleAsync(stream, control, NullLogger.Instance, stop);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class FakeHostControl : IHostControl
    {
        public int Rotations { get; private set; }

        public Task<HostStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new HostStatus(
                123456789,
                "Online",
                $"pw-{Rotations + 1}",
                [new HostSessionInfo("abc", "987 654 321", "view", "direct", CameraActive: true)],
                "Running (User)",
                true,
                !string.IsNullOrEmpty(FixedPassword)));

        public Task RotateTemporaryPasswordAsync(CancellationToken cancellationToken)
        {
            Rotations++;
            return Task.CompletedTask;
        }

        public List<string> Downgraded { get; } = [];

        public string? FixedPassword { get; private set; }

        /// <summary>Stands in for a service whose policy is newer than the window's: the refusal is the service's.</summary>
        public bool RefuseFixedPassword { get; init; }

        public Task SetFixedPasswordAsync(string? password, CancellationToken cancellationToken)
        {
            if (RefuseFixedPassword)
            {
                throw new ArgumentException("A fixed password must be at least 8 characters.", nameof(password));
            }

            FixedPassword = password;
            return Task.CompletedTask;
        }

        public List<string> Disconnected { get; } = [];

        public List<string> CameraStopped { get; } = [];

        public bool? CameraAllowed { get; private set; }

        public Task SetCameraAccessAsync(bool allowed, CancellationToken cancellationToken)
        {
            CameraAllowed = allowed;
            return Task.CompletedTask;
        }

        public Task StopCameraAsync(string sessionId, CancellationToken cancellationToken)
        {
            CameraStopped.Add(sessionId);
            return Task.CompletedTask;
        }

        public Task DowngradeSessionAsync(string sessionId, CancellationToken cancellationToken)
        {
            Downgraded.Add(sessionId);
            return Task.CompletedTask;
        }

        public Task DisconnectSessionAsync(string sessionId, CancellationToken cancellationToken)
        {
            Disconnected.Add(sessionId);
            return Task.CompletedTask;
        }
    }
}
