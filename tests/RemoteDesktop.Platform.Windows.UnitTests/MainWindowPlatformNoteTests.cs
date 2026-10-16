using RemoteDesktop.Client.ViewModels;

namespace RemoteDesktop.Platform.Windows.UnitTests;

/// <summary>
/// The platform sentences in 高级设置 must describe the platform they are read on. Each is a static property
/// chosen by <c>OperatingSystem.Is*()</c>, so a build shows exactly one branch and no single-platform run can see
/// the others — which is how Linux came to read the macOS wording in all three
/// (docs/reviews/ui-consistency-review-2026-10-17.md, U1–U3): the branch was "Windows or not". Every CI job runs
/// this suite on its own platform, so the assertions below cover each branch where it actually runs.
/// </summary>
public class MainWindowPlatformNoteTests
{
    [Fact]
    public void Platform_notes_carry_no_other_platforms_wording()
    {
        if (OperatingSystem.IsLinux())
        {
            // An owner-only file rather than the macOS keychain, a service to restart rather than one process,
            // two device codes rather than one. All three sentences used to say macOS here.
            Assert.DoesNotContain("钥匙串", MainWindowViewModel.FixedPasswordStorageNote);
            Assert.DoesNotContain("macOS", MainWindowViewModel.FixedPasswordStorageNote);
            Assert.DoesNotContain("macOS", MainWindowViewModel.ServerAddressNote);
            Assert.DoesNotContain("macOS", MainWindowViewModel.ClientCodeNote);
        }
        else if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain("钥匙串", MainWindowViewModel.FixedPasswordStorageNote);
            Assert.DoesNotContain("同一个进程", MainWindowViewModel.ClientCodeNote);
        }
        else if (OperatingSystem.IsMacOS())
        {
            // macOS is the single-process platform, and its sentences say so.
            Assert.Contains("钥匙串", MainWindowViewModel.FixedPasswordStorageNote);
            Assert.Contains("同一个进程", MainWindowViewModel.ClientCodeNote);
            Assert.Contains("macOS", MainWindowViewModel.ServerAddressNote);
        }
        else
        {
            Assert.Fail("This suite covers Windows, macOS and Linux; on any other system these assertions check nothing.");
        }
    }

    [Fact]
    public void The_portal_caveat_exists_only_where_it_applies()
    {
        // U4 of the same review: the portal refuses to persist the control grant, so a fixed password cannot make
        // a Linux machine unattended. The sentence belongs to Linux alone.
        var onLinux = OperatingSystem.IsLinux();

        Assert.Equal(onLinux, MainWindowViewModel.ShowFixedPasswordPlatformNote);
        Assert.Equal(onLinux, MainWindowViewModel.FixedPasswordPlatformNote is not null);
    }
}
