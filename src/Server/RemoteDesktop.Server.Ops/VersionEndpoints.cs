using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Ops;

/// <summary>
/// The version this console is running, for the sidebar.
/// </summary>
/// <remarks>
/// The shell is a static file, so a version written into it is a number that goes stale the first time nobody remembers
/// to edit it. This endpoint reads the assembly, which is the same source every other surface uses.
/// </remarks>
public static class VersionEndpoints
{
    /// <summary>Maps <c>/api/version</c>.</summary>
    public static void MapOpsVersion(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Anonymous on purpose: the sidebar is visible on the sign-in screen, and a version string is not the kind of
        // inventory answer that /health is kept on loopback for.
        endpoints.MapGet("/api/version", () => Results.Ok(new { version = ProductVersion.Display }))
            .AllowAnonymous();
    }
}
