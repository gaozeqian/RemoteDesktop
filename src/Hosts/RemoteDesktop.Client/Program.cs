using Avalonia;
using Microsoft.Extensions.Hosting;
using RemoteDesktop.Application.Links;

namespace RemoteDesktop.Client;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The three platforms the product is built for. Said out loud rather than left to fail somewhere further in,
        // and it is also what tells the analyzer that everything below runs where the platform layers are supported.
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine($"This build runs on Windows, macOS and Linux; this is {Environment.OSVersion.Platform}.");
            return 1;
        }

        // Reports what the platform layer can do, from inside whatever this binary was launched from. A windowed
        // program has nowhere to print, so it is a separate mode rather than a log. It runs before anything is
        // built, so that it still answers on a machine where the program cannot start — which is the machine
        // somebody runs it on.
        //
        // The environment variable is for launches that discard their arguments or their output. macOS's
        // LaunchServices is the concrete case: `open -a "Remote Desktop.app" --args …` drops the arguments
        // (macOS-port-plan.md 3.11⑧), and that is also the only launch whose TCC attribution is the app's own
        // rather than the calling terminal's — so it is the run whose answers mean anything. Set it with
        // `launchctl setenv RD_SELF_CHECK 1` so it lands in the session environment every launched app inherits,
        // and RD_SELF_CHECK_OUTPUT says where the report goes; unset both afterwards, because with the variable set
        // every launch of this build reports and exits instead of opening a window.
        if (args.Any(argument => string.Equals(argument, "--self-check", StringComparison.OrdinalIgnoreCase))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RD_SELF_CHECK")))
        {
            var reported = new List<string>(args);
            if (Environment.GetEnvironmentVariable("RD_SELF_CHECK_OUTPUT") is { Length: > 0 } reportPath)
            {
                reported.Add("--output");
                reported.Add(reportPath);
            }

            return SelfCheck.Run([.. reported]);
        }

        // A "remotedesktop://" link is not configuration: the host builder and Avalonia would both choke on it. It is
        // taken out of the arguments here and handed to the window; if another client is already running, it takes
        // the link over a pipe and this process ends before anything is built.
        var (startupLink, remaining) = SplitLink(args);
        if (startupLink is { } pending && LinkActivation.TryForward(pending))
        {
            return 0;
        }

        IHost host;
        try
        {
            host = ClientHost.Build(remaining, startupLink);
        }
        catch (StartupFailure failure)
        {
            var message = $"{failure.Message}{Environment.NewLine}{Environment.NewLine}详情见：{failure.ReportPath}";

            // Nothing has been created yet, so a window is not available: the system's own message box is. Where
            // there is no such thing, the same sentence goes to standard error instead — without it a program that
            // dies while starting up leaves an exit code and a file nobody has been told the name of.
            UserMessage.Show(message, "Remote Desktop 无法启动");
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine(message);
            }

            return 1;
        }

        using var _ = host;
        host.Start();
        try
        {
            return BuildAvaloniaApp(host.Services).StartWithClassicDesktopLifetime(remaining);
        }
        finally
        {
            host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
    }

    // Anything that starts with the scheme is ours to consume: a malformed link is dropped rather than passed on to
    // the host builder, which would refuse it as an unknown argument and leave the person with a program that does
    // not start. A valid one opens the window with the code filled in.
    internal static (ConnectionLink? Link, string[] Args) SplitLink(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith(ConnectionLink.Scheme + ":", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var link = ConnectionLink.TryParse(args[index], out var parsed) ? parsed : (ConnectionLink?)null;
            return (link, [.. args[..index], .. args[(index + 1)..]]);
        }

        return (null, args);
    }

    public static AppBuilder BuildAvaloniaApp(IServiceProvider services) =>
        AppBuilder.Configure(() => new App(services))
            .UsePlatformDetect()
            .LogToTrace();
}
