using System.Diagnostics;
using System.Globalization;
using System.Net.Quic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Abstractions.Audio;
using RemoteDesktop.Abstractions.Camera;
using RemoteDesktop.Abstractions.Clipboard;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Abstractions.Terminal;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Media;
using RemoteDesktop.Platform.MacOS;
using RemoteDesktop.Platform.MacOS.Clipboard;
using RemoteDesktop.Platform.MacOS.Input;
using RemoteDesktop.Protocol.File;

namespace RemoteDesktop.Client;

/// <summary>
/// The macOS half of <c>--self-check</c>: what this build's platform layer can actually do, from inside the bundle
/// it is running from.
/// </summary>
/// <remarks>
/// <para>
/// It exists because most of what goes wrong in a macOS port is invisible from the build: which dylibs the apphost
/// finds, whether the process is inside a signed bundle (TCC and the keychain both refuse outside one), and which
/// ports the container can actually resolve. All of those differ between <c>dotnet run</c> and the assembled
/// <c>.app</c>, and the assembled <c>.app</c> is the only configuration that matters.
/// </para>
/// <para>
/// Here rather than in the platform layer, unlike the Linux report: this one builds the container the way the client
/// does — including the media layer, whose codec ordering the video probe needs — and a platform project may not see
/// the media layer. See <c>SelfCheck.cs</c> for the map of where each report lives and why.
/// </para>
/// </remarks>
internal static partial class SelfCheck
{
    /// <summary>The platform ports, in the order the platform layer registers them.</summary>

    private static readonly (string Name, Type Port)[] Ports =
    [
        ("secrets", typeof(ISecretStore)),
        ("displays", typeof(IDisplayEnumerator)),
        ("screen capture", typeof(IScreenCapturerFactory)),
        ("input injection", typeof(IInputInjector)),
        ("user context guard", typeof(IUserContextGuard)),
        ("privacy screen", typeof(IPrivacyScreen)),
        ("keep awake", typeof(ISystemKeepAwake)),
        ("pseudo-terminal", typeof(IPtyHost)),
        ("camera enumeration", typeof(ICameraEnumerator)),
        ("camera capture", typeof(ICameraSourceFactory)),
        ("clipboard", typeof(IClipboard)),
        ("audio capture", typeof(IAudioCapturerFactory)),
        ("audio playback", typeof(IAudioPlayerFactory)),
        ("video encoder", typeof(IVideoEncoderFactory)),
        ("video decoder", typeof(IVideoDecoderFactory)),
        ("video recording", typeof(IVideoRecorderFactory)),
        ("login item", typeof(ILoginItem)),
    ];

