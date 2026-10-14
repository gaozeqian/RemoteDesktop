namespace RemoteDesktop.Media;

/// <summary>
/// BGRA ↔ NV12 with BT.709 limited-range coefficients (16.16 fixed point). Both peers use these functions, so the
/// colour matrix is consistent regardless of what the bitstream's VUI says. Large frames are split into row bands
/// processed in parallel.
/// </summary>
public static unsafe class ColorConversion
{
    private const int BandRows = 32;

    /// <param name="bgra">Source pixels; rows are <paramref name="bgraStride"/> bytes apart.</param>
    /// <param name="nv12">Destination: Y plane (<paramref name="nv12Stride"/> × height) then UV plane.</param>
    public static void BgraToNv12(ReadOnlySpan<byte> bgra, int bgraStride, int width, int height, Span<byte> nv12, int nv12Stride)
    {
        Validate(width, height);
        if (bgra.Length < ((height - 1) * bgraStride) + (width * 4) || nv12.Length < nv12Stride * height * 3 / 2)
        {
            throw new ArgumentException("Buffer too small for the frame size.");
        }

        fixed (byte* src = bgra)
        fixed (byte* dst = nv12)
        {
            var source = (nint)src;
            var destination = (nint)dst;
            ForEachBand(height, (top, bottom) =>
                BgraToNv12Rows((byte*)source, bgraStride, width, (byte*)destination, nv12Stride, height, top, bottom));
        }
    }

    public static void Nv12ToBgra(ReadOnlySpan<byte> nv12, int nv12Stride, int width, int height, Span<byte> bgra, int bgraStride)
    {
        Validate(width, height);
        if (nv12.Length < nv12Stride * height * 3 / 2 || bgra.Length < ((height - 1) * bgraStride) + (width * 4))
        {
            throw new ArgumentException("Buffer too small for the frame size.");
        }

        fixed (byte* src = nv12)
        fixed (byte* dst = bgra)
        {
            var source = (nint)src;
            var destination = (nint)dst;
            ForEachBand(height, (top, bottom) =>
                Nv12ToBgraRows((byte*)source, nv12Stride, width, height, (byte*)destination, bgraStride, top, bottom));
        }
    }

    /// <summary>
    /// 4:2:2 as a camera produces it into the NV12 the rest of the pipeline carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This one is a rearrangement rather than a conversion, and that is the point of asking a camera for YUY2: the
    /// device hands over the luminance and chroma it read off the sensor, and both are carried through untouched.
    /// The only arithmetic is the chroma, which arrives one sample per two pixels across and has to become one per
    /// two pixels each way — so the two rows that share a 4:2:0 sample are averaged.
    /// </para>
    /// <para>
    /// <b>No matrix is applied, and one would be a guess.</b> The coefficients above are BT.709; a webcam almost
    /// certainly means BT.601, which is what its own specification says and what everything that displays it without
    /// asking assumes. Rewriting the numbers here would be converting from a matrix that was assumed rather than
    /// known, on frames nobody has looked at. Should a camera turn out to be visibly the wrong colour, this is the
    /// place, and it would want a real picture from a real device to decide with.
    /// </para>
    /// </remarks>
    /// <param name="yuyv">Source pixels: <c>Y0 U Y1 V</c> per two pixels, rows <paramref name="yuyvStride"/> bytes apart.</param>
    public static void YuyvToNv12(ReadOnlySpan<byte> yuyv, int yuyvStride, int width, int height, Span<byte> nv12, int nv12Stride)
    {
        Validate(width, height);
        if (yuyv.Length < ((height - 1) * yuyvStride) + (width * 2) || nv12.Length < nv12Stride * height * 3 / 2)
        {
            throw new ArgumentException("Buffer too small for the frame size.");
        }

        fixed (byte* src = yuyv)
        fixed (byte* dst = nv12)
        {
            var source = (nint)src;
            var destination = (nint)dst;
            ForEachBand(height, (top, bottom) =>
                YuyvToNv12Rows((byte*)source, yuyvStride, width, (byte*)destination, nv12Stride, height, top, bottom));
        }
    }

