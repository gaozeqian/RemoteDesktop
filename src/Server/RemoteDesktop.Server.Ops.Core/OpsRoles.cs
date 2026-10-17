namespace RemoteDesktop.Server.Ops.Core;

/// <summary>
/// The three roles the console knows. Deliberately not a bit flag set: a deployment that needs a fourth role adds a
/// row's worth of code, and pretending the set is open would only hide where the checks are.
/// </summary>
public static class OpsRoles
{
    /// <summary>Reads every screen. Cannot change anything.</summary>
    public const string Viewer = "viewer";

    /// <summary>Also acknowledges alerts — the one action a shift needs at three in the morning.</summary>
    public const string Operator = "operator";

    /// <summary>Also edits rules, manages accounts and reads the audit log.</summary>
    public const string Admin = "admin";

    /// <summary>True for one of the three names, matched without regard to case.</summary>
    public static bool IsValid(string? role) =>
        string.Equals(role, Viewer, StringComparison.OrdinalIgnoreCase)
        || string.Equals(role, Operator, StringComparison.OrdinalIgnoreCase)
        || string.Equals(role, Admin, StringComparison.OrdinalIgnoreCase);

    /// <summary>The stored spelling of <paramref name="role"/>, or <c>null</c> when it is not a role.</summary>
    public static string? Canonical(string? role)
    {
        if (string.Equals(role, Viewer, StringComparison.OrdinalIgnoreCase))
        {
            return Viewer;
        }

        if (string.Equals(role, Operator, StringComparison.OrdinalIgnoreCase))
        {
            return Operator;
        }

        return string.Equals(role, Admin, StringComparison.OrdinalIgnoreCase) ? Admin : null;
    }
}
