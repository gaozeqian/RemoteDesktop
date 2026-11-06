using RemoteDesktop.Application.Hosting;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// Where the installer-owned settings layer lives. Windows needs it for the same reason macOS does: the live file
/// must be outside the files the package manager replaces (the signed bundle on macOS, the MSI's file table on
/// Windows). Linux already keeps its live file in /etc and has no extra layer.
/// </summary>
public sealed class MachineSettingsTests
{
    [Fact]
    public void Windows_keeps_the_live_file_under_program_data()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows layer is only meaningful on Windows.");

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RemoteDesktop",
            "appsettings.json");

        Assert.Equal(expected, MachineSettings.WindowsPath);
        Assert.Equal(expected, MachineSettings.PathForThisMachine());
    }

    [Fact]
    public void Macos_keeps_the_live_file_outside_the_signed_bundle()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The macOS layer is only meaningful on macOS.");

        Assert.Equal("/Library/Application Support/RemoteDesktop/appsettings.json", MachineSettings.PathForThisMachine());
    }

    [Fact]
    public void Linux_has_no_second_machine_layer()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux keeps the live file in /etc already.");

        Assert.Null(MachineSettings.PathForThisMachine());
    }
}
