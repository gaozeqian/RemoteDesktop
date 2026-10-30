using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Platform.Linux.Display;
using RemoteDesktop.Platform.Linux.Input;
using RemoteDesktop.Platform.Linux.XdgPortal;
using RemoteDesktop.Platform.Posix;

namespace RemoteDesktop.Platform.Linux.UnitTests;

/// <summary>
/// Controlling a Wayland session with libei, through a descriptor the portal hands over.
/// </summary>
/// <remarks>
/// <para>
/// Opt-in, and this one asks more than the others do: a remote-desktop session cannot be persisted — the portal
/// refuses it — so unlike screen sharing, control is asked for every time. That is a deliberate restriction rather
/// than something to work around, and these tests are written knowing they will cause a dialog.
/// </para>
/// <para>
/// The awkward part is proving the pointer moved. Wayland will not tell anyone where the pointer is, and the cursor
/// metadata that would have said so does not arrive on this compositor (see <c>PipeWireScreenCapturer</c>, which
/// warns once and carries on). What is left is the picture itself, and the use of it here is deliberately blunt:
/// the top-left corner opens the overview on a move alone, with no click, so a changed screen means the pointer
/// went where it was sent. The pointer is put back where it was found before the test ends.
/// </para>
/// </remarks>
public sealed class LibEiInputInjectorTests
{
    private const string LinuxOnly = "libei and the desktop portals, which only exist on Linux.";
    private const string NeedsConsent = "Needs a person to allow control; set RD_PORTAL_TEST=1 to run it.";

    private static bool Enabled => Environment.GetEnvironmentVariable("RD_PORTAL_TEST") == "1";

    private static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RemoteDesktop");

    [Fact]
    public void The_secure_attention_sequence_is_refused_rather_than_faked()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // Ctrl+Alt+Del is a Windows idea: a keystroke the system takes before any application sees it, so that only
        // the real system can ask for a password. Wayland has nothing of the kind, and the honest answer is to say
        // so rather than to send the three keys and let somebody believe it worked.
        using var control = new PortalControlSession(NullLoggerFactory.Instance, NullLogger<PortalControlSession>.Instance);
        using var injector = new LibEiInputInjector(
            new WaylandDisplayEnumerator(NullLogger<WaylandDisplayEnumerator>.Instance),
            control,
            NullLogger<LibEiInputInjector>.Instance);

        Assert.Throws<NotSupportedException>(() => injector.SendSecureAttentionSequence());
    }

    [Fact]
    public async Task Moving_the_pointer_moves_the_pointer()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
        Assert.SkipWhen(!Enabled, NeedsConsent);
        Assert.SkipUnless(MutterDisplayConfig.IsAvailable(), "No display layout to compare against.");

        using var displays = new WaylandDisplayEnumerator(NullLogger<WaylandDisplayEnumerator>.Instance);
        var target = displays.GetDisplays().FirstOrDefault(d => d.IsPrimary)
            ?? throw new InvalidOperationException("No primary screen.");

        using var grant = new PortalGrant(
            new FileSecretStore(Path.Combine(DataDirectory, "secrets")),
            NullLoggerFactory.Instance,
            NullLogger<PortalGrant>.Instance);

        var capturers = new PipeWireScreenCapturerFactory(
            grant,
            displays,
            NullLoggerFactory.Instance,
            NullLogger<PipeWireScreenCapturerFactory>.Instance);

        await using var capturer = capturers.Create(target);
        using var control = new PortalControlSession(NullLoggerFactory.Instance, NullLogger<PortalControlSession>.Instance);
        using var injector = new LibEiInputInjector(displays, control, NullLogger<LibEiInputInjector>.Instance);

        var before = await FingerprintAsync(capturer);

        try
        {
            // No click. The pointer reaching the top-left corner opens the overview by itself, so a changed picture
            // can only have come from the pointer having gone there.
            for (var i = 0; i < 8; i++)
            {
                injector.Inject(new PointerMoveEvent(target.Id, 0.0, 0.0));
                await Task.Delay(60, TestContext.Current.CancellationToken);
            }

            var after = await FingerprintAsync(capturer);

            Assert.NotEqual(before, after);
        }
        finally
        {
            // Put the pointer back in the middle, which is also what closes the overview again.
            for (var i = 0; i < 8; i++)
            {
                injector.Inject(new PointerMoveEvent(target.Id, 0.5, 0.5));
                await Task.Delay(60, TestContext.Current.CancellationToken);
            }
        }
    }

    /// <summary>A cheap summary of a frame, so two of them can be compared without keeping either.</summary>
    private static async Task<string> FingerprintAsync(IScreenCapturer capturer)
    {
        for (var attempt = 0; attempt < 25; attempt++)
        {
            using var frame = await capturer.CaptureAsync(
                TimeSpan.FromMilliseconds(400),
                TestContext.Current.CancellationToken);

            if (frame is null)
            {
                continue;
            }

            var pixels = frame.GetPixels(out var stride);
            var hash = 17L;

            // Coarse on purpose: this compares "is this the same screen" rather than "is this the same frame", and
            // a finer sample would fail on a blinking caret.
            for (var y = 0; y < frame.Height; y += 8)
            {
                for (var x = 0; x < frame.Width; x += 8)
                {
                    hash = (hash * 31) + pixels[(y * stride) + (x * 4)];
                }
            }

            return hash.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        }

        throw new InvalidOperationException("No frame arrived to compare against.");
    }
}
