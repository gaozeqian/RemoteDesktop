using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Application;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Application.Hosting;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.Camera;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Features.Terminal;
using RemoteDesktop.Media;
using RemoteDesktop.Platform.Linux;
using RemoteDesktop.Platform.Linux.Display;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Desktop;
using RemoteDesktop.Protocol.Framing;
using RemoteDesktop.Server.Rendezvous;
using RemoteDesktop.Transport;
using RemoteDesktop.Platform.Posix;
using RemoteDesktop.Transport.Rendezvous;

namespace RemoteDesktop.Platform.Linux.Tests;

/// <summary>
/// A whole session: a real host and a real controller meeting through a real rendezvous server.
/// </summary>
/// <remarks>
/// <para>
/// Everything else in this project tests one piece; this tests that the pieces are wired to each other. That is not a
/// formality — the pieces each passed while the join between them was broken, twice during this port: a screen-cast
/// session that was never started, and a format pod that the connect call refused. Both looked like a working
/// component until something asked it for a picture.
/// </para>
/// <para>
/// No test harness stands in for anything: the host is composed the way the service composes itself, the controller
/// the way the client does, and the picture comes off the real screen through the desktop portal.
/// </para>
/// <para>
/// Opt-in, like the other portal tests, and it asks for more than they do — a screen to share and permission to
/// control it — so it will cause two dialogs on a machine that has not granted either.
/// </para>
/// </remarks>
public sealed class SessionEndToEndTests
{
    private const string LinuxOnly = "The desktop portals and libei, which only exist on Linux.";
    private const string NeedsConsent = "Needs a person to share a screen and allow control; set RD_PORTAL_TEST=1.";

    private static bool Enabled => Environment.GetEnvironmentVariable("RD_PORTAL_TEST") == "1";

    [Fact]
    public async Task A_controller_sees_the_screen_and_moves_its_pointer()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
        Assert.SkipWhen(!Enabled, NeedsConsent);
        Assert.SkipUnless(MutterDisplayConfig.IsAvailable(), "No display layout to work from.");

        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("rd-e2e-").FullName;

