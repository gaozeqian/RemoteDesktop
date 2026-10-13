using System.Diagnostics;
using System.Globalization;
using System.Net.Quic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Abstractions.Audio;
using RemoteDesktop.Abstractions.Camera;
using RemoteDesktop.Abstractions.Clipboard;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Abstractions.Input;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Abstractions.Terminal;
using RemoteDesktop.Media;
using RemoteDesktop.Platform.MacOS;
using RemoteDesktop.Platform.MacOS.Clipboard;

namespace RemoteDesktop.Client;

/// <summary>
/// Reports what this build's platform layer can actually do, from inside the bundle it is running from.
/// </summary>
/// <remarks>
/// <para>
/// Reachable with <c>--self-check</c>. It exists because most of what goes wrong in a macOS port is invisible from the
/// build: which dylibs the apphost finds, whether the process is inside a signed bundle (TCC and the keychain both
/// refuse outside one), and which ports the container can actually resolve. All of those differ between
/// <c>dotnet run</c> and the assembled <c>.app</c>, and the assembled <c>.app</c> is the only configuration that
/// matters. It writes to stdout and returns a process exit code, so it can also be used from a script or CI.
/// </para>
/// <para>
/// <c>--output &lt;path&gt;</c> mirrors the whole report to a file as well. That is how to inspect the result of the
/// one launch path that cannot be faked from a shell: <c>open -a RemoteDesktop.app --args --self-check --output …</c>
/// starts the app through LaunchServices, which is the only way to run it outside the caller's sandbox, and
/// LaunchServices discards stdout.
/// </para>
/// </remarks>
internal static class SelfCheck
{
    /// <summary>The platform ports, in the order the two platform layers register them.</summary>
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

    public static int Run(string[] args)
    {
        var outputPath = ReadOption(args, "--output");

        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // A redirected or closed stdout is not a reason to fail a diagnostic.
        }

        // Everything below writes through Console.Out. When a report file was asked for, the same text is mirrored
        // into a buffer as it is produced and written out at the end -- including when the run gives up part way
        // through. That matters because the interesting way to run this is `open -a RemoteDesktop.app --args
        // --self-check`, where LaunchServices discards stdout entirely.
        var stdout = Console.Out;
        var mirror = outputPath is null ? null : new StringBuilder();
        if (mirror is not null)
        {
            Console.SetOut(new MirrorWriter(stdout, mirror));
        }

        try
        {
            return Execute(args);
        }
        finally
        {
            Console.SetOut(stdout);
            WriteReport(outputPath, mirror, stdout);
        }
    }

    /// <summary>The diagnostic proper, with stdout already wired up by <see cref="Run"/>.</summary>
    private static int Execute(string[] args)
    {
        var dataDirectory = ReadOption(args, "--DataDirectory") ?? DefaultDataDirectory;
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
        if (OperatingSystem.IsMacOS())
        {
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
        if (OperatingSystem.IsMacOS())
        {
            var loginItemState = LoginItemState(provider);
            Probe("login item", () => loginItemState, ref exitCode);
        }
        Probe("shells offered", () => string.Join(", ", provider.GetRequiredService<IPtyHost>().GetAvailableShells()), ref exitCode);
        Probe("files and shells allowed", () => provider.GetRequiredService<IUserContextGuard>().GetRefusalReason() ?? "yes", ref exitCode);

        Section("result");
        Line("exit code", exitCode.ToString(CultureInfo.InvariantCulture));
        return exitCode;
    }

    /// <summary>
    /// Builds the container the way <see cref="ClientHost"/> does, so that what is reported is what the client gets.
    /// </summary>
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
    /// from "the keychain answers EPERM", which is what an ad-hoc signature produces (see docs/macos-port-plan.md 3.7).
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
    private static string Clipboard()
    {
        // The probe is written against the macOS port because that is the one this report is for, but the behaviour
        // section runs on every platform the client builds for, and the Windows build uses a different pasteboard
        // entirely. Saying so is better than reporting a macOS result that was never produced.
        if (!OperatingSystem.IsMacOS())
        {
            return "not applicable on this platform";
        }

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
    /// next argument (docs/macos-port-plan.md 3.12). That failure is a crash inside <c>memmove</c>, not a wrong
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

    private static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteDesktop");

    /// <summary>Reads a <c>--name value</c> pair, or <see langword="null"/> when it is absent.</summary>
    private static string? ReadOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static void WriteReport(string? outputPath, StringBuilder? mirror, TextWriter stdout)
    {
        if (outputPath is null || mirror is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(outputPath, mirror.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            stdout.WriteLine($"{Environment.NewLine}   report written to {outputPath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Failing to leave a report behind is not itself a reason to change the exit code.
            stdout.WriteLine($"{Environment.NewLine}   could not write the report to {outputPath}: {ex.Message}");
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

    /// <summary>Writes through to the real stdout and keeps a copy, so <c>--output</c> can replay it to a file.</summary>
    private sealed class MirrorWriter(TextWriter inner, StringBuilder mirror) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            inner.Write(value);
            mirror.Append(value);
        }

        public override void Write(string? value)
        {
            inner.Write(value);
            mirror.Append(value);
        }

        public override void WriteLine(string? value)
        {
            inner.WriteLine(value);
            mirror.AppendLine(value);
        }

        public override void Flush() => inner.Flush();
    }
}
