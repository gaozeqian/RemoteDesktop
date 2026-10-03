using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Media;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace RemoteDesktop.Platform.Windows.MediaFoundation;

/// <summary>
/// H.264 encoding through a Media Foundation transform, sync (Microsoft software encoder) or async (hardware
/// encoders such as NVENC / Quick Sync / AMF). Configured for low latency: CBR, no B-frames, one output per input.
/// </summary>
/// <remarks>Not thread-safe; the video pipeline drives it from a single loop.</remarks>
[SupportedOSPlatform("windows")]
internal sealed class MfH264Encoder : IVideoEncoder
{
    private static readonly TimeSpan NeedInputTimeout = TimeSpan.FromSeconds(1);

    private readonly IMFTransform _transform;
    private readonly IMFActivate _activate;
    private readonly CodecApi? _codecApi;
    private readonly MfEventPump? _events;
    private readonly bool _providesSamples;
    private readonly int _outputSize;
    private readonly long _frameDuration;
    private readonly TimeSpan _outputWait;
    private readonly byte[]? _sequenceHeader;
    private int _pendingNeedInput;
    private int _pendingOutputs;
    private long _frameIndex;
    private bool _disposed;

    public MfH264Encoder(MftDescriptor descriptor, uint enumFlags, bool isHardware, VideoEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(options);

        Name = descriptor.Name;
        IsHardware = isHardware;
        Options = options;
        _frameDuration = TimeSpan.TicksPerSecond / Math.Max(1, options.FrameRate);
        _outputWait = TimeSpan.FromMilliseconds(Math.Max(20, 2000.0 / Math.Max(1, options.FrameRate)));

        (_transform, _activate) = MfRuntime.Activate(MfGuids.CategoryVideoEncoder, enumFlags, MfGuids.VideoFormatNv12, MfGuids.VideoFormatH264, descriptor.Index);
        try
        {
            var attributes = _transform.Attributes;
            var isAsync = attributes.GetUInt32(MfGuids.TransformAsync, out var asyncValue).Success && asyncValue != 0;
            if (isAsync)
            {
                attributes.Set(MfGuids.TransformAsyncUnlock, 1u).CheckError();
            }

            attributes.Set(MfGuids.LowLatency, 1u);

            _codecApi = CodecApi.TryCreate(_transform);
            _codecApi?.SetUInt32(MfGuids.CodecApiRateControlMode, MfGuids.RateControlCbr);
            _codecApi?.SetUInt32(MfGuids.CodecApiMeanBitRate, (uint)options.BitrateKbps * 1000);
            _codecApi?.SetBool(MfGuids.LowLatency, true);
            _codecApi?.SetUInt32(MfGuids.CodecApiBPictureCount, 0);
            // Long GOP: recovery is driven by explicit key-frame requests, not by periodic I-frames.
            _codecApi?.SetUInt32(MfGuids.CodecApiGopSize, (uint)Math.Max(1, options.FrameRate) * 10);

            // Encoders want the output (compressed) type before the input type.
            using (var output = MfRuntime.CreateVideoType(MfGuids.VideoFormatH264, options.Width, options.Height, options.FrameRate))
            {
                output.Set(MfGuids.AvgBitrate, (uint)options.BitrateKbps * 1000).CheckError();
                output.Set(MfGuids.Mpeg2Profile, MfGuids.H264ProfileMain).CheckError();
                _transform.SetOutputType(0, output, 0);
            }

            using (var input = MfRuntime.CreateVideoType(MfGuids.VideoFormatNv12, options.Width, options.Height, options.FrameRate))
            {
                _transform.SetInputType(0, input, 0);
            }

            var streamInfo = _transform.GetOutputStreamInfo(0);
            _providesSamples = (streamInfo.Flags & MfGuids.OutputStreamProvidesSamples) != 0;
            _outputSize = Math.Max(streamInfo.Size, options.Width * options.Height * 3 / 2);
            _sequenceHeader = TryGetSequenceHeader();

            if (isAsync)
            {
                _events = new MfEventPump(_transform.QueryInterface<IMFMediaEventGenerator>());
            }

            _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
            _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public string Name { get; }

    public bool IsHardware { get; }

    public VideoEncoderOptions Options { get; }

    public IReadOnlyList<EncodedVideoFrame> Encode(VideoFrame frame, bool forceKeyFrame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame.Format != PixelFormat.Nv12 || frame.Width != Options.Width || frame.Height != Options.Height)
        {
            throw new ArgumentException($"Expected NV12 {Options.Width}x{Options.Height}.", nameof(frame));
        }

        if (forceKeyFrame)
        {
            _codecApi?.SetUInt32(MfGuids.CodecApiForceKeyFrame, 1);
        }

        var pixels = frame.GetPixels(out var stride);
        if (stride != frame.Width)
        {
            throw new ArgumentException("NV12 input must be tightly packed.", nameof(frame));
        }

        using var sample = MfRuntime.CreateSample(pixels[..(frame.Width * frame.Height * 3 / 2)], _frameIndex * _frameDuration, _frameDuration);
        _frameIndex++;

        var outputs = new List<EncodedVideoFrame>(1);
        if (_events is null)
        {
            _transform.ProcessInput(0, sample, 0);
            DrainSync(outputs, frame.Timestamp);
        }
        else
        {
            EncodeAsync(sample, outputs, frame.Timestamp);
        }

        return outputs;
    }

    public void SetBitrate(int bitrateKbps) => _codecApi?.SetUInt32(MfGuids.CodecApiMeanBitRate, (uint)bitrateKbps * 1000);

    private void EncodeAsync(IMFSample sample, List<EncodedVideoFrame> outputs, TimeSpan timestamp)
    {
        // Collect whatever arrived since the last call, then wait for the encoder to accept input.
        Pump(outputs, timestamp, TimeSpan.Zero, untilOutput: false);
        var deadline = DateTime.UtcNow + NeedInputTimeout;
        while (_pendingNeedInput == 0)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero || !_events!.TryTake(out var eventType, remaining))
            {
                throw new InvalidOperationException($"{Name} stopped requesting input.");
            }

            Handle(eventType, outputs, timestamp);
        }

