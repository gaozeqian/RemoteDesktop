namespace RemoteDesktop.Abstractions.Platform;

/// <summary>Persists secrets (fixed password, device private key) using OS protection (DPAPI, Keychain, libsecret).</summary>
public interface ISecretStore
{
    void Save(string name, ReadOnlySpan<byte> secret);

    byte[]? Load(string name);

    void Delete(string name);
}
