using RemoteDesktop.Application.Hosting;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The folder the product keeps its state in. <see cref="Environment.SpecialFolder"/> answers something plausible but
/// wrong on Linux — <c>~/.config</c> for data, <c>/usr/share</c> for the machine's copy — so what it must not do is
/// hand back either of those.
/// </summary>
public sealed class DataDirectoryTests
{
    [Fact]
    public void The_folder_is_absolute_and_named_after_the_product()
    {
        var path = DataDirectory.ForUser();

        Assert.True(Path.IsPathRooted(path), $"'{path}' is not an absolute path.");
        Assert.Equal("RemoteDesktop", Path.GetFileName(path));
    }

    [Fact]
    public void The_client_and_the_controlled_end_share_one_folder_off_windows()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Posix layout.");

        // The controlled end runs as the signed-in user inside that user's session, so there is no second account
        // whose state would need keeping apart.
        Assert.Equal(DataDirectory.ForUser(), DataDirectory.ForMachine());
    }

    [Fact]
    public void Off_windows_the_folder_is_not_a_configuration_folder_nor_a_system_one()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Posix layout.");

        var path = DataDirectory.ForUser();

        Assert.DoesNotContain($"{Path.DirectorySeparatorChar}.config", path, StringComparison.Ordinal);
        Assert.False(path.StartsWith("/usr/share", StringComparison.Ordinal), $"'{path}' is a system folder.");
    }

    [Fact]
    public void The_data_home_the_desktop_advertises_is_preferred()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Posix layout.");

        var previous = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var home = Directory.CreateTempSubdirectory("rd-xdg-").FullName;
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", home);
            Assert.Equal(Path.Combine(home, "RemoteDesktop"), DataDirectory.ForUser());
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", previous);
            Directory.Delete(home, recursive: true);
        }
    }
}
