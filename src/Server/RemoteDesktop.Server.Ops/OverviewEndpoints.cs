using RemoteDesktop.Server.Ops.Auth;

namespace RemoteDesktop.Server.Ops;

/// <summary>The overview screen's one request. The page never assembles numbers from several calls.</summary>
public static class OverviewEndpoints
{
    /// <summary>Maps <c>/api/overview</c>.</summary>
    public static void MapOpsOverview(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/overview", async (OverviewService overview, CancellationToken cancellationToken) =>
            Results.Ok(await overview.BuildAsync(cancellationToken).ConfigureAwait(false)))
            .RequireAuthorization(OpsAuth.ViewerPolicy);
    }
}