    /// <summary>
    /// The macOS report: the bundle the process was launched from, the container the client would build, and one
    /// live probe per port. Most probes leave nothing behind (the pasteboard probes use a scratch pasteboard and the
    /// recorder probe a temporary file); the exceptions are called out where they happen — the writable probe, the
    /// keychain round trip, and the protected-folder probe, which deliberately spends the folders' TCC prompt.
    /// </summary>
    /// <returns>Zero when everything required is present, otherwise one.</returns>
    [SupportedOSPlatform("macos")]
    private static int RunMacReport(string dataDirectory)
    {
        var exitCode = 0;

        Section("process");
        Line("platform", RuntimeInformation.OSDescription);
        Line("architecture", RuntimeInformation.ProcessArchitecture.ToString());
        Line("runtime", RuntimeInformation.FrameworkDescription);
        Line("executable", Environment.ProcessPath ?? "(unknown)");
        Line("data directory", dataDirectory);

        Section("bundle");
        var bundle = FindBundle(AppContext.BaseDirectory);
        if (bundle is null)
        {
            // Not fatal by itself, but it is fatal for everything that needs a grant: since macOS 26 a bare CLI
            // executable does not even appear in the Privacy & Security list.
            Line("bundle", "(not running from a .app — TCC grants and the keychain will refuse)");
            exitCode |= 1;
        }
        else
        {
            Line("bundle", bundle);
            Line("identifier", PlistValue(bundle, "CFBundleIdentifier") ?? "(unreadable)");
            Line("version", PlistValue(bundle, "CFBundleShortVersionString") ?? "(unreadable)");
            Line("minimum macOS", PlistValue(bundle, "LSMinimumSystemVersion") ?? "(unreadable)");
            // -dvv, not -dv: the Authority= lines (the signing identity itself) are only printed at the second
            // verbose level. At -dv this loop reported nothing but Identifier/TeamIdentifier, which is the half of
            // the story that does not tell you which certificate the TCC grants are keyed to — and the Authority=
            // branch below could never fire. Ad-hoc signatures still show up here as Signature=adhoc.
            foreach (var line in Run("/usr/bin/codesign", ["-dvv", bundle]))
            {
                if (line.StartsWith("Identifier=", StringComparison.Ordinal) ||
                    line.StartsWith("Signature=", StringComparison.Ordinal) ||
                    line.StartsWith("TeamIdentifier=", StringComparison.Ordinal) ||
                    line.StartsWith("Authority=", StringComparison.Ordinal))
                {
                    Line("signature", line);
                }
            }
        }

        Section("transport");
        Line("QuicListener", QuicListener.IsSupported ? "supported" : "NOT SUPPORTED");
        Line("QuicConnection", QuicConnection.IsSupported ? "supported" : "NOT SUPPORTED");
        if (!QuicListener.IsSupported)
        {
            Line("hint", "libmsquic.dylib must sit next to the apphost; see native/macos/fetch-libmsquic.sh");
            exitCode |= 1;
        }

        // Reported before the ports, because a missing grant explains most of what goes wrong below — and because
        // the grants are keyed to the signing identity, so this is also where an ad-hoc signature shows up.
        Section("permissions");
        foreach (var grant in new[] { TccGrant.ScreenRecording, TccGrant.Accessibility, TccGrant.Camera })
        {
            var granted = TccGrants.IsGranted(grant);
            Line(grant.ToString(), granted ? "granted" : $"NOT GRANTED — {TccGrants.SettingsAnchor(grant)}");
        }

        if (!TccGrants.IsGranted(TccGrant.ScreenRecording))
        {
            Line("hint", "capture cannot work until Screen Recording is granted to this bundle, and the grant is");
            Line(string.Empty, "only read at process start, so the app has to be restarted afterwards.");
            exitCode |= 1;
        }

        // Reported but deliberately not fatal, unlike Screen Recording above. The two grants do not have the
        // same reach: without Screen Recording there is no picture at all, whereas without Accessibility the
        // capture half still works and only input injection is dead. Failing the check here would paint a
        // perfectly usable capture build red, and the ports section below already shows the injector as
        // registered — this line is what tells the two situations apart.
        if (!TccGrants.IsGranted(TccGrant.Accessibility))
        {
            Line("hint", "input injection (keyboard and mouse) cannot work until Accessibility is granted to this");
            Line(string.Empty, "bundle; capture is unaffected.");
        }


                Section("platform ports");
        using var provider = BuildContainer(dataDirectory, out var registrationError);
        if (provider is null)
        {
            Line("container", registrationError ?? "(unknown failure)");
            return exitCode | 1;
        }

        foreach (var (name, port) in Ports)
        {
            var descriptor = provider.GetService(port) is { } instance
                ? $"registered ({instance.GetType().Name})"
                : "not registered";
            Line(name, descriptor);
        }

        Section("behaviour");
        Probe("keychain round trip", () => KeychainRoundTrip(provider), ref exitCode);
        Probe("display enumeration", () => DisplaySummary(provider), ref exitCode);
        Probe("screen capture", () => ScreenCapture(provider), ref exitCode);
        Probe("keep awake", () => KeepAwake(provider), ref exitCode);
        Probe("clipboard", Clipboard, ref exitCode);
        Probe("cameras", () => Cameras(provider), ref exitCode);
        Probe("audio", () => Audio(provider), ref exitCode);
        Probe("video round trip", () => VideoRoundTrip(provider), ref exitCode);
        Probe("video recording", () => VideoRecording(provider).GetAwaiter().GetResult(), ref exitCode);
        var loginItemState = LoginItemState(provider);
        Probe("login item", () => loginItemState, ref exitCode);
        Probe("input injection", () => InputInjection(provider), ref exitCode);
        Probe("protected folders", () => ProtectedFolders(), ref exitCode);
        Probe("shells offered", () => string.Join(", ", provider.GetRequiredService<IPtyHost>().GetAvailableShells()), ref exitCode);
        Probe("files and shells allowed", () => provider.GetRequiredService<IUserContextGuard>().GetRefusalReason() ?? "yes", ref exitCode);

        Section("result");
        Line("exit code", exitCode.ToString(CultureInfo.InvariantCulture));
        return exitCode;
    }

