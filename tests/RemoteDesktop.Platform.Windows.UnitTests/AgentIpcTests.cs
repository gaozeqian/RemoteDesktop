using RemoteDesktop.Domain;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Agent;
using RemoteDesktop.Platform.Windows.Agent;

namespace RemoteDesktop.Platform.Windows.UnitTests;

/// <summary>
/// The pipes between the host service and the session agent, with both ends in this process (so the "launched agent"
/// is this very process) plus the development launcher that needs no privileges.
/// </summary>
public class AgentIpcTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewPipeName() => $"RemoteDesktop.Agent.Test.{Guid.NewGuid():N}";

    private static SecurityIdentifier[] CurrentUser() => [WindowsIdentity.GetCurrent().User!];

    private static AccessGrant ControlGrant() => AccessGrant.Create(SessionMode.Control, Permissions.All, Permissions.All);

    [Fact]
    public async Task The_launched_agent_connects_and_a_channel_carries_bytes_both_ways()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Named pipe ACLs are Windows only.");
        await using var connection = new AgentConnection(NewPipeName(), CurrentUser(), NullLogger.Instance);
        connection.ExpectProcess(Environment.ProcessId);

        await using var link = await AgentLink.ConnectAsync(connection.PipeName, Ct);
        await connection.Connected.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        var session = SessionId.New();
        var grant = ControlGrant();
        var agentSide = Task.Run(async () =>
        {
            await foreach (var request in link.ReadRequestsAsync(Ct))
            {
                Assert.Equal(AgentChannelKind.Input, request.Kind);
                Assert.Equal(session, request.Session); // the agent can tell which session a channel serves (audit log)
                Assert.Equal(grant, request.Grant); // and what the session was allowed to do
                await using var stream = await link.OpenChannelAsync(request, Ct);
                var buffer = new byte[5];
                await stream.ReadExactlyAsync(buffer, Ct);
                await stream.WriteAsync(buffer.Reverse().ToArray(), Ct);
                return;
            }
        }, Ct);

        await using var channel = await connection.OpenChannelAsync(AgentChannelKind.Input, session, grant, Ct);
        await channel.WriteAsync(new byte[] { 1, 2, 3, 4, 5 }, Ct);
        var echoed = new byte[5];
        await channel.ReadExactlyAsync(echoed, Ct);

        Assert.Equal(new byte[] { 5, 4, 3, 2, 1 }, echoed);
        await agentSide.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task A_process_that_is_not_the_launched_agent_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Named pipe ACLs are Windows only.");
        await using var connection = new AgentConnection(NewPipeName(), CurrentUser(), NullLogger.Instance);
        connection.ExpectProcess(Environment.ProcessId + 1); // someone else

        await Assert.ThrowsAsync<IOException>(async () => await AgentLink.ConnectAsync(connection.PipeName, Ct));

        Assert.False(connection.Connected.IsCompleted);
    }

    [Fact]
    public async Task Desktop_changes_and_ctrl_alt_del_requests_reach_the_service()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Named pipe ACLs are Windows only.");
        await using var connection = new AgentConnection(NewPipeName(), CurrentUser(), NullLogger.Instance);
        connection.ExpectProcess(Environment.ProcessId);
        var desktop = new TaskCompletionSource<DesktopKind>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attention = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.DesktopChanged += (_, kind) => desktop.TrySetResult(kind);
        connection.SecureAttentionRequested += (_, _) => attention.TrySetResult();

        await using var link = await AgentLink.ConnectAsync(connection.PipeName, Ct);
        await link.ReportDesktopAsync(DesktopKind.Secure, Ct);
        await link.RequestSecureAttentionAsync(Ct);

        Assert.Equal(DesktopKind.Secure, await desktop.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        await attention.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task Losing_the_agent_completes_Closed_and_fails_pending_channel_requests()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Named pipe ACLs are Windows only.");
        await using var connection = new AgentConnection(NewPipeName(), CurrentUser(), NullLogger.Instance);
        connection.ExpectProcess(Environment.ProcessId);
        var link = await AgentLink.ConnectAsync(connection.PipeName, Ct);
        await connection.Connected.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        // The agent never answers the channel request; then it dies.
        var opening = connection.OpenChannelAsync(AgentChannelKind.Video, SessionId.New(), ControlGrant(), Ct);
        await Task.Delay(200, Ct);
        await link.DisposeAsync();

        await connection.Closed.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await Assert.ThrowsAsync<AgentUnavailableException>(() => opening);
        await Assert.ThrowsAsync<AgentUnavailableException>(() => connection.OpenChannelAsync(AgentChannelKind.Video, SessionId.New(), ControlGrant(), Ct));
    }

    [Fact]
    public void Without_privileges_the_login_screen_is_unavailable_but_the_user_desktop_starts_the_agent_as_the_current_user()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        Assert.SkipWhen(SessionProcessLauncher.IsLocalSystem, "Only meaningful for an unprivileged test run.");
        var session = SessionProcessLauncher.GetActiveSessionId();
        Assert.SkipWhen(session != (uint)Process.GetCurrentProcess().SessionId, "The console belongs to another session.");

        Assert.Throws<AgentUnavailableException>(() => SessionProcessLauncher.Prepare(DesktopKind.Secure, session));

        using var prepared = SessionProcessLauncher.Prepare(DesktopKind.User, session);
        Assert.Equal(WindowsIdentity.GetCurrent().User, prepared.User);
        using var process = SessionProcessLauncher.Start(prepared, Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c exit 7");
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(10)));
        Assert.Equal(7, process.ExitCode);
    }

    [Fact]
    public void The_current_input_desktop_can_be_determined()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");

        // Depends on whether the test runs on an unlocked desktop; either answer proves the lookup works.
        Assert.True(Enum.IsDefined(InputDesktop.GetCurrent()));
    }
}
