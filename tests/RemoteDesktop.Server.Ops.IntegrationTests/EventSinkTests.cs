using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// The sender half of the event path: a real HTTP consumer, a real signature, and the body it receives. The ingest
/// tests cover the receiver; without this one, a mismatch between what is signed and what is sent would go unnoticed
/// until a real server tried it.
/// </summary>
public class EventSinkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_sink_delivers_a_signed_batch_the_receiver_can_verify()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        await using var consumer = await Consumer.StartAsync();

        using var sink = new HttpOpsEventSink(
            consumer.Url + "/ingest/events",
            Convert.ToBase64String(key),
            "k1",
            "relay-test",
            NullLogger<HttpOpsEventSink>.Instance);

        await sink.StartAsync(Ct);
        sink.Publish(new OpsEventEnvelope
        {
            EventId = Guid.CreateVersion7().ToString("D"),
            Node = "relay-test",
            Kind = OpsEventKinds.RelayPaired,
            OccurredAt = DateTimeOffset.UtcNow,
            CorrelationId = Guid.NewGuid().ToString("D"),
            Payload = """{"relaySessionId":"x"}""",
        });

        var delivery = await consumer.Received.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal("k1", delivery.KeyId);
        Assert.True(
            OpsEventSigner.Verify(key, delivery.Timestamp, delivery.Body, delivery.Signature),
            "the signature must cover exactly the bytes that were sent");

        var events = JsonSerializer.Deserialize<List<OpsEventEnvelope>>(delivery.Body, OpsEventJson.Options);
        var single = Assert.Single(events!);
        Assert.Equal(OpsEventKinds.RelayPaired, single.Kind);
        Assert.Equal("relay-test", single.Node);

        await sink.StopAsync(Ct);
    }

    [Fact]
    public async Task A_sink_without_an_endpoint_is_the_no_op_one()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationManager());

        services.AddOpsEvents(new Microsoft.Extensions.Configuration.ConfigurationManager(), "relay-test");
        await using var provider = services.BuildServiceProvider();

        Assert.IsType<NoopOpsEventSink>(provider.GetRequiredService<IOpsEventSink>());
        Assert.False(provider.GetRequiredService<IOpsEventSink>().Enabled);
    }

    /// <summary>A one-endpoint HTTP server that hands the request to the test.</summary>
    private sealed class Consumer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private Consumer(WebApplication app) => _app = app;

        public string Url { get; private set; } = string.Empty;

        public TaskCompletionSource<Delivery> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static async Task<Consumer> StartAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["urls"] = "http://127.0.0.1:0" });
            var app = builder.Build();
            var consumer = new Consumer(app);

            app.MapPost("/ingest/events", async (HttpContext context) =>
            {
                using var buffer = new MemoryStream();
                await context.Request.Body.CopyToAsync(buffer, TestContext.Current.CancellationToken);
                consumer.Received.TrySetResult(new Delivery(
                    buffer.ToArray(),
                    context.Request.Headers[OpsEventSigner.KeyIdHeader].ToString(),
                    long.Parse(context.Request.Headers[OpsEventSigner.TimestampHeader].ToString(), System.Globalization.CultureInfo.InvariantCulture),
                    context.Request.Headers[OpsEventSigner.SignatureHeader].ToString()));
                return Results.Ok();
            });

            await app.StartAsync(TestContext.Current.CancellationToken);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            consumer.Url = address;
            return consumer;
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }

    /// <summary>What the consumer saw.</summary>
    public sealed record Delivery(byte[] Body, string KeyId, long Timestamp, string Signature);
}