    /// <summary>
    /// Builds the container the way <see cref="ClientHost"/> does, so that what is reported is what the client gets.
    /// </summary>
    [SupportedOSPlatform("macos")]
    private static ServiceProvider? BuildContainer(string dataDirectory, out string? error)
    {
        error = null;
        try
        {
            var services = new ServiceCollection();
            services.AddClientPlatform(dataDirectory);

            // The media layer as well: codec selection lives above the platform ports, and the video probe cannot
            // ask an encoder factory directly without bypassing the ordering the client actually uses.
            services.AddRemoteDesktopMedia();
            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Exercises one port and reports the outcome. A port that throws when used is worth knowing about even when it
    /// resolves, which is why this is separate from the registration list above.
    /// </summary>
    private static void Probe(string name, Func<string> action, ref int exitCode)
    {
        try
        {
            Line(name, action());
        }
        catch (Exception ex)
        {
            Line(name, $"FAILED — {ex.GetType().Name}: {ex.Message}");
            exitCode |= 1;
        }
    }


    /// <summary>
    /// Writes, reads back and removes a throwaway secret. This is the check that distinguishes "the keychain works"
    /// from "the keychain answers EPERM", which is what an ad-hoc signature produces (see docs/ports/README.md,
    /// "Keychain 的 OSStatus 100001 是 EPERM").
    /// </summary>
    private static string KeychainRoundTrip(IServiceProvider provider)
    {
        var store = provider.GetRequiredService<ISecretStore>();
        var name = $"self-check-{Guid.NewGuid():N}";
        var secret = Guid.NewGuid().ToByteArray();

        try
        {
            store.Save(name, secret);
            var loaded = store.Load(name);
            if (loaded is null || !loaded.AsSpan().SequenceEqual(secret))
            {
                return "wrote a secret but read back something else";
            }

            return "ok (write, read back, delete)";
        }
        finally
        {
            store.Delete(name);
        }
    }

    private static string DisplaySummary(IServiceProvider provider)
    {
        var displays = provider.GetRequiredService<IDisplayEnumerator>().GetDisplays();
        if (displays.Count == 0)
        {
            return "no displays reported";
        }

        var described = displays.Select(d => $"{d.Bounds.Width}x{d.Bounds.Height}@{(int)d.Scale}");
        return $"{displays.Count}: {string.Join(", ", described)}";
    }

    private static string KeepAwake(IServiceProvider provider)
    {
        var keepAwake = provider.GetRequiredService<ISystemKeepAwake>();
        keepAwake.Acquire();
        keepAwake.Release();
        return "ok (acquire, release)";
    }

    /// <summary>
    /// Round-trips text and a picture through a scratch pasteboard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A registered port is not a working port, and this one has been both at once. The write path compiled, signed
    /// and loaded cleanly while a marshalling mistake made every write read its length out of the caller's stack and
    /// die inside <c>memmove</c> — the dylib was fine, the ABI declaration was not. Nothing short of writing and
    /// reading back finds that, which is why this probe exists rather than a second registration line.
    /// </para>
    /// <para>
    /// It deliberately does not use the pasteboard the client will use. A self-check that overwrote whatever the user
    /// had copied would be one nobody dares run, and reading the general pasteboard unprompted makes macOS 15 and
    /// later show its "pasted from" notice. The scratch pasteboard reaches the same helper, the same two entry points
    /// and the same UTF-8 and PNG paths, and the system releases it when this process exits, so nothing is left
    /// behind and nothing of the user's is touched.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("macos")]
    private static string Clipboard()
    {
        // A name rather than the general pasteboard, for the reasons above. It is process-scoped, so it cannot
        // collide with anything the user or another application is using.
        using var scratch = new MacClipboard("com.remotedesktop.self-check.clipboard");

        // Non-ASCII on purpose: the length that matters is the byte count, not the character count, and a pure-ASCII
        // string cannot tell the two apart.
        const string Text = "self-check — 中文 🎉";
        scratch.Set(new TextClipboardContent(Text));

        if (scratch.Get() is not TextClipboardContent text || !string.Equals(text.Text, Text, StringComparison.Ordinal))
        {
            return "wrote text but read back something else";
        }

        // Byte-for-byte, not decoded and re-compared: this is the entry point that was writing past the end of its
        // buffer, and comparing lengths would have missed it.
        var png = Convert.FromBase64String(SamplePngBase64);
        scratch.Set(new ImageClipboardContent(png));

        if (scratch.Get() is not ImageClipboardContent image || !image.Png.Span.SequenceEqual(png))
        {
            return "wrote a picture but read back different bytes";
        }

        return "ok (text and PNG round trip on a scratch pasteboard)";
    }

    /// <summary>A 4x4 RGBA PNG. Only that it survives the trip matters, not what it shows.</summary>
    /// <remarks>
    /// The same bytes the clipboard tests use. The two cannot share a constant — the tests are a separate assembly
    /// that the client does not reference — and a picture is needed here because <c>rd_clipboard_write_png</c> is a
    /// different entry point from the text one, with its own way of going wrong.
    /// </remarks>
    private const string SamplePngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAYAAACp8Z5+AAAAFUlEQVR42mP4z8DwHwwZ/oMBA+kCAA97J9n4xwmsAAAAAElFTkSuQmCC";

    /// <summary>
    /// Lists the cameras and the modes each offers.
    /// </summary>
    /// <remarks>
    /// Deliberately stops short of opening one. Opening needs the camera grant, and a self-check that turned the
    /// camera light on — or that spent the user's first prompt — would be a self-check nobody runs twice. What this
    /// proves is the part that does not need permission: that the AVFoundation calls behind the enumerator are
    /// reachable and answer, which is exactly what a wrong ABI declaration would break. The grant itself is
    /// reported in the permissions section above.
    /// </remarks>
    private static string Cameras(IServiceProvider provider)
    {
        var cameras = provider.GetRequiredService<ICameraEnumerator>().GetCameras();
        if (cameras.Count == 0)
        {
            // Not a failure: a Mac mini has no camera, and a closed lid is a normal state.
            return "none connected";
        }

        var described = cameras.Select(camera => $"{camera.Name} ({camera.Modes.Count} modes)");
        return $"{cameras.Count}: {string.Join(", ", described)}";
    }

    /// <summary>
    /// Plays a short, quiet tone and captures it back through the loopback.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Playback is exercised first and on its own, because <c>rd_audio_player_enqueue</c> takes a pointer and a
    /// length — the shape that a source-generated marshaller gets wrong silently, by filling the length with the
    /// next argument (docs/ports/README.md, "[LibraryImport] 会把 span 当裸指针传"). That failure is a crash inside <c>memmove</c>, not a wrong
    /// number, so it has to be run for real to be ruled out.
    /// </para>
    /// <para>
    /// The tone is quiet and brief because this runs on a real machine with real speakers; what matters is that the
    /// samples make the round trip, not what they sound like. A loopback that delivers nothing is reported rather
    /// than treated as a failure — ScreenCaptureKit only delivers while something is rendering, and that is a state
    /// this probe cannot fully control from here.
    /// </para>
    /// </remarks>
    private static string Audio(IServiceProvider provider)
    {
        var format = AudioFormat.Standard;
        var burst = BuildTone(format, TimeSpan.FromMilliseconds(100));
        var burstMilliseconds = burst.Length / format.Channels * 1000.0 / format.SampleRate;

        var player = provider.GetRequiredService<IAudioPlayerFactory>().Create();
        var capturer = provider.GetRequiredService<IAudioCapturerFactory>().Create();
        try
        {
            player.StartAsync(format).GetAwaiter().GetResult();

            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var first = FirstChunkAsync(capturer, limit.Token);

            // Keep the output busy until a chunk arrives or the limit runs out: the loopback only has something to
            // hand over while audio is actually rendering.
            while (!first.IsCompleted && !limit.IsCancellationRequested)
            {
                player.Enqueue(burst);
                Thread.Sleep(50);
            }

            var queued = player.Buffered;
            if (!first.Wait(TimeSpan.FromSeconds(2)) || first.Result is not { } chunk)
            {
                return $"playback ok ({queued.TotalMilliseconds:F0} ms queued), but the loopback delivered nothing";
            }

            return $"ok — played {burstMilliseconds:F0} ms bursts, loopback delivered {chunk.Length} sample(s)";
        }
        finally
        {
            capturer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            player.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Reads one chunk and stops, which is all the probe needs to know the stream is alive.</summary>
    private static async Task<ReadOnlyMemory<float>?> FirstChunkAsync(
        IAudioCapturer capturer, CancellationToken cancellationToken)
    {
        await foreach (var chunk in capturer.CaptureAsync(cancellationToken).ConfigureAwait(false))
        {
            return chunk;
        }

        return null;
    }

    private static float[] BuildTone(AudioFormat format, TimeSpan duration)
    {
        var frames = (int)(format.SampleRate * duration.TotalSeconds);
        var samples = new float[frames * format.Channels];

        // 440 Hz at a twentieth of full scale: audible enough to be a real signal, quiet enough not to startle.
        for (var frame = 0; frame < frames; frame++)
        {
            var value = (float)Math.Sin(2 * Math.PI * 440 * frame / format.SampleRate) * 0.05f;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                samples[(frame * format.Channels) + channel] = value;
            }
        }

        return samples;
    }

    /// <summary>
    /// Encodes one synthetic frame and decodes it back, checking the picture that comes out against the one that
    /// went in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only check that can tell a working codec from one whose declarations are wrong. Both halves of
    /// P4 build, register and resolve happily with a mismatched struct between them — a field in the wrong place
    /// produces pictures of the wrong height, or none at all — and only running both against each other shows it.
    /// That is exactly the failure the managed layer hit during development, which is why this probe exists rather
    /// than a line saying the encoder port resolves.
    /// </para>
    /// <para>
    /// The frame is synthetic and thrown away. Encoding it costs a few milliseconds and no display.
    /// </para>
    /// </remarks>
    private static string VideoRoundTrip(IServiceProvider provider)
    {
        var codecs = provider.GetRequiredService<VideoCodecSelector>();
        var options = new VideoEncoderOptions(VideoCodec.H264, 320, 240, 30, 6000);

        var factory = codecs.GetEncoderCandidates(options).FirstOrDefault();
        if (factory is null)
        {
            return "no H.264 encoder registered";
        }

        // A frame with real detail: a flat frame compresses to almost nothing and a decode returning garbage still
        // looks plausible, whereas noise has to survive the trip to prove the pictures are being moved.
        using var frame = new MemoryVideoFrame(320, 240, PixelFormat.Nv12, TimeSpan.Zero);
        var pixels = frame.Buffer;
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)((i * 37) & 0xFF);
        }

        using var encoder = factory.Create(options);
        var key = encoder.Encode(frame, forceKeyFrame: true);
        if (key.Count == 0)
        {
            return $"{encoder.Name} produced no picture";
        }

        using var decoder = codecs.CreateDecoder(VideoCodec.H264, 320, 240);
        using var decoded = decoder.Decode(key[0]);

        if (decoded is null)
        {
            return $"{encoder.Name} refused its own picture";
        }

        if (decoded.Width != 320 || decoded.Height != 240)
        {
            return $"{encoder.Name} round-tripped to {decoded.Width}x{decoded.Height}, expected 320x240";
        }

        var mean = 0.0;
        var samples = 0;
        var outPixels = decoded.GetPixels(out var stride);
        for (var y = 0; y < 240; y += 8)
        {
            for (var x = 0; x < 320; x += 8)
            {
                mean += outPixels[(y * stride) + x];
                samples++;
            }
        }

        mean /= samples;

        // Sampled bytes from a pseudo-random pattern average near 128. A wrong plane order or a stride bug shows up
        // here as a mean near one end of the range, which is the cheapest way to see it.
        return $"ok — {encoder.Name} → 320x240 NV12, luma mean {mean:F0} (expected near 128)";
    }

    /// <summary>
    /// Records a few synthetic frames to a temporary MP4 through the registered <see cref="IVideoRecorderFactory"/> and
    /// confirms a real file came out — a registered port is not a working one, and a recorder that resolves but writes
    /// nothing (or writes an unplayable stub) is exactly the failure this probe exists to catch.
    /// </summary>
    private static async Task<string> VideoRecording(IServiceProvider provider)
    {
        var factory = provider.GetService<IVideoRecorderFactory>();
        if (factory is null)
        {
            return "no recorder factory registered";
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"rd-selfcheck-{Guid.NewGuid():N}.mp4");
        await using var recorder = factory.Create();
        try
        {
            const int width = 320;
            const int height = 240;

            recorder.Start(tempPath, width, height, frameRate: 30, bitrateKbps: 1500);
            var frame = new byte[width * height * 4];
            for (var i = 0; i < frame.Length; i++)
            {
                frame[i] = (byte)((i * 37) & 0xFF);
            }

            for (var i = 0; i < 10; i++)
            {
                recorder.RecordFrame(frame, width * 4, width, height);
                Thread.Sleep(15);
            }

            var saved = await recorder.StopAsync();
            var info = new FileInfo(saved);
            if (!info.Exists || info.Length == 0)
            {
                return "wrote no file";
            }

            return $"ok — {info.Length} bytes at {saved}";
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    /// <summary>Reports whether this build is registered to launch at login — read-only, no side effects.</summary>
    [SupportedOSPlatform("macos")]
    private static string LoginItemState(IServiceProvider provider)
    {
        var loginItem = provider.GetService<ILoginItem>();
        if (loginItem is null)
        {
            return "not registered (no login item port)";
        }

        return loginItem.IsRegistered ? "registered" : "not registered";
    }

    /// <summary>
    /// Posts one real pointer event and checks that the cursor followed it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the check that separates a working injector from one that drops everything: without the Accessibility
    /// grant <c>CGEventPost</c> returns success and does nothing, so a registration check (or a mapping-level test)
    /// would report a healthy port that silently never delivers an event. Only a posted-and-measured move can tell
    /// those apart, which is why the P3 notes left this one for a machine with the grant given.
    /// </para>
    /// <para>
    /// The move is 120 points to the right of wherever the cursor is, or to the left when that would run past the
    /// right edge, so the target is unmistakable on screen and stays on the display on any arrangement. The event
    /// goes to the display the cursor is on rather than to a fixed one, so a second monitor does not yank it away.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("macos")]
    private static string InputInjection(IServiceProvider provider)
    {
        if (!CGEventInputInjector.HasAccessibilityPermission)
        {
            // No prompt is spent on purpose — the camera probe keeps the same rule. The permissions section above
            // says where to grant it, and this line is what a run before granting looks like.
            return "skipped — Accessibility is not granted (see the permissions section above)";
        }

        if (MacDiagnostics.IsDisplayAsleep())
        {
            return "skipped — the display is asleep, so no cursor movement could be observed";
        }

        var displays = provider.GetRequiredService<IDisplayEnumerator>().GetDisplays();
        var before = MacDiagnostics.CurrentCursorLocation();
        if (displays.Count == 0 || before is null)
        {
            return "nothing to measure against (no display, or the cursor position is unreadable)";
        }

        // The display the cursor is actually on, so the event and the measurement agree about coordinates.
        var display = displays.FirstOrDefault(d =>
                before.Value.X >= d.Bounds.X && before.Value.X < d.Bounds.X + d.Bounds.Width &&
                before.Value.Y >= d.Bounds.Y && before.Value.Y < d.Bounds.Y + d.Bounds.Height)
            ?? displays.FirstOrDefault(d => d.IsPrimary)
            ?? displays[0];

        var targetX = before.Value.X + 120;
        if (targetX > display.Bounds.X + display.Bounds.Width - 8)
        {
            targetX = before.Value.X - 120;
        }

        // The wire carries normalized coordinates, so the target is expressed the way the injector itself maps them
        // back to a point (PointerCoordinates.FromNormalized): bounds.X + x * (width - 1). Inverting that keeps this
        // probe honest on a Retina display, where a pixel is not a point.
        var x = Math.Clamp((targetX - display.Bounds.X) / Math.Max(display.Bounds.Width - 1, 1), 0, 1);
        var y = Math.Clamp((before.Value.Y - display.Bounds.Y) / Math.Max(display.Bounds.Height - 1, 1), 0, 1);
        provider.GetRequiredService<IInputInjector>().Inject(new PointerMoveEvent(display.Id, x, y));

        // The window server processes the event off this thread; reading back immediately would race it.
        Thread.Sleep(150);

        var after = MacDiagnostics.CurrentCursorLocation();
        return after is not null && Math.Abs(after.Value.X - targetX) <= 4
            ? $"ok — the cursor followed the injected event (x {before.Value.X:F0} → {after.Value.X:F0})"
            : $"FAILED — the event was posted but the cursor did not follow (x {before.Value.X:F0} → {(after is null ? "unreadable" : $"{after.Value.X:F0}")})";
    }

    /// <summary>
    /// Lists the three folders macOS protects from other applications, through the very call the host answers a
    /// controller's browse with — not a raw directory enumeration, so the answer is what the other side receives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first access is also the moment macOS asks the question, and that is the behaviour the P7 notes deferred
    /// to a real machine. Both outcomes are a correct result: after Allow the folders list, and after Don't Allow the
    /// controller sees <see cref="FileError.AccessDenied"/> with a sentence it can show — no crash, no dropped
    /// channel, no hang. What would be a defect is a raw <see cref="UnauthorizedAccessException"/> reaching the
    /// protocol layer, and this line is where that would first be visible.
    /// </para>
    /// <para>
    /// Unlike the camera probe this one spends the prompt, deliberately: watching the question appear and seeing
    /// what the answer does to the listing is the entire point. The prompt is per-folder, so a folder already
    /// answered does not ask again.
    /// </para>
    /// <para>
    /// A denied folder also stops resolving: <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>
    /// answers with an empty string rather than with a path it cannot read. That is the denial signature, but it
    /// says nothing about a controller that names the well-known path anyway, so the literal spelling is
    /// enumerated as a fallback and the report says which one produced the answer.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("macos")]
    private static string ProtectedFolders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var operations = new LocalFileOperations(FileAccessPolicy.Unrestricted);

        // Desktop and Documents have a SpecialFolder each, which follows a folder the user has moved somewhere;
        // ~/Downloads has none, so it is always spelled out.
        var results = new List<string>();
        foreach (var (label, name, special) in new[]
        {
            ("Desktop", "Desktop", Environment.SpecialFolder.Desktop),
            ("Documents", "Documents", Environment.SpecialFolder.MyDocuments),
            ("Downloads", "Downloads", (Environment.SpecialFolder?)null),
        })
        {
            // Only a folder that has a SpecialFolder can be denied: ~/Downloads never resolves through one, so an
            // empty answer there is the normal case and must not be reported as a denial.
            var resolved = special is { } folder ? Environment.GetFolderPath(folder) : string.Empty;
            var denied = special is not null && resolved.Length == 0;
            var path = resolved.Length > 0 ? resolved : Path.Combine(home, name);

            string outcome;
            try
            {
                outcome = $"ok ({operations.List(path).Entries.Count} entries)";
            }
            catch (FileOperationException ex)
            {
                // Reported rather than failed: a folder the user chose not to share is a choice, not a defect.
                outcome = $"{ex.Error} (\"{ex.Message}\")";
            }

            results.Add(denied
                ? $"{label}: {outcome} [literal path — the folder stops resolving while access is denied]"
                : $"{label}: {outcome}");
        }

        return string.Join("; ", results);
    }

    /// <summary>
    /// Captures one frame from the primary display.
    /// </summary>
    /// <remarks>
    /// This is the only check that separates a working ScreenCaptureKit from a missing Screen Recording grant, and
    /// the two are hard to tell apart from the outside: both end in "no frames". Everything up to the first frame —
    /// the dylib loading, the shareable-content lookup, the stream starting — succeeds silently either way, so
    /// running it for real is the point. The frame is then disposed rather than inspected: its pixels live in the
    /// helper's buffer, and holding one open would block the next capture.
    /// </remarks>
    private static string ScreenCapture(IServiceProvider provider)
    {
        var displays = provider.GetRequiredService<IDisplayEnumerator>().GetDisplays();
        var target = displays.FirstOrDefault(display => display.IsPrimary) ?? (displays.Count > 0 ? displays[0] : null);
        if (target is null)
        {
            return "no display to capture";
        }

        var capturer = provider.GetRequiredService<IScreenCapturerFactory>().Create(target);
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var frame = capturer.CaptureAsync(TimeSpan.FromSeconds(5), limit.Token).AsTask().GetAwaiter().GetResult();
            if (frame is null)
            {
                // Not necessarily a failure: ScreenCaptureKit reports an unchanged screen as no frame at all, which
                // is what the pipeline wants. Worth stating plainly so it is not mistaken for a broken capture.
                return "stream started, but the screen was unchanged for 5s";
            }

            var description = $"ok — {frame.Width}x{frame.Height} {frame.Format}, {frame.DirtyRects.Count} dirty rect(s)";
            frame.Dispose();
            return description;
        }
        finally
        {
            capturer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }


    /// <summary>Walks up from the executable looking for the enclosing <c>.app</c>.</summary>
    private static string? FindBundle(string baseDirectory)
    {
        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            if (directory.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(Path.Combine(directory.FullName, "Contents")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string? PlistValue(string bundle, string key)
    {
        var lines = Run("/usr/libexec/PlistBuddy", ["-c", $"Print :{key}", Path.Combine(bundle, "Contents", "Info.plist")]);
        return lines.FirstOrDefault();
    }

    private static string[] Run(string executable, string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return [];
        }

        // Both streams, both drained, and drained concurrently. codesign writes its report to stderr — which is
        // exactly the output this diagnostic exists to show, so reading only stdout silently dropped the signing
        // authority and team identifier, the two fields that explain why TCC is or is not working. Draining them
        // one after the other would also risk a deadlock: a child that fills the pipe nobody is reading blocks
        // forever against a parent that is waiting for it to exit.
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        var combined = output.GetAwaiter().GetResult() + Environment.NewLine + error.GetAwaiter().GetResult();
        return combined.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static void Section(string title) => Console.WriteLine($"{Environment.NewLine}== {title} ==");

    private static void Line(string name, string value) => Console.WriteLine($"   {name,-24} {value}");
}
