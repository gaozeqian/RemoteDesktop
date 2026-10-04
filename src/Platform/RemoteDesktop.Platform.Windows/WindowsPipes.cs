using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RemoteDesktop.Platform.Windows;

/// <summary>Named pipe servers with explicit access control; the default ACL would let any local user connect.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsPipes
{
    public static SecurityIdentifier AuthenticatedUsers { get; } = new(WellKnownSidType.AuthenticatedUserSid, null);

    private static SecurityIdentifier LocalSystem { get; } = new(WellKnownSidType.LocalSystemSid, null);

    private static SecurityIdentifier Administrators { get; } = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private static SecurityIdentifier Network { get; } = new(WellKnownSidType.NetworkSid, null);

    /// <summary>
    /// Creates one instance of a pipe that SYSTEM, administrators and the account running this process control, and
    /// that <paramref name="clients"/> may read and write. Network access is denied outright.
    /// </summary>
    public static NamedPipeServerStream CreateServer(string name, IEnumerable<SecurityIdentifier> clients)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(clients);

        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(Network, PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(LocalSystem, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(Administrators, PipeAccessRights.FullControl, AccessControlType.Allow));

        // The creator needs the right to create further instances of the pipe, or the second client cannot connect.
        if (WindowsIdentity.GetCurrent().User is { } self)
        {
            security.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        foreach (var client in clients)
        {
            security.AddAccessRule(new PipeAccessRule(client, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        }

        return NamedPipeServerStreamAcl.Create(
            name,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024,
            security);
    }
}
