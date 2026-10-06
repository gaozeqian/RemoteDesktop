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

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, byte[]> _data = [];

        public byte[]? Load(string name) => _data.GetValueOrDefault(name);

        public void Save(string name, ReadOnlySpan<byte> secret) => _data[name] = secret.ToArray();

        public void Delete(string name) => _data.Remove(name);
    }
}
