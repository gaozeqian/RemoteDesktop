namespace RemoteDesktop.Client;

/// <summary>
/// Where this client keeps its state. Shown read-only on the 高级设置 page: pointing a client somewhere else gives it
/// a different device identity, so it is not something to edit from inside the running program.
/// </summary>
public sealed record ClientEnvironment(string DataDirectory);
