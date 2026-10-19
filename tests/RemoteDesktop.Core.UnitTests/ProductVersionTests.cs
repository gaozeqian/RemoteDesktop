using ClientVersion = RemoteDesktop.Application.ProductVersion;
using ServerVersion = RemoteDesktop.Server.Shared.ProductVersion;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// One number, two shapes: what people and package names see, and what diagnostics carry.
/// </summary>
public class ProductVersionTests
{
    [Fact]
    public void Display_is_three_parts_and_Full_starts_with_it()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+$", ClientVersion.Display);
        Assert.StartsWith(ClientVersion.Display, ClientVersion.Full, StringComparison.Ordinal);
        Assert.DoesNotContain('+', ClientVersion.Display);
    }

    [Fact]
    public void The_client_and_the_server_report_the_same_version()
    {
        // The copies exist only because the two sides may not depend on each other's layer. They read the same
        // Directory.Build.props, and this is what keeps them from becoming two answers.
        Assert.Equal(ClientVersion.Display, ServerVersion.Display);
        Assert.Equal(ClientVersion.Full, ServerVersion.Full);
    }
}
