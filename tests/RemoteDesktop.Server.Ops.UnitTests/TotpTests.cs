using System.Text;
using RemoteDesktop.Server.Ops.Core;

namespace RemoteDesktop.Server.Ops.UnitTests;

/// <summary>
/// TOTP against RFC 6238's SHA-1 vectors. The RFC prints eight digits; this implementation shows six, which are the
/// last six of the same truncation — testing the real values is the only thing that proves the truncation is right.
/// </summary>
public class TotpTests
{
    /// <summary>The RFC's ASCII secret, base32-encoded.</summary>
    private const string RfcSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    public static TheoryData<long, string> Vectors => new()
    {
        { 59L, "287082" },
        { 1_111_111_109L, "081804" },
        { 1_111_111_111L, "050471" },
        { 1_234_567_890L, "005924" },
        { 2_000_000_000L, "279037" },
        { 20_000_000_000L, "353130" },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Codes_match_the_rfc_vectors(long unixSeconds, string expected)
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

        Assert.Equal(expected, Totp.Compute(RfcSecret, at));
    }

    [Fact]
    public void A_code_from_the_previous_step_is_accepted_but_the_one_two_steps_away_is_not()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(1_234_567_890L);
        var previous = Totp.Compute(RfcSecret, at.AddSeconds(-30));
        var older = Totp.Compute(RfcSecret, at.AddSeconds(-90));

        Assert.True(Totp.TryVerify(RfcSecret, previous, at, out _));
        Assert.False(Totp.TryVerify(RfcSecret, older, at, out _));
    }

    [Fact]
    public void Verification_reports_the_step_that_matched()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(1_234_567_890L);
        var code = Totp.Compute(RfcSecret, at);

        Assert.True(Totp.TryVerify(RfcSecret, code, at, out var step));
        Assert.Equal(at.ToUnixTimeSeconds() / Totp.StepSeconds, step);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    [InlineData("")]
    public void Malformed_codes_are_refused(string code)
    {
        Assert.False(Totp.TryVerify(RfcSecret, code, DateTimeOffset.UnixEpoch, out _));
    }

    [Fact]
    public void A_generated_secret_is_base32_and_round_trips()
    {
        var secret = Totp.GenerateSecret();

        Assert.Equal(32, secret.Length);
        Assert.Equal(20, Base32.Decode(secret).Length);
        Assert.All(secret, character => Assert.Contains(character, "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"));
    }

    [Fact]
    public void Base32_decodes_the_rfc_secret_to_its_ascii_text()
    {
        Assert.Equal("12345678901234567890", Encoding.ASCII.GetString(Base32.Decode(RfcSecret)));
    }

    [Fact]
    public void Base32_tolerates_padding_and_lower_case()
    {
        Assert.Equal("hello", Encoding.ASCII.GetString(Base32.Decode("NBSWY3DP===")));
        Assert.Equal("hello", Encoding.ASCII.GetString(Base32.Decode("nbswy3dp")));
    }

    [Fact]
    public void An_enrolment_uri_names_the_profile_the_app_must_use()
    {
        var uri = Totp.BuildUri("RemoteDesktop", "ops-admin", RfcSecret);

        Assert.StartsWith("otpauth://totp/RemoteDesktop:ops-admin?", uri, StringComparison.Ordinal);
        Assert.Contains("secret=" + RfcSecret, uri, StringComparison.Ordinal);
        Assert.Contains("algorithm=SHA1", uri, StringComparison.Ordinal);
        Assert.Contains("digits=6", uri, StringComparison.Ordinal);
        Assert.Contains("period=30", uri, StringComparison.Ordinal);
    }
}
