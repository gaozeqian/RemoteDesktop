using RemoteDesktop.Application.Links;
using RemoteDesktop.Client;

namespace RemoteDesktop.WindowsUi.IntegrationTests;

/// <summary>
/// What link activation does when it cannot hand the link over. The delivered case is
/// <see cref="ClientAppLifecycleTests.Link_forwarding_hands_off_link_to_primary_listener"/>, which is where it lives
/// with the single-owner rule it depends on; a second copy here asserted the same delivery and nothing more.
/// </summary>
public sealed class LinkActivationIntegrationTests
{
    [Fact]
    public void LinkActivation_forwarding_returns_false_when_no_listener_exists()
    {
        var pipeName = $"rd-link-missing-{Guid.NewGuid():N}";
        Assert.True(ConnectionLink.TryParse("remotedesktop://123456789", out var link));

        var forwarded = LinkActivation.TryForward(link, pipeName);
        Assert.False(forwarded);
    }
}
