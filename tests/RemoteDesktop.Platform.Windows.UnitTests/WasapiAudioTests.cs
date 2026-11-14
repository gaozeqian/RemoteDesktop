using RemoteDesktop.Abstractions.Audio;
using RemoteDesktop.Platform.Windows.Audio;

namespace RemoteDesktop.Platform.Windows.UnitTests;

public class WasapiAudioTests
{
    private const string WindowsOnly = "WASAPI is Windows only.";

    [Fact]
    public void WasapiAudioCapturerFactory_creates_instance_with_standard_audio_format()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        var factory = new WasapiAudioCapturerFactory();
        var capturer = factory.Create();

        Assert.NotNull(capturer);
        Assert.Equal(AudioFormat.Standard, capturer.Format);
        Assert.Equal(48000, capturer.Format.SampleRate);
        Assert.Equal(2, capturer.Format.Channels);
    }

    [Fact]
    public void WasapiAudioPlayerFactory_creates_player_with_zero_initial_buffered_duration()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        var factory = new WasapiAudioPlayerFactory();
        var player = factory.Create();

        Assert.NotNull(player);
        Assert.Equal(TimeSpan.Zero, player.Buffered);
    }

    [Fact]
    public void WasapiAudioPlayer_Enqueue_before_Start_throws_InvalidOperationException()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        var factory = new WasapiAudioPlayerFactory();
        var player = factory.Create();

        float[] samples = [0.0f, 0.0f];
        Assert.Throws<InvalidOperationException>(() => player.Enqueue(samples));
    }

    [Fact]
    public async Task WasapiAudioPlayer_DisposeAsync_is_idempotent()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        var factory = new WasapiAudioPlayerFactory();
        var player = factory.Create();

        await player.DisposeAsync();
        await player.DisposeAsync();
    }

    [Fact]
    public async Task WasapiLoopbackAudioCapturer_DisposeAsync_completes_successfully()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        var factory = new WasapiAudioCapturerFactory();
        var capturer = factory.Create();

        await capturer.DisposeAsync();
        await capturer.DisposeAsync();
    }

    /// <summary>Disposal is final; the check runs before the default endpoint is opened.</summary>
    [Fact]
    public async Task A_disposed_capturer_refuses_a_later_capture()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        var capturer = new WasapiLoopbackAudioCapturer();
        await capturer.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await foreach (var _ in capturer.CaptureAsync(TestContext.Current.CancellationToken))
            {
            }
        });
    }
}
