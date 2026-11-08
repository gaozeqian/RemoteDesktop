using RemoteDesktop.Application.Links;
using RemoteDesktop.Client;

namespace RemoteDesktop.WindowsUi.IntegrationTests;

public class LinkActivationIntegrationTests
{
    [Fact]
    public async Task LinkActivation_forwarding_delivers_device_code_to_active_listener()
    {
        var pipeName = $"rd-link-test-{Guid.NewGuid():N}";
        var received = new TaskCompletionSource<ConnectionLink>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var listener = LinkActivation.TryListen(link => received.TrySetResult(link), pipeName);
        Assert.NotNull(listener);

        Assert.True(ConnectionLink.TryParse("remotedesktop://connect?code=987654321", out var link));
        var forwarded = LinkActivation.TryForward(link, pipeName);

        Assert.True(forwarded);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await received.Task.WaitAsync(cts.Token);

        Assert.Equal(987654321, result.Device.Value);
    }

    [Fact]
    public void LinkActivation_forwarding_returns_false_when_no_listener_exists()
    {
        var pipeName = $"rd-link-missing-{Guid.NewGuid():N}";
        Assert.True(ConnectionLink.TryParse("remotedesktop://123456789", out var link));

        var forwarded = LinkActivation.TryForward(link, pipeName);
        Assert.False(forwarded);
    }
}