        try
        {
            var rendezvous = await StartRendezvousAsync();

            using var host = await StartHostAsync(Path.Combine(root, "host"), rendezvous, cancellationToken);
            var hostRendezvous = host.Services.GetRequiredService<IRendezvousClient>();
            var credentials = host.Services.GetRequiredService<HostCredentials>();
            await WaitUntilOnlineAsync(hostRendezvous, cancellationToken);

            using var controller = await StartControllerAsync(Path.Combine(root, "controller"), rendezvous, cancellationToken);
            await WaitUntilOnlineAsync(controller.Services.GetRequiredService<IRendezvousClient>(), cancellationToken);

            await using var session = await controller.Services.GetRequiredService<ControllerSessionService>()
                .ConnectAsync(hostRendezvous.DeviceId!.Value, credentials.TemporaryPassword, SessionMode.Control, cancellationToken);

            var display = controller.Services.GetRequiredService<IDisplayEnumerator>().GetDisplays()[0];
            var decoders = controller.Services.GetRequiredService<IVideoDecoderFactory>();

            await using var video = await session.Context.Connection.OpenChannelAsync(ChannelType.Video, bidirectional: true, cancellationToken);
            await ProtobufFrames.WriteAsync(
                video.Stream,
                new VideoControl { Request = new VideoRequest { DisplayId = display.Id, MaxFrameRate = 30 } },
                cancellationToken);

            using var decoder = decoders.Create(VideoCodec.H264, display.Bounds.Width, display.Bounds.Height);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));

            var before = await KeyFramePrintAsync(video, decoder, deadline.Token);

            await using var input = await session.Context.Connection.OpenChannelAsync(ChannelType.Input, bidirectional: false, cancellationToken);
            await MovePointerAsync(input, display.Id, 0.0, 0.0, cancellationToken);

            // A key frame on request is, by definition, the screen as it is now — so a picture that differs can only
            // have changed because the pointer went somewhere.
            await ProtobufFrames.WriteAsync(video.Stream, new VideoControl { KeyFrameRequest = new KeyFrameRequest() }, cancellationToken);
            var after = await KeyFramePrintAsync(video, decoder, deadline.Token);

            Assert.False(
                before.AsSpan().SequenceEqual(after),
                "The picture did not change, so input did not reach the screen.");

            // Put the pointer back in the middle, which is also what closes the overview the corner opened.
            await MovePointerAsync(input, display.Id, 0.5, 0.5, cancellationToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Reads until a key frame arrives, decodes it, and boils it down to something comparable.</summary>
    private static async Task<byte[]> KeyFramePrintAsync(IChannel channel, IVideoDecoder decoder, CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = await FrameCodec.ReadAsync(channel.Stream, FrameCodec.DefaultMaxPayloadSize, cancellationToken)
                ?? throw new InvalidDataException("The channel closed before a key frame arrived.");

            if (frame.MessageType != VideoDataFrame.MessageType)
            {
                continue;
            }

            var (encoded, _) = VideoDataFrame.Parse(frame);
            if (!encoded.IsKeyFrame)
            {
                continue;
            }

            using var picture = decoder.Decode(encoded) ?? throw new InvalidDataException("A key frame produced no picture.");
            var pixels = picture.GetPixels(out var stride);

            // The Y plane, sampled coarsely across the whole picture. This only has to tell two different screens
            // apart, and a finer sample would fail on a blinking caret.
            var print = new byte[64];
            for (var i = 0; i < print.Length; i++)
            {
                print[i] = pixels[i * ((stride * picture.Height) - 1) / print.Length];
            }

            return print;
        }
    }

    private static async Task MovePointerAsync(IChannel input, string displayId, double x, double y, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 10; i++)
        {
            await ProtobufFrames.WriteAsync(input.Stream, InputMessages.ToMessage(new PointerMoveEvent(displayId, x, y)), cancellationToken);
            await Task.Delay(80, cancellationToken);
        }
    }

    private static async Task<Uri> StartRendezvousAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            // A fixed reflector port would have every server in a test run fighting over the same one.
            ["Rendezvous:ReflectorPort"] = "0",
        });
        builder.Services.AddRendezvous(builder.Configuration);

        var server = builder.Build();
        server.MapRendezvous();
        await server.StartAsync();

        return new Uri($"ws://127.0.0.1:{new Uri(server.Urls.First()).Port}{RendezvousServer.SignalingPath}");
    }

    /// <summary>
    /// The controlled end, composed the way the service composes itself on this platform.
    /// </summary>
    /// <remarks>
    /// Its own data directory would be a directory the desktop has never granted anything to, so the host would stop
    /// at a dialog nobody is there to answer and the test would fail as a timeout — which is exactly what happened
    /// the first time this ran. The grant is copied across instead: the test keeps its state to itself, and the
    /// permission the person already gave is the one it uses.
    /// </remarks>
    private static async Task<IHost> StartHostAsync(string dataDirectory, Uri rendezvous, CancellationToken cancellationToken)
    {
        InheritPortalGrant(dataDirectory);

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();

        builder.Services
            .AddSingleton(TimeProvider.System)
            .AddLinuxPlatform(dataDirectory)
            .AddQuicTransport(new TransportOptions { RendezvousUri = rendezvous })
            .AddRemoteDesktopApplication()
            .AddRemoteDesktopMedia()
            .AddDesktopFeature()
            .AddFileTransferFeature(partialFileLedgerPath: Path.Combine(dataDirectory, "partial-files.log"))
            .AddTerminalFeature()
            .AddCameraFeature()
            .AddLocalHostControl(isService: false);

        var host = builder.Build();
        await host.StartAsync(cancellationToken);
        return host;
    }

    /// <summary>The controlling end, with a data directory of its own so it is a separate device.</summary>
    private static async Task<IHost> StartControllerAsync(string dataDirectory, Uri rendezvous, CancellationToken cancellationToken)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();

        builder.Services
            .AddSingleton(TimeProvider.System)
            .AddLinuxPlatform(dataDirectory)
            .AddQuicTransport(new TransportOptions { RendezvousUri = rendezvous })
            .AddRemoteDesktopApplication();

        var host = builder.Build();
        await host.StartAsync(cancellationToken);
        return host;
    }

    /// <summary>Copies the desktop's screen-sharing token into a data directory that has not got one.</summary>
    private static void InheritPortalGrant(string dataDirectory)
    {
        var granted = new FileSecretStore(Path.Combine(DataDirectory.ForUser(), "secrets"));
        if (granted.Load("portal-restore-token") is not { Length: > 0 } token)
        {
            return;
        }

        new FileSecretStore(Path.Combine(dataDirectory, "secrets")).Save("portal-restore-token", token);
    }

    private static async Task WaitUntilOnlineAsync(IRendezvousClient client, CancellationToken cancellationToken)
    {
        var online = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StateChanged += (_, _) =>
        {
            if (client.State == RendezvousState.Online)
            {
                online.TrySetResult();
            }
        };

        if (client.State == RendezvousState.Online)
        {
            online.TrySetResult();
        }

        await online.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
    }
}
