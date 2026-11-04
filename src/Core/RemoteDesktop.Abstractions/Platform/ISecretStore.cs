namespace RemoteDesktop.Abstractions.Platform;

/// <summary>Persists secrets (fixed password, device private key) using OS protection (DPAPI on Windows, Keychain on macOS, an owner-only 0600 file on Linux).</summary>
public interface ISecretStore
{
    void Save(string name, ReadOnlySpan<byte> secret);

    byte[]? Load(string name);

    void Delete(string name);
}
