using RemoteDesktop.Features.FileTransfer;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>The free-space check has to ask the volume the destination lives on, not the root of the path.</summary>
public sealed class PartialFileVolumeTests
{
    [Fact]
    public void The_longest_matching_mount_point_is_the_volume_on_unix()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Posix path syntax.");
        string[] mounts = ["/", "/media/usb", "/home", "/media", "/media/usb2"];

        Assert.Equal("/media/usb", PartialFile.VolumeRootFor("/media/usb/photos", mounts, StringComparison.Ordinal));
        Assert.Equal("/media/usb", PartialFile.VolumeRootFor("/media/usb", mounts, StringComparison.Ordinal));
        Assert.Equal("/media/usb2", PartialFile.VolumeRootFor("/media/usb2/x", mounts, StringComparison.Ordinal));
        Assert.Equal("/home", PartialFile.VolumeRootFor("/home/me/Downloads", mounts, StringComparison.Ordinal));
        Assert.Equal("/", PartialFile.VolumeRootFor("/var/tmp", mounts, StringComparison.Ordinal));
        Assert.Equal("/media", PartialFile.VolumeRootFor("/media/usbstick/x", mounts, StringComparison.Ordinal)); // not a prefix of "usb"
        Assert.Null(PartialFile.VolumeRootFor("/var/tmp", ["/home"], StringComparison.Ordinal));
    }

    [Fact]
    public void The_drive_letter_is_the_volume_on_windows_ignoring_case()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows path syntax.");
        string[] drives = [@"C:\", @"D:\", @"D:\mounted\"];

        Assert.Equal(@"C:\", PartialFile.VolumeRootFor(@"c:\Users\me", drives, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(@"D:\", PartialFile.VolumeRootFor(@"D:\", drives, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(@"D:\mounted\", PartialFile.VolumeRootFor(@"d:\Mounted\x", drives, StringComparison.OrdinalIgnoreCase));
        Assert.Null(PartialFile.VolumeRootFor(@"E:\x", drives, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_real_folder_has_room_for_nothing_and_not_for_an_absurd_size()
    {
        var folder = Directory.CreateTempSubdirectory("rd-room-").FullName;
        try
        {
            Assert.True(PartialFile.HasRoomFor(folder, 0));
            Assert.True(PartialFile.HasRoomFor(folder, 1));
            Assert.False(PartialFile.HasRoomFor(folder, long.MaxValue));
        }
        finally
        {
            Directory.Delete(folder);
        }
    }
}
