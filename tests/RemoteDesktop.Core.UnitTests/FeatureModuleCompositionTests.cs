using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Audio;
using RemoteDesktop.Abstractions.Camera;
using RemoteDesktop.Abstractions.Clipboard;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Abstractions.Terminal;
using RemoteDesktop.Application.Features;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.Camera;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Features.Terminal;
using RemoteDesktop.Hosts.Composition;
using RemoteDesktop.Media;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The host-feature helper a host composes itself from, checked as one set: which channels end up served, and under
/// which permission. Every other test in this repository either serves a channel it registered by hand or
/// drives the dispatcher with a fake module, so a feature that quietly stopped being registered — or one that asked
/// for the wrong permission — would leave all of them green.
/// </summary>
/// <remarks>
/// The hosts call <see cref="ServiceCollectionExtensions.AddRemoteDesktopHostFeatures"/>: <c>RemoteDesktop.HostService</c>
/// on Linux and macOS, <c>RemoteDesktop.SessionAgent</c>, and the client when it hosts incoming sessions itself.
/// </remarks>
public class FeatureModuleCompositionTests
{
    /// <summary>The channel-to-permission table the host's authorisation is built on, written out independently.</summary>
    private static readonly (ChannelType Channel, Permissions Permission)[] Expected =
    [
        (ChannelType.Video, Permissions.ViewScreen),
        (ChannelType.Input, Permissions.ControlInput),
        (ChannelType.Clipboard, Permissions.Clipboard),
        (ChannelType.Audio, Permissions.Audio),
        (ChannelType.FileTransfer, Permissions.FileTransfer),
        (ChannelType.Terminal, Permissions.Terminal),
        (ChannelType.Camera, Permissions.Camera),
    ];

    [Fact]
    public void The_host_feature_set_serves_exactly_the_seven_channels_with_their_own_permission()
    {
        using var serviceProvider = ComposeHostFeatures();

        var dispatcher = new ChannelDispatcher(serviceProvider.GetServices<IFeatureModule>());

        // GetRequiredPermission answers null for a channel no module serves, so a dropped feature shows up as a null
        // here rather than as a "feature missing" that only a person at the far end would notice.
        foreach (var (channel, permission) in Expected)
        {
            Assert.Equal<Permissions?>(permission, dispatcher.GetRequiredPermission(channel));
        }

        // The control channel belongs to the session itself and is served before any module is looked up, so no
        // feature may claim it.
        Assert.Null(dispatcher.GetRequiredPermission(ChannelType.Control));
    }

    [Fact]
    public void A_watching_session_is_refused_every_channel_that_could_change_the_host()
    {
        using var serviceProvider = ComposeHostFeatures();
        var dispatcher = new ChannelDispatcher(serviceProvider.GetServices<IFeatureModule>());

        // The controller may ask for any of them; the grant it was given covers only what watching means.
        var watching = new SessionContext(
            SessionId.New(),
            SessionRole.Host,
            connection: null!,
            AccessGrant.Create(SessionMode.View, Permissions.All, Permissions.All));

        Assert.Equal(DispatchResult.Accepted, dispatcher.Authorize(ChannelType.Video, watching, out _));
        Assert.Equal(DispatchResult.Accepted, dispatcher.Authorize(ChannelType.Audio, watching, out _));

        // Written out rather than derived from Permissions.ViewOnly: the expectation is the sentence above, not a
        // second copy of the product's own mask arithmetic.
        foreach (var channel in new[]
                 {
                     ChannelType.Input,
                     ChannelType.Clipboard,
                     ChannelType.FileTransfer,
                     ChannelType.Terminal,
                     ChannelType.Camera,
                 })
        {
            var result = dispatcher.Authorize(channel, watching, out var module);

            Assert.Equal(DispatchResult.Denied, result);
            Assert.Null(module);
        }
    }

