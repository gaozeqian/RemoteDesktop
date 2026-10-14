using RemoteDesktop.Features.FileTransfer;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// Finding a person's folders on a desktop whose language is not English.
/// </summary>
/// <remarks>
/// <see cref="Environment.SpecialFolder"/> answers with a fixed English name under the home directory. On a Chinese
/// install the desktop is <c>~/桌面</c>, so the answer does not exist — the file browser offers nothing and a
/// recording is written somewhere nobody will look. The desktop's own file is what is read instead.
/// </remarks>
public sealed class XdgUserDirectoriesTests : IDisposable
{
    private const string UnixOnly = "The desktop's own folder list.";

    private readonly string _root = Directory.CreateTempSubdirectory("rd-xdg-").FullName;
    private readonly string? _previousConfigHome;

    public XdgUserDirectoriesTests()
    {
        _previousConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previousConfigHome);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void A_folder_the_desktop_names_is_the_one_that_is_offered()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), UnixOnly);

        // Written the way the settings tool writes it: quotes, and a path. Absolute paths are used here so that the
        // test touches nothing outside its own temporary folder — its earlier form wrote names into the real home
        // directory, which is a thing a test must not do even when it cleans up after itself.
        var desktop = Path.Combine(_root, "桌面");
        Directory.CreateDirectory(desktop);
        WriteUserDirs($"""
            XDG_DESKTOP_DIR="{desktop}"
            XDG_DOWNLOAD_DIR="{Path.Combine(_root, "下载")}"
            """);

        Assert.Equal(desktop, XdgUserDirectories.Desktop);

        // Named but never created: not somewhere to send anyone.
        Assert.Null(XdgUserDirectories.Downloads);
    }

    [Fact]
    public void A_folder_set_to_the_home_directory_counts_as_switched_off()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), UnixOnly);

        // This is what the settings tool writes when a person turns a folder off. Offering the whole home directory
        // as "Desktop" would be wrong in a way that looks like it had worked.
        WriteUserDirs("""XDG_DESKTOP_DIR="$HOME" """);

        Assert.Null(XdgUserDirectories.Desktop);
    }

    [Fact]
    public void A_folder_that_does_not_exist_is_not_offered()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), UnixOnly);

        WriteUserDirs($"""XDG_DOCUMENTS_DIR="{Path.Combine(_root, "nowhere-at-all")}" """);

        Assert.Null(XdgUserDirectories.Documents);
    }

    [Fact]
    public void Without_the_file_there_is_no_answer_rather_than_a_wrong_one()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), UnixOnly);

        // Nothing written. The callers fall back to what the platform offers; what must not happen is a path being
        // invented out of the English names.
        Assert.Null(XdgUserDirectories.Desktop);
        Assert.Null(XdgUserDirectories.Documents);
        Assert.Null(XdgUserDirectories.Downloads);
        Assert.Null(XdgUserDirectories.Videos);
    }

    [Fact]
    public void A_tilde_is_read_the_same_way_as_the_home_variable()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), UnixOnly);

        // The one test that has to go through the real home directory, because that is what the tilde means. The name
        // is unique to this run so that it cannot collide with a folder that was already there, and it is removed
        // again whatever happens.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var name = $"rd-xdg-test-{Guid.NewGuid():N}";
        var created = Directory.CreateDirectory(Path.Combine(home, name)).FullName;

        try
        {
            WriteUserDirs($"""XDG_VIDEOS_DIR="~/{name}" """);
            Assert.Equal(created, XdgUserDirectories.Videos);
        }
        finally
        {
            Directory.Delete(created, recursive: true);
        }
    }

    [Fact]
    public void The_recording_folder_falls_back_to_what_the_platform_offers()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), UnixOnly);

        // Nothing set, so the recording still has somewhere to go rather than failing because a folder the person
        // never chose does not exist.
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), XdgUserDirectories.RecordingDirectory());

        var videos = Path.Combine(_root, "视频");
        Directory.CreateDirectory(videos);
        WriteUserDirs($"""XDG_VIDEOS_DIR="{videos}" """);

        Assert.Equal(videos, XdgUserDirectories.RecordingDirectory());
    }

    [Fact]
    public void A_file_whose_lines_are_not_what_they_should_be_does_not_stop_the_rest_being_read()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), UnixOnly);

        var documents = Path.Combine(_root, "文档");
        Directory.CreateDirectory(documents);
        WriteUserDirs($"""
            # a comment
            XDG_DESKTOP_DIR
            XDG_DOCUMENTS_DIR="{documents}"
            not a setting at all
            """);

        Assert.Null(XdgUserDirectories.Desktop);
        Assert.Equal(documents, XdgUserDirectories.Documents);
    }

    /// <summary>
    /// Writes the desktop's file. The folders it names are created inside this test's own temporary directory,
    /// because the reader checks that they are really there.
    /// </summary>
    private void WriteUserDirs(string contents) =>
        File.WriteAllText(Path.Combine(_root, "user-dirs.dirs"), contents);
}
