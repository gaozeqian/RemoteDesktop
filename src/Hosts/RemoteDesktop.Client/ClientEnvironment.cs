namespace RemoteDesktop.Client;

/// <summary>
/// Where this client keeps its state. Shown read-only on the 高级设置 page: pointing a client somewhere else gives it
/// a different device identity, so it is not something to edit from inside the running program.
/// </summary>
/// <param name="ControlledEndInThisProcess">
/// True when this window is also the controlled end (standalone/portable, or macOS). The settings page's sentences
/// depend on it: they must not describe a background service on a machine that has none.
/// </param>
public sealed record ClientEnvironment(string DataDirectory, bool ControlledEndInThisProcess);
