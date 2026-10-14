using Avalonia;
using Microsoft.Extensions.Hosting;

namespace RemoteDesktop.Client;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The two platforms the product is built for. Said out loud rather than left to fail somewhere further in,
        // and it is also what tells the analyzer that everything below runs where the platform layers are supported.
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine($"This build runs on Windows and Linux; this is {Environment.OSVersion.Platform}.");
            return 1;
        }

        // Before anything is built, so that it still answers on a machine where the program cannot start — which is
        // the machine somebody runs it on.
        if (args.Any(argument => string.Equals(argument, "--self-check", StringComparison.OrdinalIgnoreCase)))
        {
            return SelfCheck.Run(Console.Out);
        }

        IHost host;
        try
        {
            host = ClientHost.Build(args);
        }
        catch (StartupFailure failure)
        {
            // Nothing has been created yet, so a window is not available: the system's own message box is.
            UserMessage.Show(
                $"{failure.Message}{Environment.NewLine}{Environment.NewLine}详情见：{failure.ReportPath}",
                "Remote Desktop 无法启动");
            return 1;
        }

        using var _ = host;
        host.Start();
        try
        {
            return BuildAvaloniaApp(host.Services).StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
    }

    public static AppBuilder BuildAvaloniaApp(IServiceProvider services) =>
        AppBuilder.Configure(() => new App(services))
            .UsePlatformDetect()
            .LogToTrace();
}
