using RemoteDesktop.Server.Relay;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// The relay had no start-up validation, so a zero idle timeout cleared every pairing seconds after it was made while
/// the process still reported healthy. These are the settings the relay now refuses up front.
/// </summary>
public class RelayOptionsTests
{
    [Fact]
    public void The_defaults_are_usable()
    {
        new RelayOptions().Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void A_port_outside_the_valid_range_is_refused(int port)
    {
        var options = new RelayOptions { Port = port };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Relay:Port", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zero_idle_timeout_is_refused()
    {
        var options = new RelayOptions { IdleTimeout = TimeSpan.Zero };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Relay:IdleTimeout", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_relative_http_endpoint_is_refused()
    {
        var options = new RelayOptions { HttpEndpoint = "127.0.0.1:5083" };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Relay:HttpEndpoint", error.Message, StringComparison.Ordinal);
    }
}
