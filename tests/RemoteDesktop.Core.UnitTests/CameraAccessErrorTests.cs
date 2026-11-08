using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Camera;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.Camera;
using RemoteDesktop.Media;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Camera;
using RemoteDesktop.Protocol.Framing;

namespace RemoteDesktop.Core.UnitTests;

public class CameraAccessErrorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(CameraAccessState.Denied)]
    [InlineData(CameraAccessState.Unset)]
    public async Task Camera_module_returns_explicit_refusal_when_access_is_not_allowed(CameraAccessState state)
    {
        var (server, client) = await TestPipe.ConnectAsync(Ct);
        await using var serverEnd = server;
        await using var clientEnd = client;

        var channel = new FakeChannel(ChannelType.Camera, serverEnd);
        var session = new SessionContext(
            SessionId.New(),
            SessionRole.Host,
            null!,
            AccessGrant.Create(SessionMode.Control, Permissions.All, Permissions.All));

        var cameraAccess = new StubCameraAccess(state);
        var codecSelector = new VideoCodecSelector([new StubVideoEncoderFactory()], []);
        var module = new CameraModule(
            new StubCameraEnumerator([]),
            new StubCameraSourceFactory(),
            new StubUserContextGuard(),
            codecSelector,
            TimeProvider.System,
            NullLogger<CameraModule>.Instance,
            cameraAccess);

        var handling = module.HandleAsync(channel, session, Ct);

        // Client requests camera listing
        await ProtobufFrames.WriteAsync(clientEnd, new CameraRequest { List = new ListCamerasRequest() }, Ct);

        // Client reads the response frame
        var response = await ProtobufFrames.ReadAsync(clientEnd, CameraEvent.Parser, 65536, Ct);
        Assert.NotNull(response);
        Assert.Equal(CameraEvent.BodyOneofCase.Failed, response.BodyCase);
        Assert.Equal(CameraError.NotAllowed, response.Failed.Error);
        Assert.Equal(
            "被控端未允许摄像头访问。请在被控端「高级设置 → 安全 → 摄像头权限」中允许后重试。",
            response.Failed.Message);

        await clientEnd.DisposeAsync();
        await handling;
    }

    [Fact]
    public async Task Camera_module_returns_camera_list_when_access_is_allowed()
    {
        var (server, client) = await TestPipe.ConnectAsync(Ct);
        await using var serverEnd = server;
        await using var clientEnd = client;

        var channel = new FakeChannel(ChannelType.Camera, serverEnd);
        var session = new SessionContext(
            SessionId.New(),
            SessionRole.Host,
            null!,
            AccessGrant.Create(SessionMode.Control, Permissions.All, Permissions.All));

        var cameraAccess = new StubCameraAccess(CameraAccessState.Allowed);
        var camera = new CameraInfo("cam1", "Test Camera", [new CameraMode(1280, 720, 30)]);
        var codecSelector = new VideoCodecSelector([new StubVideoEncoderFactory()], []);
        var module = new CameraModule(
            new StubCameraEnumerator([camera]),
            new StubCameraSourceFactory(),
            new StubUserContextGuard(),
            codecSelector,
            TimeProvider.System,
            NullLogger<CameraModule>.Instance,
            cameraAccess);

        var handling = module.HandleAsync(channel, session, Ct);

        await ProtobufFrames.WriteAsync(clientEnd, new CameraRequest { List = new ListCamerasRequest() }, Ct);

        var response = await ProtobufFrames.ReadAsync(clientEnd, CameraEvent.Parser, 65536, Ct);
        Assert.NotNull(response);
        Assert.Equal(CameraEvent.BodyOneofCase.Cameras, response.BodyCase);
        var received = Assert.Single(response.Cameras.Cameras);
        Assert.Equal("cam1", received.Id);
        Assert.Equal("Test Camera", received.Name);

        await clientEnd.DisposeAsync();
        await handling;
    }

    private sealed class StubCameraAccess(CameraAccessState state) : ICameraAccess
    {
        public Task<CameraAccessState> GetStateAsync(CancellationToken cancellationToken) => Task.FromResult(state);
        public Task SetAllowedAsync(bool allowed, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubCameraEnumerator(IReadOnlyList<CameraInfo> cameras) : ICameraEnumerator
    {
        public IReadOnlyList<CameraInfo> GetCameras() => cameras;
    }

    private sealed class StubCameraSourceFactory : ICameraSourceFactory
    {
        public ICameraSource Open(string cameraId) => throw new NotSupportedException();
    }

    private sealed class StubUserContextGuard : IUserContextGuard
    {
        public string? GetRefusalReason() => null;
    }

    private sealed class StubVideoEncoderFactory : IVideoEncoderFactory
    {
        public string Name => "Stub";
        public bool IsHardware => false;
        public int Priority => 0;
        public bool IsSupported(VideoEncoderOptions options) => true;
        public IVideoEncoder Create(VideoEncoderOptions options) => throw new NotSupportedException();
    }
}
