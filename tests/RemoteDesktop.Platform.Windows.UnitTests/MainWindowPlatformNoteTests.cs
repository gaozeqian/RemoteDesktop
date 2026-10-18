using RemoteDesktop.Client.ViewModels;

namespace RemoteDesktop.Platform.Windows.UnitTests;

/// <summary>
/// The platform and hosting sentences in 高级设置 must describe the machine they are read on. The hosting mode is
/// part of that: a Windows standalone client has no background service, so it must not say that its two codes differ
/// or promise a UAC prompt and a service restart. These assertions run on every CI platform, so each branch is
/// checked where it actually runs.
/// </summary>
public class MainWindowPlatformNoteTests
{
    [Fact]
    public void Client_code_wording_follows_the_hosting_mode()
    {
        var inProcess = MainWindowViewModel.DescribeClientCode(controlledEndInThisProcess: true);
        var service = MainWindowViewModel.DescribeClientCode(controlledEndInThisProcess: false);

        Assert.Contains("同一个代码", inProcess);
        Assert.DoesNotContain("后台服务", inProcess);
        Assert.Contains("后台服务", service);
        Assert.DoesNotContain("同一个代码", service);
    }

    [Fact]
    public void Server_address_wording_matches_what_the_platform_can_actually_do()
    {
        var inProcess = MainWindowViewModel.DescribeServerAddress(controlledEndInThisProcess: true);
        var service = MainWindowViewModel.DescribeServerAddress(controlledEndInThisProcess: false);

        if (OperatingSystem.IsWindows())
        {
            // A standalone Windows client cannot write the service's appsettings; the sentence says how to edit it
            // instead of promising a UAC prompt that would only end in "找不到后台服务程序".
            Assert.Contains("启动参数", inProcess);
            Assert.DoesNotContain("UAC", inProcess);
            Assert.Contains("UAC", service);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.Contains("客户端会重启", inProcess);
            Assert.Contains("已安装的被控端服务", service);
            Assert.DoesNotContain("macOS", inProcess);
            Assert.DoesNotContain("macOS", service);
        }
        else
        {
            // macOS has no service at all; the controlled end is always this process.
            Assert.Contains("macOS", inProcess);
            Assert.Contains("同一个进程", inProcess);
        }
    }

    [Fact]
    public void Fixed_password_storage_names_the_store_of_this_platform()
    {
        if (OperatingSystem.IsLinux())
        {
            Assert.DoesNotContain("钥匙串", MainWindowViewModel.FixedPasswordStorageNote);
            Assert.DoesNotContain("macOS", MainWindowViewModel.FixedPasswordStorageNote);
        }
        else if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain("钥匙串", MainWindowViewModel.FixedPasswordStorageNote);
            Assert.Contains("DPAPI", MainWindowViewModel.FixedPasswordStorageNote);
        }
        else
        {
            Assert.Contains("钥匙串", MainWindowViewModel.FixedPasswordStorageNote);
        }
    }

    [Fact]
    public void The_portal_caveat_exists_only_where_it_applies()
    {
        // U4 of docs/reviews/ui-consistency-review-2026-10-17.md: the portal refuses to persist the control grant,
        // so a fixed password cannot make a Linux machine unattended. The sentence belongs to Linux alone.
        var onLinux = OperatingSystem.IsLinux();

        Assert.Equal(onLinux, MainWindowViewModel.ShowFixedPasswordPlatformNote);
        Assert.Equal(onLinux, MainWindowViewModel.FixedPasswordPlatformNote is not null);
    }
}
