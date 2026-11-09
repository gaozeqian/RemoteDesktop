using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RemoteDesktop.Server.Rendezvous;
using RemoteDesktop.Server.Shared;
using RemoteDesktop.Signaling.Messages;

namespace RemoteDesktop.Transport.IntegrationTests;

public sealed class TelemetryReceiverTests
{
    [Fact]
    public void Only_the_host_heartbeat_can_supply_host_platform_and_local_session_id()
    {
        var sink = new CaptureSink();
        var publisher = new NodeEventPublisher(sink, Options.Create(new RendezvousOptions()), TimeProvider.System);
        var sessions = new SessionDirectory(TimeProvider.System);
        var sessionId = Guid.NewGuid();
        sessions.Register(sessionId, controllerDeviceId: 10, hostDeviceId: 20);
        var receiver = new TelemetryReceiver(
            publisher,
            new ServerMetrics(),
            sessions,
            TimeProvider.System,
            NullLogger<TelemetryReceiver>.Instance);
        using var socket = WebSocket.CreateFromStream(new MemoryStream(), new WebSocketCreationOptions { IsServer = true });
        using var controller = new DeviceSession(10, "controller", 0, socket);
        using var host = new DeviceSession(20, "host", 0, socket);

        receiver.Handle(controller, Report(TelemetryRole.Controller, "linux", sessionId));
        receiver.Handle(host, Report(TelemetryRole.Host, "windows", sessionId));

        Assert.Equal(2, sink.Events.Count);
        using var controllerPayload = JsonDocument.Parse(sink.Events[0].Payload);
        Assert.Equal(JsonValueKind.Null, controllerPayload.RootElement.GetProperty("platform").ValueKind);
        Assert.Equal(JsonValueKind.Null, controllerPayload.RootElement.GetProperty("sessionId").ValueKind);
        using var hostPayload = JsonDocument.Parse(sink.Events[1].Payload);
        Assert.Equal("windows", hostPayload.RootElement.GetProperty("platform").GetString());
        Assert.Equal(sessionId.ToString("D"), hostPayload.RootElement.GetProperty("sessionId").GetString());
    }

    private static TelemetryReport Report(TelemetryRole role, string platform, Guid sessionId) => new()
    {
        SessionId = sessionId.ToString("D"),
        Role = role,
        Mode = "control",
        Transport = TelemetryTransport.Direct,
        RttMillis = 10,
        Platform = platform,
        Version = "1.0.0",
    };

    private sealed class CaptureSink : IOpsEventSink
    {
        public bool Enabled => true;

        public List<OpsEventEnvelope> Events { get; } = [];

        public void Publish(OpsEventEnvelope envelope) => Events.Add(envelope);
    }
}