    private static void YuyvToNv12Rows(byte* yuyv, int yuyvStride, int width, byte* nv12, int stride, int height, int top, int bottom)
    {
        var uvPlane = nv12 + (stride * height);
        for (var y = top; y < bottom; y += 2)
        {
            var row0 = yuyv + (y * yuyvStride);
            var row1 = row0 + yuyvStride;
            var luma0 = nv12 + (y * stride);
            var luma1 = luma0 + stride;
            var uv = uvPlane + ((y / 2) * stride);

            for (var x = 0; x < width; x += 2)
            {
                // One group of four bytes: Y0 U Y1 V.
                var first = row0 + (x * 2);
                var second = row1 + (x * 2);

                luma0[x] = first[0];
                luma0[x + 1] = first[2];
                luma1[x] = second[0];
                luma1[x + 1] = second[2];

                // Rounded rather than truncated: a chroma average that always leaned towards the darker row would
                // tint a flat surface, and flat surfaces are most of what a camera points at.
                uv[x] = (byte)((first[1] + second[1] + 1) / 2);
                uv[x + 1] = (byte)((first[3] + second[3] + 1) / 2);
            }
        }
    }

    private static void Validate(int width, int height)
    {
        if (width < 2 || height < 2 || width % 2 != 0 || height % 2 != 0)
        {
            throw new ArgumentException("NV12 needs even dimensions of at least 2x2.");
        }
    }

    // Bands always start on an even row so each band owns whole chroma rows.
    private static void ForEachBand(int height, Action<int, int> body)
    {
        var bands = (height + BandRows - 1) / BandRows;
        if (bands <= 2)
        {
            body(0, height);
            return;
        }

        Parallel.For(0, bands, band => body(band * BandRows, Math.Min(height, (band + 1) * BandRows)));
    }

    private static void BgraToNv12Rows(byte* bgra, int bgraStride, int width, byte* nv12, int stride, int height, int top, int bottom)
    {
        var uvPlane = nv12 + (stride * height);
        for (var y = top; y < bottom; y += 2)
        {
            var row0 = bgra + (y * bgraStride);
            var row1 = row0 + bgraStride;
            var y0 = nv12 + (y * stride);
            var y1 = y0 + stride;
            var uv = uvPlane + (y / 2 * stride);

            for (var x = 0; x < width; x += 2)
            {
                var p00 = row0 + (x * 4);
                var p01 = p00 + 4;
                var p10 = row1 + (x * 4);
                var p11 = p10 + 4;

                y0[x] = Luma(p00);
                y0[x + 1] = Luma(p01);
                y1[x] = Luma(p10);
                y1[x + 1] = Luma(p11);

                // Average the 2x2 block for chroma.
                var b = p00[0] + p01[0] + p10[0] + p11[0];
                var g = p00[1] + p01[1] + p10[1] + p11[1];
                var r = p00[2] + p01[2] + p10[2] + p11[2];
                uv[x] = Clamp(128 + ((((-6597 * r) - (22187 * g) + (28784 * b)) / 4 + 32768) >> 16));
                uv[x + 1] = Clamp(128 + ((((28784 * r) - (26147 * g) - (2637 * b)) / 4 + 32768) >> 16));
            }
        }
    }

    private static byte Luma(byte* p) => (byte)(16 + (((11966 * p[2]) + (40254 * p[1]) + (4064 * p[0]) + 32768) >> 16));

    private static void Nv12ToBgraRows(byte* nv12, int stride, int width, int height, byte* bgra, int bgraStride, int top, int bottom)
    {
        var uvPlane = nv12 + (stride * height);
        for (var y = top; y < bottom; y++)
        {
            var luma = nv12 + (y * stride);
            var uv = uvPlane + (y / 2 * stride);
            var output = bgra + (y * bgraStride);
            for (var x = 0; x < width; x += 2)
            {
                var d = uv[x] - 128;
                var e = uv[x + 1] - 128;
                var red = (117489 * e) + 32768;
                var green = (-13975 * d) - (34925 * e) + 32768;
                var blue = (138438 * d) + 32768;

                for (var i = 0; i < 2; i++)
                {
                    var c = 76309 * (luma[x + i] - 16);
                    var pixel = output + ((x + i) * 4);
                    pixel[0] = Clamp((c + blue) >> 16);
                    pixel[1] = Clamp((c + green) >> 16);
                    pixel[2] = Clamp((c + red) >> 16);
                    pixel[3] = 255;
                }
            }
        }
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}
