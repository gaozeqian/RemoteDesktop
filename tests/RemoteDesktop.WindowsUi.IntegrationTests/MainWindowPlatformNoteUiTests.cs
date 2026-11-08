using RemoteDesktop.Client.ViewModels;

namespace RemoteDesktop.WindowsUi.IntegrationTests;

/// <summary>
/// Integration tests verifying that the client UI ViewModel renders platform-appropriate
/// notes, security descriptions, and unattended access warnings for all supported OS modes.
/// </summary>
public class MainWindowPlatformNoteUiTests
{
    [Fact]
    public void Client_code_wording_distinguishes_in_process_from_background_service()
    {
        var inProcess = MainWindowViewModel.DescribeClientCode(controlledEndInThisProcess: true);
        var service = MainWindowViewModel.DescribeClientCode(controlledEndInThisProcess: false);

        Assert.Contains("同一个代码", inProcess);
        Assert.DoesNotContain("后台服务", inProcess);
        Assert.Contains("后台服务", service);
        Assert.DoesNotContain("同一个代码", service);
    }

    [Fact]
    public void Server_address_wording_matches_platform_service_lifecycle_capabilities()
    {
        var inProcess = MainWindowViewModel.DescribeServerAddress(controlledEndInThisProcess: true);
        var service = MainWindowViewModel.DescribeServerAddress(controlledEndInThisProcess: false);

        if (OperatingSystem.IsWindows())
        {
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
            Assert.Contains("macOS", inProcess);
            Assert.Contains("同一个进程", inProcess);
        }
    }

    [Fact]
    public void Fixed_password_storage_note_specifies_the_backing_keystore_for_the_os()
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
    public void Portal_unattended_warning_is_displayed_exclusively_on_linux()
    {
        // The expectation is a platform constant, not a second copy of the product's IsLinux() expression.
        var onLinux = OperatingSystem.IsLinux();
        Assert.Equal(onLinux, MainWindowViewModel.ShowFixedPasswordPlatformNote);

        if (onLinux)
        {
            var note = Assert.IsType<string>(MainWindowViewModel.FixedPasswordPlatformNote);
            Assert.Contains("portal", note);
            Assert.Contains("无人值守", note);
            Assert.Contains("重新授权", note);
            Assert.Contains("Windows 的被控端", note);
            Assert.DoesNotContain("macOS", note);
        }
        else
        {
            Assert.Null(MainWindowViewModel.FixedPasswordPlatformNote);
        }

        // The two product properties must agree with each other, not merely with the OS check.
        Assert.Equal(
            MainWindowViewModel.FixedPasswordPlatformNote is not null,
            MainWindowViewModel.ShowFixedPasswordPlatformNote);
    }
}
