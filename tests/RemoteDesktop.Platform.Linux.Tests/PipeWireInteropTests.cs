using System.Runtime.InteropServices;
using RemoteDesktop.Platform.Linux.Interop;

namespace RemoteDesktop.Platform.Linux.Tests;

/// <summary>
/// The sizes of the structures the capture path reads out of native memory.
/// </summary>
/// <remarks>
/// <para>
/// Every number here was printed by a C compiler including the real headers, not worked out by hand. That is the
/// whole point: a struct that is four bytes too small does not throw, it reads the neighbouring field as part of
/// this one, and the result is a frame that is subtly wrong or a crash somewhere else entirely. Pinning the sizes
/// turns "somebody edited a struct" into a failing test.
/// </para>
/// <para>
/// The values come from <c>spa/buffer/buffer.h</c>, <c>spa/buffer/meta.h</c> and <c>pipewire/stream.h</c> on
/// PipeWire 1.6.2. A future version that changes one of these layouts is exactly the case worth failing on, because
/// the failure would otherwise be a picture with the wrong stride.
/// </para>
/// </remarks>
public sealed class PipeWireInteropTests
{
    private const string LinuxOnly = "PipeWire, which only exists on Linux.";

    [Fact]
    public void The_buffer_structures_are_the_size_the_headers_say()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        Assert.Equal(16, Marshal.SizeOf<PipeWire.SpaChunk>());
        Assert.Equal(40, Marshal.SizeOf<PipeWire.SpaData>());
        Assert.Equal(24, Marshal.SizeOf<PipeWire.SpaBuffer>());
        Assert.Equal(40, Marshal.SizeOf<PipeWire.PwBuffer>());
    }

    [Fact]
    public void The_metadata_structures_are_the_size_the_headers_say()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        Assert.Equal(16, Marshal.SizeOf<PipeWire.SpaMeta>());
        Assert.Equal(28, Marshal.SizeOf<PipeWire.SpaMetaCursor>());
        Assert.Equal(20, Marshal.SizeOf<PipeWire.SpaMetaBitmap>());
    }

    [Fact]
    public void The_callbacks_are_the_size_the_headers_say()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // A version word, four bytes of padding to align the pointers, then eleven of them.
        Assert.Equal(96, Marshal.SizeOf<PipeWire.PwStreamEvents>());
    }

    [Fact]
    public void The_fields_the_capture_path_reads_are_where_the_headers_put_them()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // These offsets are the ones that would be silently wrong. Reading the pixels from anywhere but 24 gives a
        // pointer into the middle of the structure; reading the chunk from anywhere but 32 gives a stride and a size
        // that belong to something else, and a frame built from those is the kind of wrong that looks like a driver
        // bug.
        Assert.Equal(24, Marshal.OffsetOf<PipeWire.SpaData>(nameof(PipeWire.SpaData.Data)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<PipeWire.SpaData>(nameof(PipeWire.SpaData.Chunk)).ToInt32());
        Assert.Equal(0, Marshal.OffsetOf<PipeWire.SpaData>(nameof(PipeWire.SpaData.Type)).ToInt32());

        Assert.Equal(8, Marshal.OffsetOf<PipeWire.SpaMeta>(nameof(PipeWire.SpaMeta.Data)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<PipeWire.SpaMetaCursor>(nameof(PipeWire.SpaMetaCursor.BitmapOffset)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<PipeWire.SpaMetaBitmap>(nameof(PipeWire.SpaMetaBitmap.Offset)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<PipeWire.SpaMetaBitmap>(nameof(PipeWire.SpaMetaBitmap.Width)).ToInt32());
    }

    [Fact]
    public void The_cursor_metadata_is_four_int32s_around_a_cursor_id()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // The compositor writes this structure into a buffer we own, so the field order is not a matter of taste: a
        // position read from the hotspot's offset puts the pointer somewhere plausible and wrong.
        Assert.Equal(0, Marshal.OffsetOf<PipeWire.SpaMetaCursor>(nameof(PipeWire.SpaMetaCursor.Id)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<PipeWire.SpaMetaCursor>(nameof(PipeWire.SpaMetaCursor.PositionX)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<PipeWire.SpaMetaCursor>(nameof(PipeWire.SpaMetaCursor.HotspotX)).ToInt32());
    }
}
