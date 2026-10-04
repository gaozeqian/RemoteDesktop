using System.Diagnostics;
using System.Text;
using RemoteDesktop.Abstractions.Terminal;
using RemoteDesktop.Platform.Windows.Terminal;

namespace RemoteDesktop.Platform.Windows.Tests;

/// <summary>The real Windows pseudoconsole with real shells.</summary>
public class ConPtyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ConPtyHost Host = new();

    private static readonly string[] KnownShells = ["pwsh", "powershell", "cmd"];

    private static PtyStartOptions Options(string? shell, int columns = 100, int rows = 30) => new(shell, [], null, columns, rows);

    [Fact]
    public void The_usual_shells_are_offered_and_the_system_ones_exist()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ConPTY is Windows only.");
        var shells = Host.GetAvailableShells();

        Assert.Contains("cmd", shells);
        Assert.Contains("powershell", shells);
        Assert.All(shells, s => Assert.Contains(s, KnownShells));
    }

    [Fact]
    public async Task Input_reaches_the_shell_and_its_output_comes_back_with_the_exit_code()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ConPTY is Windows only.");
        await using var session = Host.Start(Options("cmd"));

        await session.Input.WriteAsync(Encoding.UTF8.GetBytes("echo hello-conpty\r\nexit 7\r\n"), Ct);
        await session.Input.FlushAsync(Ct);
        var output = await ReadToEndAsync(session, TimeSpan.FromSeconds(20));

        Assert.Contains("hello-conpty", output);
        Assert.Equal(7, await session.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task The_shell_sees_the_size_and_follows_resizes()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ConPTY is Windows only.");
        await using var session = Host.Start(Options("powershell", columns: 90, rows: 25));

        await session.Input.WriteAsync(Encoding.UTF8.GetBytes("$Host.UI.RawUI.WindowSize.Width\r\n"), Ct);
        await WaitForOutputAsync(session, "90", TimeSpan.FromSeconds(30));

        session.Resize(120, 40);
        await session.Input.WriteAsync(Encoding.UTF8.GetBytes("'w=' + $Host.UI.RawUI.WindowSize.Width + ' h=' + $Host.UI.RawUI.WindowSize.Height\r\n"), Ct);
        await WaitForOutputAsync(session, "w=120 h=40", TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Disposing_ends_the_shell_and_what_it_started()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ConPTY is Windows only.");
        var marker = $"rd-conpty-{Guid.NewGuid():N}";
        var session = Host.Start(Options("cmd"));

        // A child of the shell that would outlive it: ping with a long count, recognizable by its window-less command line.
        await session.Input.WriteAsync(Encoding.UTF8.GetBytes($"ping -n 120 127.0.0.1 -l {marker.Length + 1}\r\n"), Ct);
        await WaitForProcessAsync("PING", TimeSpan.FromSeconds(10));
        var before = Process.GetProcessesByName("PING").Length;
        Assert.True(before > 0);

        await session.DisposeAsync();

        await WaitUntilAsync(() => Process.GetProcessesByName("PING").Length < before, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task The_output_ends_when_the_shell_exits()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ConPTY is Windows only.");
        await using var session = Host.Start(Options("cmd"));
        await session.Input.WriteAsync(Encoding.UTF8.GetBytes("exit\r\n"), Ct);

        var buffer = new byte[4096];
        var read = 0;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        do
        {
            read = await session.Output.ReadAsync(buffer, Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(20), Ct);
        }
        while (read > 0 && DateTime.UtcNow < deadline);

        Assert.Equal(0, read);
    }

    [Fact]
    public void A_shell_that_is_not_offered_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ConPTY is Windows only.");
        Assert.Throws<InvalidOperationException>(() => Host.Start(Options(@"C:\Windows\System32\notepad.exe")));
    }

    private static async Task<string> ReadToEndAsync(IPtySession session, TimeSpan timeout)
    {
        var text = new StringBuilder();
        var buffer = new byte[8192];
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(timeout);
        int read;
        while ((read = await session.Output.ReadAsync(buffer, limit.Token)) > 0)
        {
            text.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        return text.ToString();
    }

    private static async Task WaitForOutputAsync(IPtySession session, string expected, TimeSpan timeout)
    {
        var text = new StringBuilder();
        var buffer = new byte[8192];
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(timeout);
        while (!text.ToString().Contains(expected, StringComparison.Ordinal))
        {
            var read = await session.Output.ReadAsync(buffer, limit.Token);
            Assert.True(read > 0, $"The output ended before '{expected}' appeared: {text}");
            text.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
    }

    private static Task WaitForProcessAsync(string name, TimeSpan timeout) =>
        WaitUntilAsync(() => Process.GetProcessesByName(name).Length > 0, timeout);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(100, Ct);
        }
    }
}
