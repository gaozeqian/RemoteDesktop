using System.Text;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Ops.UnitTests;

/// <summary>
/// The ingest signature is the only thing between a captured request and the console's event table. What matters is
/// that neither half of it can be swapped independently.
/// </summary>
public class OpsEventSignerTests
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""[{"kind":"session.accepted"}]""");

    [Fact]
    public void A_signature_verifies_for_the_same_timestamp_and_body()
    {
        var signature = OpsEventSigner.Sign(Key, 1_700_000_000, Body);

        Assert.True(OpsEventSigner.Verify(Key, 1_700_000_000, Body, signature));
    }

    [Fact]
    public void A_different_body_does_not_verify()
    {
        var signature = OpsEventSigner.Sign(Key, 1_700_000_000, Body);
        var tampered = Encoding.UTF8.GetBytes("""[{"kind":"session.relay_closed"}]""");

        Assert.False(OpsEventSigner.Verify(Key, 1_700_000_000, tampered, signature));
    }

    [Fact]
    public void A_different_timestamp_does_not_verify()
    {
        var signature = OpsEventSigner.Sign(Key, 1_700_000_000, Body);

        Assert.False(OpsEventSigner.Verify(Key, 1_700_000_001, Body, signature));
    }

    [Fact]
    public void Another_key_does_not_verify()
    {
        var signature = OpsEventSigner.Sign(Key, 1_700_000_000, Body);
        var other = Encoding.UTF8.GetBytes("fedcba9876543210fedcba9876543210");

        Assert.False(OpsEventSigner.Verify(other, 1_700_000_000, Body, signature));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 at all")]
    public void A_malformed_signature_is_refused_rather_than_thrown(string signature)
    {
        Assert.False(OpsEventSigner.Verify(Key, 1_700_000_000, Body, signature));
    }
}
