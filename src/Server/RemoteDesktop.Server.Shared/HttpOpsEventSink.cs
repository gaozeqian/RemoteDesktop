using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RemoteDesktop.Server.Shared;

/// <summary>
/// Sends events to the console over HTTP, from a background loop, with the signature the console checks.
/// </summary>
/// <remarks>
/// A bounded queue and a sender that retries three times and then gives up. Giving up is the important half: an event
/// that cannot be delivered is worth less than the connection it was describing, and a queue that grows without bound
/// turns a console outage into a memory leak in the signaling server.
/// </remarks>
public sealed partial class HttpOpsEventSink : BackgroundService, IOpsEventSink
{
    private const int Capacity = 4096;
    private const int MaxBatch = 100;
    private const int MaxAttempts = 3;

    private readonly Channel<OpsEventEnvelope> _queue;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly Uri _endpoint;
    private readonly byte[] _key;
    private readonly string _keyId;
    private readonly string _node;
    private readonly ILogger<HttpOpsEventSink> _logger;
    private long _dropped;

    /// <summary>
    /// Builds the sink. <paramref name="key"/> is Base64 and must be at least 32 bytes, matching what the console
    /// accepts; anything shorter is refused at start-up rather than silently signing with a weak key.
    /// </summary>
    public HttpOpsEventSink(string endpoint, string key, string keyId, string node, ILogger<HttpOpsEventSink> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(logger);

        var endpointUri = new Uri(endpoint, UriKind.Absolute);

        // The deployment document configures the console's base address ("http://127.0.0.1:5090"); the ingest path
        // is fixed on the console. Accept both spellings — a base address that is posted to verbatim answers 405 and
        // every event is dropped, which is exactly the failure this normalisation removes.
        _endpoint = string.IsNullOrEmpty(endpointUri.AbsolutePath.Trim('/'))
            ? new Uri(endpointUri, "ingest/events")
            : endpointUri;

        _key = Convert.FromBase64String(key);
        if (_key.Length < 32)
        {
            throw new InvalidOperationException("OpsEvents:Key must be a base64 key of at least 32 bytes.");
        }

        _keyId = keyId;
        _node = node;
        _logger = logger;
        _queue = Channel.CreateBounded<OpsEventEnvelope>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <inheritdoc />
    public bool Enabled => true;

    /// <summary>Events dropped because the queue was full or delivery kept failing. Exposed for tests.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <inheritdoc />
    public void Publish(OpsEventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!_queue.Writer.TryWrite(envelope))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<OpsEventEnvelope>(MaxBatch);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < MaxBatch && _queue.Reader.TryRead(out var envelope))
                {
                    batch.Add(envelope);
                }

                if (batch.Count > 0)
                {
                    await SendAsync([.. batch], stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown; whatever is queued is lost, and that is the same trade as the queue's bound.
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }

    private async Task SendAsync(IReadOnlyList<OpsEventEnvelope> batch, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                var body = JsonSerializer.SerializeToUtf8Bytes(batch, OpsEventJson.Options);
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
                {
                    Content = new ByteArrayContent(body),
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                request.Headers.Add(OpsEventSigner.KeyIdHeader, _keyId);
                request.Headers.Add(OpsEventSigner.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
                request.Headers.Add(OpsEventSigner.SignatureHeader, OpsEventSigner.Sign(_key, timestamp, body));

                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    Delivered(_logger, _node, batch.Count);
                    return;
                }

                DeliveryRejected(_logger, _node, batch.Count, (int)response.StatusCode, attempt);
            }
            catch (HttpRequestException ex)
            {
                DeliveryFailed(_logger, _node, ex.Message, attempt);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                DeliveryFailed(_logger, _node, "timed out", attempt);
            }

            if (attempt < MaxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt * attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        Interlocked.Add(ref _dropped, batch.Count);
        BatchDropped(_logger, _node, batch.Count);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sent {Count} events from {Node} to the console.")]
    private static partial void Delivered(ILogger logger, string node, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The console refused {Count} events from {Node} with HTTP {Status} (attempt {Attempt}).")]
    private static partial void DeliveryRejected(ILogger logger, string node, int count, int status, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Delivering events from {Node} failed: {Reason} (attempt {Attempt}).")]
    private static partial void DeliveryFailed(ILogger logger, string node, string reason, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dropped {Count} events from {Node}: delivery kept failing.")]
    private static partial void BatchDropped(ILogger logger, string node, int count);
}

/// <summary>Wires the sink from <c>OpsEvents:*</c>; a deployment without an endpoint gets the no-op sink.</summary>
public static class OpsEventsRegistration
{
    /// <summary>
    /// Registers the sink. Reads <c>OpsEvents:Endpoint</c>, <c>OpsEvents:Key</c>, <c>OpsEvents:KeyId</c> and
    /// <c>OpsEvents:Node</c>; <paramref name="defaultNode"/> is used when the last is absent.
    /// </summary>
    public static IServiceCollection AddOpsEvents(
        this IServiceCollection services,
        IConfiguration configuration,
        string defaultNode)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var endpoint = configuration["OpsEvents:Endpoint"];
        var key = configuration["OpsEvents:Key"];
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key))
        {
            services.AddSingleton<IOpsEventSink, NoopOpsEventSink>();
            return services;
        }

        var keyId = configuration["OpsEvents:KeyId"] ?? "k1";
        var node = configuration["OpsEvents:Node"] ?? defaultNode;

        services.AddSingleton(sp => new HttpOpsEventSink(
            endpoint,
            key,
            keyId,
            node,
            sp.GetRequiredService<ILogger<HttpOpsEventSink>>()));
        services.AddSingleton<IOpsEventSink>(sp => sp.GetRequiredService<HttpOpsEventSink>());
        services.AddHostedService(sp => sp.GetRequiredService<HttpOpsEventSink>());
        return services;
    }
}
