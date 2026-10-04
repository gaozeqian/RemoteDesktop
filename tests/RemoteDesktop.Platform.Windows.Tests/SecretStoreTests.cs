using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace RemoteDesktop.Platform.Windows.Tests;

public class SecretStoreTests
{
    [Fact]
    public void Machine_scope_secrets_live_in_a_folder_only_system_administrators_and_the_owner_can_enter()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI and ACLs are Windows only.");
        var directory = Path.Combine(Path.GetTempPath(), $"rd-secrets-{Guid.NewGuid():N}");
        try
        {
            var store = new DpapiSecretStore(directory, DataProtectionScope.LocalMachine);
            store.Save("device-identity", [1, 2, 3]);

            Assert.Equal(new byte[] { 1, 2, 3 }, store.Load("device-identity")); // the owner still has access
            var rules = new DirectoryInfo(directory).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().ToList();
            var allowed = rules.Where(r => r.AccessControlType == AccessControlType.Allow).Select(r => (SecurityIdentifier)r.IdentityReference).ToList();

            Assert.Contains(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), allowed);
            Assert.Contains(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), allowed);
            Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), allowed);
            Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), allowed);
            Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.WorldSid, null), allowed);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
