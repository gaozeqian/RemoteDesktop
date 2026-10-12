using Avalonia;
using Microsoft.Extensions.Hosting;

namespace RemoteDesktop.Client;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The client is the only host on macOS: there is no service/agent split there (no Session 0), so this one
        // process is both the controller and — in standalone mode — the device others connect to.
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine("Remote Desktop runs on Windows and macOS.");
            return 1;
        }

        // Reports what the platform layer can do, from inside whatever this binary was launched from. A windowed
        // program has nowhere to print, so it is a separate mode rather than a log.
        if (args.Contains("--self-check", StringComparer.Ordinal))
        {
            return SelfCheck.Run(args);
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
