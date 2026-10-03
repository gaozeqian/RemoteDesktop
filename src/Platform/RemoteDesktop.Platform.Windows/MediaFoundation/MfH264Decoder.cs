using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Media;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace RemoteDesktop.Platform.Windows.MediaFoundation;

/// <summary>
/// H.264 decoding with a synchronous Media Foundation decoder (the Microsoft H.264 decoder), low-latency mode, NV12
/// output cropped to the stream size.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MfH264Decoder : IVideoDecoder
{
    private readonly IMFTransform _transform;
    private readonly IMFActivate _activate;
    private readonly int _width;
    private readonly int _height;
    private int _outputStride;
    private int _outputPlaneHeight;
    private int _outputSize;
    private long _sampleTime;
    private bool _disposed;

    public MfH264Decoder(MftDescriptor descriptor, uint enumFlags, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        Name = descriptor.Name;
        _width = width;
        _height = height;
        (_transform, _activate) = MfRuntime.Activate(MfGuids.CategoryVideoDecoder, enumFlags, MfGuids.VideoFormatH264, MfGuids.VideoFormatNv12, descriptor.Index);
        try
        {
            // One picture out per access unit in; no reordering delay.
            _transform.Attributes.Set(MfGuids.LowLatency, 1u);

            using (var input = MfRuntime.CreateVideoType(MfGuids.VideoFormatH264, width, height, 0))
            {
                _transform.SetInputType(0, input, 0);
            }

            SelectNv12Output();
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

    public VideoFrame? Decode(EncodedVideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var sample = MfRuntime.CreateSample(frame.Data.Span, _sampleTime, 0);
        _sampleTime++;
        try
        {
            _transform.ProcessInput(0, sample, 0);
        }
        catch (SharpGenException ex) when (ex.ResultCode.Code == MfGuids.ErrorNotAccepting)
        {
            // Output pending from a previous unit: drain it, then the input is accepted.
            using (Drain())
            {
            }

            _transform.ProcessInput(0, sample, 0);
        }
        catch (SharpGenException ex)
        {
            throw new InvalidDataException("The decoder rejected the bitstream.", ex);
        }

        return Drain();
    }

    // Returns the newest picture; older ones (rare in low-latency mode) are dropped.
    private MemoryVideoFrame? Drain()
    {
        MemoryVideoFrame? latest = null;
        while (true)
        {
            var buffer = new OutputDataBuffer { StreamID = 0, Sample = MfRuntime.CreateEmptySample(_outputSize) };
            var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buffer, out _);
            buffer.Events?.Dispose();
            try
            {
                if (result.Code == MfGuids.ErrorNeedMoreInput)
                {
                    return latest;
                }

                if (result.Code == MfGuids.ErrorStreamChange)
                {
                    SelectNv12Output();
                    continue;
                }

                if (result.Failure)
                {
                    latest?.Dispose();
                    throw new InvalidDataException($"Decoding failed (0x{result.Code:X8}).");
                }

                latest?.Dispose();
                latest = CopyPicture(buffer.Sample);
            }
            finally
            {
                buffer.Sample?.Dispose();
            }
        }
    }

    private unsafe MemoryVideoFrame CopyPicture(IMFSample sample)
    {
        var frame = new MemoryVideoFrame(_width, _height, PixelFormat.Nv12, TimeSpan.FromTicks(sample.SampleTime));
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var pointer, out _, out var length);
        try
        {
            if (length < _outputStride * _outputPlaneHeight * 3 / 2)
            {
                frame.Dispose();
                throw new InvalidDataException("Decoder output smaller than its media type.");
            }

            var source = new ReadOnlySpan<byte>((void*)pointer, length);
            var destination = frame.Buffer;

            // Y plane, then the interleaved UV plane, dropping the decoder's alignment padding.
            for (var y = 0; y < _height; y++)
            {
                source.Slice(y * _outputStride, _width).CopyTo(destination.Slice(y * _width, _width));
            }

            var sourceUv = source[(_outputStride * _outputPlaneHeight)..];
            var destinationUv = destination[(_width * _height)..];
            for (var y = 0; y < _height / 2; y++)
            {
                sourceUv.Slice(y * _outputStride, _width).CopyTo(destinationUv.Slice(y * _width, _width));
            }
        }
        finally
        {
            buffer.Unlock();
        }

        return frame;
    }

    private void SelectNv12Output()
    {
        for (var index = 0; ; index++)
        {
            IMFMediaType type;
            try
            {
                type = _transform.GetOutputAvailableType(0, index);
            }
            catch (SharpGenException ex)
            {
                throw new NotSupportedException("The decoder offers no NV12 output.", ex);
            }

            using (type)
            {
                if (type.GetGUID(MfGuids.Subtype) != MfGuids.VideoFormatNv12)
                {
                    continue;
                }

                _transform.SetOutputType(0, type, 0);
                MediaFactory.MFGetAttributeSize(type, MfGuids.FrameSize, out var alignedWidth, out var alignedHeight).CheckError();
                _outputStride = type.GetUInt32(MfGuids.DefaultStride, out var stride).Success && (int)stride > 0 ? (int)stride : (int)alignedWidth;
                _outputPlaneHeight = Math.Max(_height, (int)alignedHeight);
                _outputSize = Math.Max(_transform.GetOutputStreamInfo(0).Size, _outputStride * _outputPlaneHeight * 3 / 2);
                return;
            }
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

        _activate.ShutdownObject();
        _transform.Dispose();
        _activate.Dispose();
    }
}
