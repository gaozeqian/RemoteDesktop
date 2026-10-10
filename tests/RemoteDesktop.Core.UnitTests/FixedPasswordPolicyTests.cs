using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Application.Sessions;

namespace RemoteDesktop.Core.UnitTests;

public class FixedPasswordPolicyTests
{
    [Fact]
    public void Load_returns_null_when_store_is_empty()
    {
        var store = new MemorySecrets();
        Assert.Null(FixedPasswordPolicy.Load(store));
    }

    [Fact]
    public void Save_persists_fixed_password_and_load_reads_it_back()
    {
        var store = new MemorySecrets();
        FixedPasswordPolicy.Save(store, "secret-unattended-123");

        Assert.Equal("secret-unattended-123", FixedPasswordPolicy.Load(store));
    }

    [Fact]
    public void Save_null_or_empty_clears_the_fixed_password()
    {
        var store = new MemorySecrets();
        FixedPasswordPolicy.Save(store, "secret-unattended-123");
        Assert.NotNull(FixedPasswordPolicy.Load(store));

        FixedPasswordPolicy.Save(store, null);
        Assert.Null(FixedPasswordPolicy.Load(store));

        FixedPasswordPolicy.Save(store, "new-secret");
        Assert.NotNull(FixedPasswordPolicy.Load(store));

        FixedPasswordPolicy.Save(store, "   ");
        Assert.Null(FixedPasswordPolicy.Load(store));
    }

    /// <summary>
    /// The rule is the service's, not the window's: the fixed password is what makes this machine reachable with
    /// nobody in front of it, and the setting arrives over a pipe that any process on this machine can open.
    /// </summary>
    /// <remarks>An empty value is not too short, it is "clear the setting"; the test above covers that.</remarks>
    [Theory]
    [InlineData("a")]
    [InlineData("12345")]
    public void A_fixed_password_below_the_minimum_is_refused(string password)
    {
        var store = new MemorySecrets();

        Assert.False(FixedPasswordPolicy.IsAcceptable(password));
        Assert.Throws<ArgumentException>(() => FixedPasswordPolicy.Save(store, password));
        Assert.Null(FixedPasswordPolicy.Load(store));
    }

    [Fact]
    public void The_shortest_password_that_is_allowed_is_the_documented_one()
    {
        var store = new MemorySecrets();
        var shortest = new string('a', FixedPasswordPolicy.MinimumLength);

        FixedPasswordPolicy.Save(store, shortest);

        Assert.Equal(shortest, FixedPasswordPolicy.Load(store));
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, byte[]> _data = [];

        public byte[]? Load(string name) => _data.GetValueOrDefault(name);

        public void Save(string name, ReadOnlySpan<byte> secret) => _data[name] = secret.ToArray();

        public void Delete(string name) => _data.Remove(name);
    }
}
