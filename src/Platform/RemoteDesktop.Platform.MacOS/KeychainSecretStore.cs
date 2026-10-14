using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Platform.MacOS.Interop;

namespace RemoteDesktop.Platform.MacOS;

/// <summary>
/// Stores secrets in the login keychain as generic-password items.
/// </summary>
/// <remarks>
/// <para>
/// This replaces Windows' DPAPI. There is no direct equivalent: DPAPI encrypts bytes to the machine or the user, while
/// the keychain is a database the system owns and protects. The mapping is therefore by item, not by file — the name
/// becomes the account and the value becomes the item's data.
/// </para>
/// <para>
/// The data directory goes into the service name on purpose. A second instance started with
/// <c>--DataDirectory</c> is meant to be a different device with its own identity (see the README), and on Windows
/// that separation came from the two directories. Without it here, both instances would read the same keychain item
/// and quietly become the same device.
/// </para>
/// <para>
/// Accessibility is <c>AfterFirstUnlock</c> rather than <c>WhenUnlocked</c>: an unattended host must be able to come
/// back after a reboot and reconnect before anybody has logged in and unlocked the keychain.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed unsafe class KeychainSecretStore(string dataDirectory) : ISecretStore
{
    private readonly string _service = $"RemoteDesktop:{dataDirectory}";

    public void Save(string name, ReadOnlySpan<byte> secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        // Update first: SecItemAdd on an existing item fails with errSecDuplicateItem, and deleting-then-adding would
        // leave a window where the device identity does not exist at all.
        using (var query = Query(name))
        using (var attributes = new CfDictionary().AddData(Security.SecValueData, secret))
        {
            var status = Security.SecItemUpdate(query.Build(), attributes.Build());
            if (status == Security.ErrSecSuccess)
            {
                return;
            }

            if (status != Security.ErrSecItemNotFound)
            {
                throw new InvalidOperationException($"Could not update the keychain item '{name}' (OSStatus {status}).");
            }
        }

        using var insert = Query(name)
            .AddData(Security.SecValueData, secret)
            .AddRaw(Security.SecAttrAccessible, Security.SecAttrAccessibleAfterFirstUnlock);
        var added = Security.SecItemAdd(insert.Build(), null);
        if (added != Security.ErrSecSuccess)
        {
            throw new InvalidOperationException($"Could not store the keychain item '{name}' (OSStatus {added}).");
        }
    }

    public byte[]? Load(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        using var query = Query(name)
            .AddBoolean(Security.SecReturnData, true)
            .AddRaw(Security.SecMatchLimit, Security.SecMatchLimitOne);

        nint data;
        var status = Security.SecItemCopyMatching(query.Build(), &data);
        if (status == Security.ErrSecItemNotFound)
        {
            return null;
        }

        if (status != Security.ErrSecSuccess)
        {
            throw new InvalidOperationException($"Could not read the keychain item '{name}' (OSStatus {status}).");
        }

        try
        {
            var length = (int)CoreFoundation.CFDataGetLength(data);
            var result = new byte[length];
            unsafe
            {
                var source = CoreFoundation.CFDataGetBytePtr(data);
                new ReadOnlySpan<byte>(source, length).CopyTo(result);
            }

            return result;
        }
        finally
        {
            CoreFoundation.CFRelease(data);
        }
    }

    public void Delete(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        using var query = Query(name);
        var status = Security.SecItemDelete(query.Build());
        if (status is not (Security.ErrSecSuccess or Security.ErrSecItemNotFound))
        {
            throw new InvalidOperationException($"Could not delete the keychain item '{name}' (OSStatus {status}).");
        }
    }

    /// <summary>The class / service / account triple that identifies one item.</summary>
    private CfDictionary Query(string name) =>
        new CfDictionary()
            .AddRaw(Security.SecClass, Security.SecClassGenericPassword)
            .AddString(Security.SecAttrService, _service)
            .AddString(Security.SecAttrAccount, name);
}
