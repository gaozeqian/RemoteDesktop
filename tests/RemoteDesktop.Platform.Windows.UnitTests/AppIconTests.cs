using System.Buffers.Binary;

namespace RemoteDesktop.Platform.Windows.UnitTests;

/// <summary>
/// The Windows icon container. The shell asks for 20 px at 125% scaling and 40 px at 250%; an .ico without those
/// entries makes it scale a neighbour, which is the case the icon review flagged. Regenerating the icon used to be
/// able to drop them without anything noticing, so the committed file is checked here.
/// </summary>
public sealed class AppIconTests
{
    private static readonly int[] ExpectedSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    [Fact]
    public void The_ico_carries_every_size_windows_asks_for()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RemoteDesktop.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "src", "Hosts", "RemoteDesktop.Client", "Assets", "icon.ico");
        var ico = File.ReadAllBytes(path);

        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(ico)); // reserved
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2))); // 1 = icon, 2 = cursor
        var count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
        var sizes = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + i * 16;
            var width = ico[entry] == 0 ? 256 : ico[entry];
            var height = ico[entry + 1] == 0 ? 256 : ico[entry + 1];
            Assert.Equal(width, height);
            Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(entry + 6)));

            var length = BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(entry + 8));
            var start = BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(entry + 12));
            Assert.True(start >= 0 && length > 0 && start + length <= ico.Length, $"The {width}px entry runs past the end of the file.");

            // Every entry is PNG compressed, like the rest of this icon; the IHDR dimensions have to agree with the
            // directory, or Windows may pick the entry and draw the wrong size.
            Assert.Equal(0x89504E47u, BinaryPrimitives.ReadUInt32BigEndian(ico.AsSpan(start)));
            Assert.Equal(width, (int)BinaryPrimitives.ReadUInt32BigEndian(ico.AsSpan(start + 16)));
            Assert.Equal(height, (int)BinaryPrimitives.ReadUInt32BigEndian(ico.AsSpan(start + 20)));
            sizes.Add(width);
        }

        foreach (var size in ExpectedSizes)
        {
            Assert.Contains(size, sizes);
        }

        Assert.Equal(sizes.Distinct().Order(), sizes);
    }
}
