namespace RemoteDesktop.Server.Ops.Core;

/// <summary>
/// A dynamic notification channel stored in the database.
/// </summary>
public sealed record NotificationChannel(
    string Id,
    string Kind,
    string Target,
    string? Secret,
    bool Enabled = true);
