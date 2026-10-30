using RemoteDesktop.Application.Hosting;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The settings a host reads before it builds anything. A mistyped address used to kill the background service before
/// it had a log: the service was simply not running, and the only thing the interface could say was that it could not
/// reach the service. What is checked here is that the failure carries a sentence, and that the sentence lands where
/// somebody was told to look.
/// </summary>
public sealed class StartupSettingsTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("rd-startup-").FullName;

    private string ReportPath => Path.Combine(_folder, "logs", "startup-error.log");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Settings_that_can_be_used_are_returned_and_nothing_is_reported()
    {
        var settings = StartupSettings.TryReadTransport(
            () => new TransportOptions { RendezvousUri = new Uri("wss://rd.example.com:5080/ws") },
            ReportPath,
            out var reason);

        Assert.NotNull(settings);
        Assert.Equal("wss://rd.example.com:5080/ws", settings.RendezvousUri.ToString());
        Assert.Empty(reason);
        Assert.False(File.Exists(ReportPath));
    }

    /// <summary>
    /// The shape of the real failure: binding a value that is not an address at all throws before anything exists to
    /// catch it. The delegate stands in for that, because the binding itself belongs to the hosts.
    /// </summary>
    [Fact]
    public void Settings_that_cannot_be_bound_are_reported_rather_than_thrown()
    {
        var settings = StartupSettings.TryReadTransport(
            () => throw new InvalidOperationException("Failed to convert configuration value 'Transport:RendezvousUri' to type 'System.Uri'."),
            ReportPath,
            out var reason);

        Assert.Null(settings);
        Assert.Contains("RendezvousUri", reason, StringComparison.Ordinal);

        var written = File.ReadAllText(ReportPath);
        Assert.Contains(reason, written, StringComparison.Ordinal);
        Assert.Contains("startup:", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_that_do_not_pass_their_own_checks_are_reported_the_same_way()
    {
        var settings = StartupSettings.TryReadTransport(
            () => new TransportOptions { UdpRequestTimeout = TimeSpan.Zero },
            ReportPath,
            out var reason);

        Assert.Null(settings);
        Assert.Contains("UdpRequestTimeout", reason, StringComparison.Ordinal);
        Assert.Contains("UdpRequestTimeout", File.ReadAllText(ReportPath), StringComparison.Ordinal);
    }

    [Fact]
    public void A_report_that_cannot_be_written_is_not_worth_failing_over()
    {
        var blocked = Path.Combine(_folder, "not-a-directory");
        File.WriteAllText(blocked, "x");

        var settings = StartupSettings.TryReadTransport(
            () => throw new InvalidOperationException("bad"),
            Path.Combine(blocked, "startup-error.log"),
            out var reason);

        Assert.Null(settings);
        Assert.NotEmpty(reason); // the sentence still reaches whoever asked; the file was the only casualty
    }
}

public class TransportOptionsTests
{
    [Fact]
    public void The_defaults_are_usable() => new TransportOptions().Validate();

    [Theory]
    [InlineData("http://rd.example.com:5080/ws")]
    [InlineData("rd.example.com:5080")]
    public void An_address_that_is_not_a_websocket_address_is_refused(string address)
    {
        var options = new TransportOptions { RendezvousUri = new Uri(address) };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void A_relative_address_is_refused()
    {
        var options = new TransportOptions { RendezvousUri = new Uri("/ws", UriKind.Relative) };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    /// <summary>
    /// Typing "ws://host:5080" without the path is common, and the server only maps /ws. The parser fills it in so
    /// the connection does not go to "/" and fail as if the server were unreachable.
    /// </summary>
    [Theory]
    [InlineData("ws://rd.example.com:5080", "ws://rd.example.com:5080/ws")]
    [InlineData("wss://rd.example.com/", "wss://rd.example.com/ws")]
    public void An_address_without_the_signaling_path_gets_it(string address, string expected)
    {
        var options = new TransportOptions { RendezvousUri = new Uri(address), AllowInsecureSignaling = true };

        options.Validate();

        Assert.Equal(expected, options.RendezvousUri.AbsoluteUri);
    }

    [Fact]
    public void Remote_plaintext_signaling_needs_an_explicit_opt_in()
    {
        var options = new TransportOptions { RendezvousUri = new Uri("ws://rd.example.com:5080/ws") };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("wss://", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loopback_plaintext_signaling_remains_the_safe_local_default()
    {
        new TransportOptions { RendezvousUri = new Uri("ws://127.0.0.1:5080/ws") }.Validate();
    }

    [Fact]
    public void A_reverse_proxy_path_is_kept()
    {
        var options = new TransportOptions { RendezvousUri = new Uri("wss://rd.example.com/rd/ws") };

        options.Validate();

        Assert.Equal("wss://rd.example.com/rd/ws", options.RendezvousUri.AbsoluteUri);
    }

    /// <summary>
    /// A timeout of zero or less does not fail where it is set; it fails later, somewhere with no idea what caused it.
    /// </summary>
    [Fact]
    public void A_timeout_that_is_not_a_duration_is_refused()
    {
        var options = new TransportOptions { HostAcceptTimeout = TimeSpan.FromSeconds(-1) };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("HostAcceptTimeout", error.Message, StringComparison.Ordinal);
    }
}
