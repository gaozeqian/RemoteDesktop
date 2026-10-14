using RemoteDesktop.Platform.Posix;

namespace RemoteDesktop.Platform.Linux.Tests;

/// <summary>
/// The file-backed secret store. The permissions are the whole protection, so they are what gets asserted.
/// </summary>
public sealed class SecretStoreTests : IDisposable
{
    private const string LinuxOnly = "Posix file permissions.";

    private readonly string _root = Directory.CreateTempSubdirectory("rd-secrets-").FullName;
    private readonly FileSecretStore _store;

    public SecretStoreTests() => _store = new FileSecretStore(Path.Combine(_root, "secrets"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_secret_survives_being_saved_and_loaded()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        _store.Save("device-identity", [1, 2, 3, 4]);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, _store.Load("device-identity"));
    }

    [Fact]
    public void A_secret_that_was_never_saved_is_null_rather_than_an_error()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        Assert.Null(_store.Load("never-written"));
    }

    [Fact]
    public void Deleting_a_secret_that_is_not_there_is_not_an_error()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        _store.Save("gone", [9]);
        _store.Delete("gone");

        Assert.Null(_store.Load("gone"));
        _store.Delete("gone"); // again, for a store that is already empty
    }

    [Fact]
    public void Only_the_owner_may_read_the_secrets_or_enter_the_folder()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        _store.Save("device-identity", [1, 2, 3]);

        var directory = new DirectoryInfo(Path.Combine(_root, "secrets"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, directory.UnixFileMode);

        var file = new FileInfo(Path.Combine(directory.FullName, "device-identity.bin"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, file.UnixFileMode);
    }

    [Fact]
    public void Saving_over_a_secret_leaves_no_half_written_file_behind()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        _store.Save("device-identity", [1, 2, 3]);
        _store.Save("device-identity", [4, 5, 6]);

        Assert.Equal(new byte[] { 4, 5, 6 }, _store.Load("device-identity"));
        // The replacement goes through a temporary file so that an interrupted write cannot leave a file that is
        // neither the old secret nor the new one — for the device identity that would be a machine that comes back as
        // a different device and can no longer be reached.
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "secrets"), "*.tmp"));
    }

    [Fact]
    public void The_folder_is_created_on_first_use_only()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var missing = Path.Combine(_root, "secrets");
        Assert.False(Directory.Exists(missing));

        _store.Save("first", [1]);
        Assert.True(Directory.Exists(missing));

        // A second save must not fail because the folder is already there.
        _store.Save("second", [2]);
        Assert.Equal(new byte[] { 2 }, _store.Load("second"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("a/b")]
    public void A_name_that_could_escape_the_folder_is_refused(string name)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        Assert.Throws<ArgumentException>(() => _store.Save(name, [1]));
    }
}
