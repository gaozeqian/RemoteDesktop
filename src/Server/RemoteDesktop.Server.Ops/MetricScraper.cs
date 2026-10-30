using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops;

/// <summary>
/// Pulls <c>/metrics</c> from every enabled node and stores what it finds.
/// </summary>
/// <remarks>
/// Pull rather than push: a failed scrape is itself a signal ("this node is not answering"), and it needs no queue on
/// the node side that could grow while the console is down. The interval is fixed per deployment; a node cannot ask to
/// be scraped faster.
/// </remarks>
public sealed partial class MetricScraper(
    IHttpClientFactory httpClientFactory,
    OpsStore store,
    OpsOptions options,
    OpsLease lease,
    TimeProvider time,
    ILogger<MetricScraper> logger) : BackgroundService
{
    /// <summary>The named client, so its timeout is configured once.</summary>
    public const string ClientName = "ops-scraper";

    private const string LeaseName = "scrape";

    /// <summary>
    /// Scrapes every enabled node once and returns how many answered. Public so a test can drive one pass without
    /// waiting for the timer.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!await lease.TryAcquireAsync(LeaseName, TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        var nodes = await store.ListNodesAsync(cancellationToken).ConfigureAwait(false);
        var enabled = nodes.Where(node => node.Enabled).ToArray();
        if (enabled.Length == 0)
        {
            return 0;
        }

        var results = await Task.WhenAll(enabled.Select(node => ScrapeAsync(node, cancellationToken))).ConfigureAwait(false);
        return results.Count(ok => ok);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.ScrapeIntervalSeconds)), time);

        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The scraper is the console's only writer of node health. If one pass faults, the next one must still
                // run; the alternative is the default host behaviour, which stops the whole console.
                ScrapePassFailed(logger, ex);
            }
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<bool> ScrapeAsync(OpsNode node, CancellationToken cancellationToken)
    {
        var timestamp = time.GetUtcNow().ToUnixTimeMilliseconds();
        var token = NodeHttp.TokenFor(node, options);
        if (NodeHttp.RefusalReason(node.BaseUrl, token, options) is { } refusal)
        {
            await RecordFailureAsync(node, timestamp, refusal, cancellationToken).ConfigureAwait(false);
            return false;
        }

        try
        {
            using var client = httpClientFactory.CreateClient(ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, node.BaseUrl.TrimEnd('/') + "/metrics");
            NodeHttp.ApplyAuthorization(request, token);

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                await store.RecordScrapeAsync(node.Id, timestamp, ok: false, cancellationToken).ConfigureAwait(false);
                ScrapeFailed(logger, node.Name, $"HTTP {(int)response.StatusCode}");
                return false;
            }

            var text = await NodeHttp
                .ReadStringBoundedAsync(response.Content, NodeHttp.MaxResponseBytes, cancellationToken)
                .ConfigureAwait(false);
            var snapshot = PrometheusTextParser.Parse(text);

            // One catalog row per series, written before the samples so a query never sees a value whose type is not
            // known yet. A series with no TYPE line is recorded as unknown rather than guessed into a gauge.
            var catalog = new List<KeyValuePair<string, string>>(snapshot.Samples.Count);
            foreach (var sample in snapshot.Samples)
            {
                var kind = snapshot.Types.TryGetValue(sample.Name, out var known)
                    ? MetricKinds.ToPrometheusType(known)
                    : "unknown";
                catalog.Add(new KeyValuePair<string, string>(sample.Key, kind));
            }

            if (catalog.Count > 0)
            {
                await store.UpsertMetricCatalogAsync(catalog, cancellationToken).ConfigureAwait(false);
            }

            await store.WriteSamplesAsync(node.Id, timestamp, snapshot.Samples, cancellationToken).ConfigureAwait(false);
            await store.RecordScrapeAsync(node.Id, timestamp, ok: true, cancellationToken).ConfigureAwait(false);
            Scraped(logger, node.Name, snapshot.Samples.Count, snapshot.Skipped);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown, not a failed node.
            throw;
        }
        catch (TaskCanceledException)
        {
            await RecordFailureAsync(node, timestamp, "timed out", cancellationToken).ConfigureAwait(false);
            return false;
        }
        catch (HttpRequestException ex)
        {
            await RecordFailureAsync(node, timestamp, ex.Message, cancellationToken).ConfigureAwait(false);
            return false;
        }
        catch (InvalidOperationException ex)
        {
            // A malformed BaseUrl, which the API refuses on create; this is the path a hand-edited row takes.
            await RecordFailureAsync(node, timestamp, ex.Message, cancellationToken).ConfigureAwait(false);
            return false;
        }
        catch (Exception ex)
        {
            // A bad pin path, a response over the size limit, a database error while recording: whatever it was, this
            // node's scrape failed and the next node must still be tried.
            await RecordFailureAsync(node, timestamp, ex.Message, cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    private async Task RecordFailureAsync(OpsNode node, long timestamp, string reason, CancellationToken cancellationToken)
    {
        // A failed scrape still updates last_scrape_at: the node card must say "last tried a moment ago, failed", not
        // keep showing the last success as if it were current. Recording that status is itself a database write, so a
        // database that is down cannot be allowed to throw out of this handler.
        try
        {
            await store.RecordScrapeAsync(node.Id, timestamp, ok: false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ScrapeStatusFailed(logger, node.Name, ex);
        }

        ScrapeFailed(logger, node.Name, reason);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Scraped {Node}: {Samples} samples, {Skipped} lines skipped.")]
    private static partial void Scraped(ILogger logger, string node, int samples, int skipped);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scrape of {Node} failed: {Reason}.")]
    private static partial void ScrapeFailed(ILogger logger, string node, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record the failed scrape of {Node}; the node card may show a stale status.")]
    private static partial void ScrapeStatusFailed(ILogger logger, string node, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A scrape pass failed; will try again next interval.")]
    private static partial void ScrapePassFailed(ILogger logger, Exception exception);
}
