using RemoteDesktop.Server.Ops.Core;

namespace RemoteDesktop.Server.Ops.UnitTests;

/// <summary>
/// The password format is a promise to every account already stored, so the tests care as much about what is refused
/// as about what verifies.
/// </summary>
public class PasswordHasherTests
{
    [Fact]
    public void A_hash_verifies_its_own_password_and_no_other()
    {
        var encoded = PasswordHasher.Hash("correct horse battery staple");

        Assert.True(PasswordHasher.Verify("correct horse battery staple", encoded));
        Assert.False(PasswordHasher.Verify("correct horse battery stapl", encoded));
        Assert.False(PasswordHasher.Verify(string.Empty, encoded));
    }

    [Fact]
    public void Two_hashes_of_the_same_password_differ()
    {
        // Different salts, or the hash is a lookup table with extra steps.
        Assert.NotEqual(PasswordHasher.Hash("same"), PasswordHasher.Hash("same"));
    }

    [Fact]
    public void The_encoded_form_carries_its_parameters()
    {
        var parts = PasswordHasher.Hash("x").Split('$');

        Assert.Equal(4, parts.Length);
        Assert.Equal("pbkdf2-sha256", parts[0]);
        Assert.InRange(int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), 100_000, 2_000_000);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain-text")]
    [InlineData("pbkdf2-sha256$not-a-number$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2-sha256$999999999$c2FsdA==$aGFzaA==")]
    [InlineData("bcrypt$600000$c2FsdA==$aGFzaA==")]
    public void Malformed_or_hostile_encodings_are_refused(string encoded)
    {
        Assert.False(PasswordHasher.Verify("whatever", encoded));
    }
}
