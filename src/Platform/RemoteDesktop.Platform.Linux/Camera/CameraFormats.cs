using System.Runtime.Versioning;

namespace RemoteDesktop.Platform.Linux.Camera;

/// <summary>
/// Turning what a camera hands over into the NV12 the ports promise for cameras.
/// </summary>
/// <remarks>
/// <para>
/// Here rather than beside the other colour maths in <c>RemoteDesktop.Media</c>, and the architecture is what decides
/// it: the platform layer may not depend on <c>Media</c>, so a conversion this layer has to perform cannot live
/// there. It is also honest about who needs it — nothing else does.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal static unsafe class CameraFormats
{
    /// <summary>
    /// 4:2:2 as a camera produces it into NV12.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A rearrangement rather than a conversion, and that is the point of asking a camera for YUY2: the device hands
    /// over the luminance and chroma it read off the sensor, and both are carried through untouched. The only
    /// arithmetic is the chroma, which arrives one sample per two pixels across and has to become one per two pixels
    /// each way — so the two rows that share a 4:2:0 sample are averaged.
    /// </para>
    /// <para>
    /// <b>No colour matrix is applied, and applying one would be a guess.</b> The rest of this pipeline is defined
    /// over BT.709; a webcam almost certainly means BT.601, which is what its own specification says and what
    /// everything that displays it without asking assumes. Rewriting the numbers here would be converting from a
    /// matrix that was assumed rather than known, on frames nobody has looked at — and the result of getting it
    /// wrong is a picture that looks plausible and has the wrong colour, which is the worst kind. Should a camera
    /// turn out to be visibly the wrong colour, this is the place to change, and it wants a real picture off a real
    /// device to decide with.
    /// </para>
    /// </remarks>
    /// <param name="yuyv">Source: <c>Y0 U Y1 V</c> per two pixels, rows <paramref name="yuyvStride"/> bytes apart.</param>
    /// <param name="nv12">Destination: the luminance plane (<paramref name="nv12Stride"/> × height), then interleaved UV.</param>
    public static void YuyvToNv12(ReadOnlySpan<byte> yuyv, int yuyvStride, int width, int height, Span<byte> nv12, int nv12Stride)
    {
        if (width < 2 || height < 2 || (width & 1) != 0 || (height & 1) != 0)
        {
            throw new ArgumentException("NV12 needs even dimensions of at least 2x2.");
        }

        if (yuyv.Length < ((height - 1) * yuyvStride) + (width * 2) || nv12.Length < nv12Stride * height * 3 / 2)
        {
            throw new ArgumentException("Buffer too small for the frame size.");
        }

        fixed (byte* source = yuyv)
        fixed (byte* destination = nv12)
        {
            var uvPlane = destination + (nv12Stride * height);

            for (var y = 0; y < height; y += 2)
            {
                var row0 = source + (y * yuyvStride);
                var row1 = row0 + yuyvStride;
                var luma0 = destination + (y * nv12Stride);
                var luma1 = luma0 + nv12Stride;
                var uv = uvPlane + ((y / 2) * nv12Stride);

                for (var x = 0; x < width; x += 2)
                {
                    // One group of four bytes: Y0 U Y1 V.
                    var first = row0 + (x * 2);
                    var second = row1 + (x * 2);

                    luma0[x] = first[0];
                    luma0[x + 1] = first[2];
                    luma1[x] = second[0];
                    luma1[x + 1] = second[2];

                    // Rounded rather than truncated. Always leaning towards the darker row would tint every flat
                    // surface, and a flat surface is most of what a camera is pointed at.
                    uv[x] = (byte)((first[1] + second[1] + 1) / 2);
                    uv[x + 1] = (byte)((first[3] + second[3] + 1) / 2);
                }
            }
        }
    }
}
