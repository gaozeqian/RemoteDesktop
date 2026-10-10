using RemoteDesktop.Client.Setup;
using RemoteDesktop.Platform.Windows.Setup;

namespace RemoteDesktop.Platform.Windows.Tests;

/// <summary>
/// The elevated step behind "服务器地址": the client asks the installed service executable to write the address and
/// restart itself. What is checked here is the shape of that call — the executable is started directly, and the
/// address travels as an argument of its own, so there is no command line string for it to break out of.
/// </summary>
public sealed class HostServiceSetupTests
{
    private const string ServicePath = @"C:\Program Files\RemoteDesktop\RemoteDesktop.HostService.exe";

    private static Uri Address(string text)
    {
        Assert.True(SystemSetup.TryParseRendezvousUri(text, out var parsed));
        return parsed;
    }

    [Fact]
    public void The_service_executable_is_started_directly_and_elevated()
    {
        var startInfo = HostServiceSetup.BuildStartInfo(ServicePath, Address("ws://rd.example.com:5080/ws"));

        Assert.Equal(ServicePath, startInfo.FileName);
        Assert.DoesNotContain("cmd", startInfo.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("powershell", startInfo.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal("runas", startInfo.Verb);
    }

    [Fact]
    public void The_address_is_one_argument_of_its_own()
    {
        var startInfo = HostServiceSetup.BuildStartInfo(ServicePath, Address("ws://rd.example.com:5080/ws"));

        string[] expected = ["--configure-system", "--rendezvous", "ws://rd.example.com:5080/ws", "--restart-service"];
        Assert.Equal(expected, startInfo.ArgumentList);
    }

    [Fact]
    public void What_is_handed_over_is_the_parsed_address_and_not_the_typed_text()
    {
        var startInfo = HostServiceSetup.BuildStartInfo(ServicePath, Address("  WS://RD.example.com:5080/ws  "));

        Assert.Equal("ws://rd.example.com:5080/ws", startInfo.ArgumentList[2]);
    }
}