        _pendingNeedInput--;
        _transform.ProcessInput(0, sample, 0);
        _pendingOutputs++;

        // Low-latency encoders produce the picture within a few milliseconds; wait for it so the frame leaves now.
        Pump(outputs, timestamp, _outputWait, untilOutput: true);
    }

    private void Pump(List<EncodedVideoFrame> outputs, TimeSpan timestamp, TimeSpan wait, bool untilOutput)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            if (untilOutput && _pendingOutputs == 0)
            {
                return;
            }

            var remaining = deadline - DateTime.UtcNow;
            var got = remaining > TimeSpan.Zero ? _events!.TryTake(out var eventType, remaining) : _events!.TryTake(out eventType);
            if (!got)
            {
                return;
            }

            Handle(eventType, outputs, timestamp);
        }
    }

    private void Handle(int eventType, List<EncodedVideoFrame> outputs, TimeSpan timestamp)
    {
        switch (eventType)
        {
            case MfGuids.EventTransformNeedInput:
                _pendingNeedInput++;
                break;
            case MfGuids.EventTransformHaveOutput:
                if (TryProcessOutput(timestamp, out var encoded))
                {
                    outputs.Add(encoded);
                    _pendingOutputs = Math.Max(0, _pendingOutputs - 1);
                }

                break;
            default:
                break;
        }
    }

    private void DrainSync(List<EncodedVideoFrame> outputs, TimeSpan timestamp)
    {
        while (TryProcessOutput(timestamp, out var encoded))
        {
            outputs.Add(encoded);
        }
    }

    private bool TryProcessOutput(TimeSpan timestamp, out EncodedVideoFrame encoded)
    {
        encoded = null!;
        while (true)
        {
            var buffer = new OutputDataBuffer { StreamID = 0, Sample = _providesSamples ? null! : MfRuntime.CreateEmptySample(_outputSize) };
            var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buffer, out _);
            buffer.Events?.Dispose();
            try
            {
                if (result.Code == MfGuids.ErrorNeedMoreInput)
                {
                    return false;
                }

                if (result.Code == MfGuids.ErrorStreamChange)
                {
                    // The encoder adjusted its output type; accept its first proposal and retry.
                    using var type = _transform.GetOutputAvailableType(0, 0);
                    _transform.SetOutputType(0, type, 0);
                    continue;
                }

                result.CheckError();
                var data = MfRuntime.ToArray(buffer.Sample);
                var isKeyFrame = H264Bitstream.IsKeyFrame(data);
                if (isKeyFrame && _sequenceHeader is not null && !H264Bitstream.ContainsParameterSets(data))
                {
                    // Some encoders only publish SPS/PPS on the media type; the decoder needs them in-band.
                    data = [.. _sequenceHeader, .. data];
                }

                encoded = new EncodedVideoFrame(data, isKeyFrame, timestamp);
                return true;
            }
            finally
            {
                buffer.Sample?.Dispose();
            }
        }
    }

    private byte[]? TryGetSequenceHeader()
    {
        try
        {
            using var type = _transform.GetOutputCurrentType(0);
            var header = type.GetBlob(MfGuids.MpegSequenceHeader);
            return header.Length > 0 ? header : null;
        }
        catch (SharpGenException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, 0);
            _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, 0);
        }
        catch (SharpGenException)
        {
        }

        // Shutting the transform down releases the event pump's blocking GetEvent.
        _activate.ShutdownObject();
        _events?.Dispose();
        _codecApi?.Dispose();
        _transform.Dispose();
        _activate.Dispose();
    }
}
