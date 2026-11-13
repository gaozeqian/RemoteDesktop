using RemoteDesktop.Client;
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
    private static readonly IClientPlatformPresentation Presentation =
        ClientPlatformPresentation.ForCurrentPlatform();

    [Fact]
    public void Client_code_wording_follows_the_hosting_mode()
    {
        var inProcess = ClientPlatformPresentation.DescribeClientCode(controlledEndInThisProcess: true);
        var service = ClientPlatformPresentation.DescribeClientCode(controlledEndInThisProcess: false);

        Assert.Contains("同一个代码", inProcess);
        Assert.DoesNotContain("后台服务", inProcess);
        Assert.Contains("后台服务", service);
        Assert.DoesNotContain("同一个代码", service);
    }

    [Fact]
    public void Server_address_wording_matches_what_the_platform_can_actually_do()
    {
        var inProcess = Presentation.DescribeServerAddress(controlledEndInThisProcess: true);
        var service = Presentation.DescribeServerAddress(controlledEndInThisProcess: false);

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
            Assert.DoesNotContain("钥匙串", Presentation.FixedPasswordStorageNote);
            Assert.DoesNotContain("macOS", Presentation.FixedPasswordStorageNote);
        }
        else if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain("钥匙串", Presentation.FixedPasswordStorageNote);
            Assert.Contains("DPAPI", Presentation.FixedPasswordStorageNote);
        }
        else
        {
            Assert.Contains("钥匙串", Presentation.FixedPasswordStorageNote);
        }
    }

    [Fact]
    public void The_portal_caveat_exists_only_where_it_applies()
    {
        // The portal refuses to persist the control grant,
        // so a fixed password cannot make a Linux machine unattended. The sentence belongs to Linux alone.
        var onLinux = OperatingSystem.IsLinux();

        Assert.Equal(onLinux, Presentation.ShowFixedPasswordPlatformNote);
        Assert.Equal(onLinux, Presentation.FixedPasswordPlatformNote is not null);

        // The sentence has to say what goes wrong and where the operator finds the difference. These are the words
        // the Linux pane is built from, so a rewrite that drops one of them is a change of meaning, not of style.
        if (onLinux)
        {
            var note = Assert.IsType<string>(Presentation.FixedPasswordPlatformNote);
            Assert.Contains("portal", note, StringComparison.Ordinal);
            Assert.Contains("无人值守", note, StringComparison.Ordinal);
            Assert.Contains("重新授权", note, StringComparison.Ordinal);
            Assert.Contains("Windows 的被控端", note, StringComparison.Ordinal);
            Assert.DoesNotContain("macOS", note, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(Presentation.FixedPasswordPlatformNote);
        }
    }
}
