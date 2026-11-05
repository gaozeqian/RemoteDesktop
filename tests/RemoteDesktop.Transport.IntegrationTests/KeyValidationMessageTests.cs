using RemoteDesktop.Server.Rendezvous;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// Pins the exact refusal each place that checks a base64 key gives. The places word their messages for their own
/// setting (and for whoever reads them: an installer log, a systemd journal), so a shared implementation underneath
/// must not blur them into one text.
/// </summary>
public sealed class KeyValidationMessageTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("rd-keymessages-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static string Base64Of(int bytes) => Convert.ToBase64String(new byte[bytes]);

    private static RendezvousOptions WithRelayKey(string? key) =>
        new() { RelayEndpoint = "203.0.113.10:5082", RelaySharedKey = key! };

    [Fact]
    public void Rendezvous_accepts_a_key_of_exactly_32_bytes() =>
        WithRelayKey(Base64Of(32)).Validate();

    [Fact]
    public void Rendezvous_does_not_look_at_the_key_without_a_relay_endpoint() =>
        new RendezvousOptions { RelayEndpoint = "", RelaySharedKey = "not base64 at all" }.Validate();

    [Fact]
    public void Rendezvous_refuses_a_key_that_is_not_base64()
    {
        var error = Assert.Throws<InvalidOperationException>(() => WithRelayKey("not base64 at all").Validate());

        Assert.Equal("Rendezvous:RelaySharedKey must be base64 (generate one with: openssl rand -base64 48).", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void Rendezvous_refuses_a_key_of_31_bytes()
    {
        var error = Assert.Throws<InvalidOperationException>(() => WithRelayKey(Base64Of(31)).Validate());

        Assert.Equal(
            "Rendezvous:RelaySharedKey must be at least 32 bytes when Rendezvous:RelayEndpoint is set (it is only 31 bytes). Generate one with: openssl rand -base64 48",
            error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rendezvous_calls_a_missing_key_empty(string? key)
    {
        var error = Assert.Throws<InvalidOperationException>(() => WithRelayKey(key).Validate());

        Assert.Equal(
            "Rendezvous:RelaySharedKey must be at least 32 bytes when Rendezvous:RelayEndpoint is set (it is empty). Generate one with: openssl rand -base64 48",
            error.Message);
    }

    [Fact]
    public void Setup_accepts_a_relay_key_of_exactly_32_bytes() =>
        ServerSetup.Configure(_folder, new ServerSetupOptions { RelaySharedKey = Base64Of(32) });

    [Fact]
    public void Setup_refuses_a_relay_key_that_is_not_base64()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ServerSetup.Configure(_folder, new ServerSetupOptions { RelaySharedKey = "not base64 at all" }));

        Assert.Equal("The relay shared key must be base64 (generate one with: openssl rand -base64 48).", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void Setup_refuses_a_relay_key_of_31_bytes()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ServerSetup.Configure(_folder, new ServerSetupOptions { RelaySharedKey = Base64Of(31) }));

        Assert.Equal("The relay shared key must be at least 32 bytes (31 given). Generate one with: openssl rand -base64 48", error.Message);
    }

    [Fact]
    public void Setup_accepts_an_ops_ingest_key_of_exactly_32_bytes() =>
        ServerSetup.Configure(_folder, new ServerSetupOptions { OpsIngestKey = Base64Of(32) });

    [Fact]
    public void Setup_refuses_an_ops_ingest_key_that_is_not_base64()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ServerSetup.Configure(_folder, new ServerSetupOptions { OpsIngestKey = "not base64 at all" }));

        Assert.Equal("The Ops ingest key must be base64 (generate one with: openssl rand -base64 32).", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void Setup_refuses_an_ops_ingest_key_of_31_bytes()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ServerSetup.Configure(_folder, new ServerSetupOptions { OpsIngestKey = Base64Of(31) }));

        Assert.Equal("The Ops ingest key must be at least 32 bytes (31 given). Generate one with: openssl rand -base64 32.", error.Message);
    }
}
