using System.IO.Pipes;
using System.Security.Principal;
using Microsoft.Win32;
using RemoteDesktop.Application.Hosting;

namespace RemoteDesktop.Platform.Windows.UnitTests;

/// <summary>
/// The rules behind the client's check of who owns the host-control pipe. A standard user cannot open the service
/// process at all, so the answer comes from the service control manager: its process id and the executable it was
/// configured to run. Those two facts are compared here; the interop that reads them is exercised by the last test,
/// which needs the product installed on the machine it runs on.
/// </summary>
public sealed class WindowsPipeServerIdentityTests
{
    private const string Expected = @"C:\Program Files\RemoteDesktop\RemoteDesktop.HostService.exe";
    private const uint PipePid = 7788;

    [Theory]
    [InlineData(@"""" + @"C:\Program Files\RemoteDesktop\RemoteDesktop.HostService.exe" + @"""")]
    [InlineData(@"""C:\Program Files\RemoteDesktop\RemoteDesktop.HostService.exe"" --service")]
    [InlineData(@"C:\Program Files\RemoteDesktop\RemoteDesktop.HostService.exe")]
    [InlineData(@"C:\Program Files\RemoteDesktop\RemoteDesktop.HostService.exe -k host")]
    public void A_service_registered_for_the_expected_executable_is_accepted(string commandLine)
    {
        Assert.Null(WindowsPipeServerIdentity.DescribeMismatch(PipePid, PipePid, commandLine, Expected));
    }

    [Theory]
    [InlineData(42u)]
    [InlineData(0u)]
    public void A_different_or_stopped_service_process_is_refused(uint servicePid)
    {
        var mismatch = WindowsPipeServerIdentity.DescribeMismatch(PipePid, servicePid, Expected, Expected);

        Assert.NotNull(mismatch);
        Assert.Contains($"process {PipePid}", mismatch, StringComparison.Ordinal);
        Assert.Contains($"process {servicePid}", mismatch, StringComparison.Ordinal);
    }

    [Fact]
    public void A_service_pointed_at_another_executable_is_refused()
    {
        var mismatch = WindowsPipeServerIdentity.DescribeMismatch(
            PipePid, PipePid, @"""C:\Somewhere Else\Other.exe"" --service", Expected);

        Assert.NotNull(mismatch);
        Assert.Contains(@"C:\Somewhere Else\Other.exe", mismatch, StringComparison.Ordinal);
        Assert.Contains("unexpected server process", mismatch, StringComparison.Ordinal);
    }

    [Fact]
    public void Paths_are_compared_case_insensitively_and_device_prefixes_are_ignored()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows paths.");

        Assert.Null(WindowsPipeServerIdentity.DescribeMismatch(
            PipePid,
            PipePid,
            @"c:\program files\remotedesktop\REMOTEDESKTOP.HOSTSERVICE.EXE",
            Expected));
        Assert.Null(WindowsPipeServerIdentity.DescribeMismatch(
            PipePid, PipePid, Expected, @"\\?\C:\Program Files\RemoteDesktop\RemoteDesktop.HostService.exe"));
    }

    [Fact]
    public void An_unrecognised_path_is_a_mismatch_rather_than_a_crash()
    {
        var mismatch = WindowsPipeServerIdentity.DescribeMismatch(PipePid, PipePid, "\"\0bad\u0001path\"", Expected);

        Assert.NotNull(mismatch);
    }

    [Fact]
    public void Environment_variables_in_a_service_command_line_are_expanded()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ProgramFiles is a Windows variable.");

        Assert.Equal(
            Expected,
            WindowsPipeServerIdentity.ServiceExecutable(@"%ProgramFiles%\RemoteDesktop\RemoteDesktop.HostService.exe"));
        Assert.Null(WindowsPipeServerIdentity.DescribeMismatch(
            PipePid, PipePid, @"""%ProgramFiles%\RemoteDesktop\RemoteDesktop.HostService.exe"" -k host", Expected));
    }

    /// <summary>
    /// The real thing: connect to the installed service's control pipe as this (unelevated) account and let the check
    /// through the service control manager run. It only exists where the host package is installed and running, so a
    /// runner without it says so and skips instead of pretending the path was covered.
    /// </summary>
    [Fact]
    public void The_installed_host_service_answers_the_identity_check()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows Installer services are Windows only.");

        using var service = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\" + HostControlProtocol.WindowsHostServiceName);
        Assert.SkipWhen(service is null, "The host service is not installed on this machine.");

        var commandLine = service!.GetValue("ImagePath") as string;
        Assert.SkipWhen(string.IsNullOrWhiteSpace(commandLine), "The host service has no registered executable.");

        var expected = WindowsPipeServerIdentity.ServiceExecutable(commandLine!);
        Assert.True(File.Exists(expected), $"The registered executable '{expected}' is not on disk.");

        using var pipe = new NamedPipeClientStream(
            ".",
            HostControlProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        try
        {
            pipe.Connect(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            Assert.Skip($"The host-control pipe is not reachable: {ex.Message}");
        }

        Assert.Null(Record.Exception(() => WindowsPipeServerIdentity.EnsureExpected(pipe, expected)));
    }
}
