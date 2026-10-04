namespace RemoteDesktop.Abstractions.Platform;

/// <summary>
/// Decides whether the current process may hand out things that must only be reached with the rights of the logged-in
/// user: files and shells. A process running as the system (the agent on the login screen) would give the whole
/// machine to anyone who was allowed to connect.
/// </summary>
public interface IUserContextGuard
{
    /// <returns><c>null</c> when the user's files and shells may be served, otherwise why not (shown to the controller).</returns>
    string? GetRefusalReason();
}
