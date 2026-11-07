using System.Globalization;
using System.Net;
using System.Reflection;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Signaling;
using RemoteDesktop.Signaling.Messages;
using RemoteDesktop.Transport.Identity;
using RemoteDesktop.Transport.Rendezvous;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// A session-end report describes a QUIC session, which has nothing to do with the signaling socket it travels over.
/// When that socket dies before the report is on the wire, the report has to wait for the next connection.
/// </summary>
public sealed class SessionEndReportRetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Admin_commands_apply_backpressure_instead_of_being_dropped_or_growing_without_bound()
    {
        using var client = new RendezvousClient(
            new TransportOptions(),
            DeviceIdentity.CreateEphemeral(),
            NullLogger<RendezvousClient>.Instance);
        var queue = (System.Threading.Channels.Channel<AdminCommand>)typeof(RendezvousClient)
            .GetField("_admin", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client)!;

        for (var index = 0; index < 32; index++)
        {
            Assert.True(queue.Writer.TryWrite(new AdminCommand { CommandId = index.ToString(CultureInfo.InvariantCulture) }));
        }

        Assert.False(queue.Writer.TryWrite(new AdminCommand { CommandId = "overflow" }));
        var blocked = queue.Writer.WriteAsync(new AdminCommand { CommandId = "wait" }, Ct).AsTask();
        Assert.False(blocked.IsCompleted);

        Assert.True(queue.Reader.TryRead(out _));
        await blocked.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task A_report_that_could_not_be_sent_before_the_signaling_connection_died_is_delivered_after_the_reconnect()
    {
        await using var server = await FakeSignaling.StartAsync();
        using var client = new RendezvousClient(
            new TransportOptions { RendezvousUri = server.Uri },
            DeviceIdentity.CreateEphemeral(),
            NullLogger<RendezvousClient>.Instance);
        var online = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var offline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StateChanged += (_, _) =>
        {
            if (client.State == RendezvousState.Online)
            {
                online.TrySetResult();
            }
            else if (client.State == RendezvousState.Offline)
            {
                offline.TrySetResult();
            }
        };

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = client.RunAsync(stop.Token);
        try
        {
            await online.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

            // The send lock is held, so the end loop takes the report off its queue and then has to wait to send it:
            // exactly where it is when a connection breaks under it. Holding it is the only way to make that moment
            // last; without it the report is sent (or not) in the microseconds before the break is noticed.
            var sendLock = (SemaphoreSlim)typeof(RendezvousClient)
                .GetField("_sendLock", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(client)!;
            await sendLock.WaitAsync(Ct);
            client.ReportSessionEnded(new SessionEndedReport { SessionId = "0123456789abcdef0123456789abcdef", Role = TelemetryRole.Host, Reason = "closed" });
            await Task.Delay(200, Ct);

            // The server goes away before it has received anything.
            server.DropCurrentConnection();
            await offline.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            sendLock.Release();

            // The device reconnects by itself, and the report is on the new connection.
            var delivered = await server.NextSessionEndedAsync().WaitAsync(TimeSpan.FromSeconds(15), Ct);
            Assert.Equal("0123456789abcdef0123456789abcdef", delivered.SessionId);
            Assert.Equal(2, server.Connections);
        }
        finally
        {
            await stop.CancelAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }
    }

    /// <summary>
    /// A signaling server that registers whoever connects and records the end reports it receives. The first
    /// connection is the one a test breaks on purpose.
    /// </summary>
    private sealed class FakeSignaling : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly TaskCompletionSource<SessionEndedReport> _received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _drop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connections;

        private FakeSignaling(WebApplication app, Uri uri)
        {
            _app = app;
            Uri = uri;
        }

        public Uri Uri { get; }

        public int Connections => Volatile.Read(ref _connections);

        public static async Task<FakeSignaling> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            app.UseWebSockets();
            FakeSignaling? self = null;
            app.Map("/ws", context => self!.ServeAsync(context));
            await app.StartAsync();
            var port = new Uri(app.Urls.First()).Port;
            self = new FakeSignaling(app, new Uri($"ws://127.0.0.1:{port}/ws"));
            return self;
        }

        public void DropCurrentConnection() => _drop.TrySetResult();

        public Task<SessionEndedReport> NextSessionEndedAsync() => _received.Task;

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();

        private async Task ServeAsync(HttpContext context)
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var first = Interlocked.Increment(ref _connections) == 1;
            var token = context.RequestAborted;

            await WebSocketMessaging.SendAsync(
                socket,
                new ServerMessage { RegisterChallenge = new RegisterChallenge { Nonce = ByteString.CopyFrom(new byte[32]) } },
                token);
            _ = await WebSocketMessaging.ReceiveAsync(socket, ClientMessage.Parser, token);
            await WebSocketMessaging.SendAsync(
                socket,
                new ServerMessage { Registered = new Registered { DeviceId = 123456789, ReflectorPort = 1 } },
                token);

            if (first)
            {
                await _drop.Task.WaitAsync(token);
                context.Abort();
                return;
            }

            while (await WebSocketMessaging.ReceiveAsync(socket, ClientMessage.Parser, token) is { } message)
            {
                if (message.BodyCase == ClientMessage.BodyOneofCase.SessionEnded)
                {
                    _received.TrySetResult(message.SessionEnded);
                }
            }
        }
    }
}
