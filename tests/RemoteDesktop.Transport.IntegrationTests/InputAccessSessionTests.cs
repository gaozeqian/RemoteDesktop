using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Domain;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// The operating system's control permission over a real session: the host reports it, a change reaches the
/// controller, and the controller's retry reaches the host.
/// </summary>
public sealed class InputAccessSessionTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_host_reports_input_authorization_and_a_retry_reaches_it()
    {
        var access = new FakeInputAccess(new InputAccessStatus(InputAccessState.NotRequested));

        await using var host = await TestDevice.StartAsync(server, forceRelay: false, access);
        await using var controller = await TestDevice.StartAsync(server);
        await using var session = await controller.Controller.ConnectAsync(
            host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);

        // The state is there before the controller has sent any input: the UI must be able to explain what will
        // happen the first time somebody types, not only after it has happened.
        await WaitForAsync(() => session.InputAccess?.State == InputAccessState.NotRequested);

        access.Set(new InputAccessStatus(InputAccessState.Denied, "the desktop said no"));
        await WaitForAsync(() => session.InputAccess?.State == InputAccessState.Denied);
        Assert.Equal("the desktop said no", session.InputAccess?.Reason);

        await session.RequestInputAccessAsync(Ct);
        await WaitForAsync(() => access.Requests > 0);

        access.Set(new InputAccessStatus(InputAccessState.Granted));
        await WaitForAsync(() => session.InputAccess?.State == InputAccessState.Granted);
    }

    [Fact]
    public async Task The_host_reports_input_unavailable_when_service_is_missing()
    {
        var access = new FakeInputAccess(new InputAccessStatus(InputAccessState.NotRequested));

        await using var host = await TestDevice.StartAsync(server, forceRelay: false, access);
        await using var controller = await TestDevice.StartAsync(server);
        await using var session = await controller.Controller.ConnectAsync(
            host.Id, host.Credentials.TemporaryPassword, SessionMode.Control, Ct);

        await WaitForAsync(() => session.InputAccess?.State == InputAccessState.NotRequested);

        access.Set(new InputAccessStatus(InputAccessState.Unavailable, "libei socket not found"));
        await WaitForAsync(() => session.InputAccess?.State == InputAccessState.Unavailable);
        Assert.Equal("libei socket not found", session.InputAccess?.Reason);
    }

    [Fact]
    public async Task A_watch_only_session_is_not_told_about_input_authorization()
    {
        var access = new FakeInputAccess(new InputAccessStatus(InputAccessState.Denied, "no"));

        await using var host = await TestDevice.StartAsync(server, forceRelay: false, access);
        await using var controller = await TestDevice.StartAsync(server);
        await using var session = await controller.Controller.ConnectAsync(
            host.Id, host.Credentials.TemporaryPassword, SessionMode.View, Ct);

        // Watching has no input to authorize. A pause would only say that nothing had arrived yet: the host writes
        // its start-up status on the same control channel that carries ping/pong, so a completed round trip proves
        // anything it sent when the session started has already been delivered.
        var rtt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (session.RoundTripTime is not null)
        {
            rtt.TrySetResult();
        }
        else
        {
            session.RoundTripTimeChanged += (_, _) => rtt.TrySetResult();
        }

        await rtt.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Null(session.InputAccess);
        Assert.Equal(0, access.Requests);

        // Stronger than the absence of a message: the host did not even ask this machine how input stands.
        Assert.Equal(0, access.StatusReads);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The expected input-authorization state did not arrive.");
            }

            await Task.Delay(20, Ct);
        }
    }

    private sealed class FakeInputAccess(InputAccessStatus initial) : IInputAccess
    {
        private InputAccessStatus _status = initial;
        private int _requests;
        private int _statusReads;

        public int Requests => Volatile.Read(ref _requests);

        /// <summary>How often the host asked what state this machine is in, as opposed to waiting to be told.</summary>
        public int StatusReads => Volatile.Read(ref _statusReads);

        public InputAccessStatus Status
        {
            get
            {
                Interlocked.Increment(ref _statusReads);
                return Volatile.Read(ref _status);
            }
        }

        public event EventHandler<InputAccessStatus>? StatusChanged;

        public Task<InputAccessStatus> RequestAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(Status);
        }

        public void Set(InputAccessStatus status)
        {
            Volatile.Write(ref _status, status);
            StatusChanged?.Invoke(this, status);
        }
    }
}
