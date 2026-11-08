using RemoteDesktop.Application.Links;
using RemoteDesktop.Client;

namespace RemoteDesktop.WindowsUi.IntegrationTests;

/// <summary>
/// Integration tests for desktop client application lifecycle, single-instance link activation handover,
/// client environment configuration, and crash logging.
/// </summary>
public sealed class ClientAppLifecycleTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _tempDataDir = Path.Combine(Path.GetTempPath(), "rd-lifecycle-" + Guid.NewGuid().ToString("N"));

    private static string FreshPipeName() => "rd.link.test." + Guid.NewGuid().ToString("N")[..12];

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDataDir))
            {
                Directory.Delete(_tempDataDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public async Task Link_forwarding_hands_off_link_to_primary_listener()
    {
        var pipe = FreshPipeName();
        var received = new TaskCompletionSource<ConnectionLink>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var listener = LinkActivation.TryListen(link => received.TrySetResult(link), pipe);
        Assert.NotNull(listener);

        Assert.True(ConnectionLink.TryParse("remotedesktop://connect?code=902226105", out var link));
        Assert.True(LinkActivation.TryForward(link, pipe));

        var forwarded = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("902226105", forwarded.Device.ToString());
    }

    [Fact]
    public void Only_one_client_can_own_the_link_pipe()
    {
        var pipe = FreshPipeName();

        using var first = LinkActivation.TryListen(_ => { }, pipe);
        Assert.NotNull(first);

        using var second = LinkActivation.TryListen(_ => { }, pipe);
        Assert.Null(second);
    }

    [Fact]
    public void Client_environment_reflects_standalone_mode()
    {
        string[] args = ["--DataDirectory", _tempDataDir, "--HostMode", "standalone"];
        using var host = ClientHost.Build(args);

        var environment = (ClientEnvironment)host.Services.GetService(typeof(ClientEnvironment))!;
        Assert.NotNull(environment);
        Assert.True(environment.ControlledEndInThisProcess);
        Assert.Equal(_tempDataDir, environment.DataDirectory);
    }

    [Fact]
    public void Crash_log_records_unhandled_fatal_exceptions()
    {
        Directory.CreateDirectory(_tempDataDir);
        var crashLogPath = Path.Combine(_tempDataDir, "crash.log");

        var testException = new InvalidOperationException("E2E simulated client crash failure");
        CrashLog.Write(crashLogPath, "test-source", testException);

        Assert.True(File.Exists(crashLogPath));
        var logContent = File.ReadAllText(crashLogPath);
        Assert.Contains("E2E simulated client crash failure", logContent);
        Assert.Contains("test-source", logContent);
    }
}