    [Fact]
    public void A_controlled_session_that_was_granted_everything_is_accepted_on_every_channel()
    {
        using var serviceProvider = ComposeHostFeatures();
        var dispatcher = new ChannelDispatcher(serviceProvider.GetServices<IFeatureModule>());

        // What a host reports it can hand out: everything it actually serves. Ctrl+Alt+Del is a capability of the
        // machine, not of a channel, and no module asks for it.
        var controlling = new SessionContext(
            SessionId.New(),
            SessionRole.Host,
            connection: null!,
            AccessGrant.Create(SessionMode.Control, Permissions.All, Permissions.All));

        foreach (var (channel, _) in Expected)
        {
            Assert.Equal(DispatchResult.Accepted, dispatcher.Authorize(channel, controlling, out var module));
            Assert.NotNull(module);
        }
    }

    /// <summary>
    /// Composes the four feature calls with the platform ports stubbed, then builds the provider so every module's
    /// own dependencies are validated — a module that lost a constructor dependency fails here, not at first use.
    /// </summary>
    private static ServiceProvider ComposeHostFeatures()
    {
        var services = new ServiceCollection();

        // The hosts always add logging; a module that takes an ILogger cannot be built without a provider.
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        // Platform ports. None of them is called: only ChannelType and RequiredPermission are read, and a test that
        // reached further would be testing the module, which has its own tests.
        services.AddSingleton<IDisplayEnumerator>(new StubDisplays());
        services.AddSingleton<IScreenCapturerFactory>(new UnusableScreenCapturers());
        services.AddSingleton<IInputInjector>(new InertInputInjector());
        services.AddSingleton<IClipboard>(new InertClipboard());
        services.AddSingleton<IAudioCapturerFactory>(new UnusableAudioCapturers());
        services.AddSingleton<ICameraEnumerator>(new StubCameras());
        services.AddSingleton<ICameraSourceFactory>(new UnusableCameraSources());
        services.AddSingleton<IUserContextGuard>(new UnrestrictedUserContext());
        services.AddSingleton<IPtyHost>(new UnusablePtyHost());

        // Both the video and the camera module take the selector; with no codec factories registered it simply has
        // no candidates, which is all this test needs from it.
        services.AddSingleton<VideoCodecSelector>();

        // Supplied by AddRemoteDesktopApplication in a real host, which adds signalling and transport with it.
        services.AddSingleton(TimeProvider.System);

        var provider = services
            .AddRemoteDesktopHostFeatures(Path.Combine(Path.GetTempPath(), "rd-compose-test"))
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        // The desktop feature registers four modules of its own; the three others one each.
        Assert.Equal(Expected.Length, provider.GetServices<IFeatureModule>().Count());
        Assert.NotNull(provider.GetRequiredService<VideoCodecSelector>());

        return provider;
    }

    private sealed class StubDisplays : IDisplayEnumerator
    {
        public IReadOnlyList<DisplayInfo> GetDisplays() => [];
    }

    private sealed class UnusableScreenCapturers : IScreenCapturerFactory
    {
        public IScreenCapturer Create(DisplayInfo display) => throw new NotSupportedException();
    }

    private sealed class InertInputInjector : IInputInjector
    {
        public bool SupportsSecureAttention => false;

        public void Inject(InputEvent inputEvent) => throw new NotSupportedException();

        public void SendSecureAttentionSequence() => throw new NotSupportedException();
    }

    private sealed class InertClipboard : IClipboard
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public ClipboardContent? Get() => null;

        public void Set(ClipboardContent content) => throw new NotSupportedException();
    }

    private sealed class UnusableAudioCapturers : IAudioCapturerFactory
    {
        public IAudioCapturer Create() => throw new NotSupportedException();
    }

    private sealed class StubCameras : ICameraEnumerator
    {
        public IReadOnlyList<CameraInfo> GetCameras() => [];
    }

    private sealed class UnusableCameraSources : ICameraSourceFactory
    {
        public ICameraSource Open(string cameraId) => throw new NotSupportedException();
    }

    private sealed class UnrestrictedUserContext : IUserContextGuard
    {
        public string? GetRefusalReason() => null;
    }

    private sealed class UnusablePtyHost : IPtyHost
    {
        public IReadOnlyList<string> GetAvailableShells() => [];

        public IPtySession Start(PtyStartOptions options) => throw new NotSupportedException();
    }
}
