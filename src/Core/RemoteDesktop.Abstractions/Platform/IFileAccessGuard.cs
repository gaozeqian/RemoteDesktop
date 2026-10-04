namespace RemoteDesktop.Abstractions.Platform;

/// <summary>
/// Decides whether the current process may serve its files to a remote controller. Files must only ever be reached with
/// the rights of the logged-in user: a process running as the system (the agent on the login screen) would hand out
/// the whole disk to anyone who was allowed to connect.
/// </summary>
public interface IFileAccessGuard
{
    /// <returns><c>null</c> when files may be served, otherwise why not (shown to the controller).</returns>
    string? GetRefusalReason();
}
