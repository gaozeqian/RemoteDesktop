using Avalonia;
using Microsoft.Extensions.Hosting;

namespace RemoteDesktop.Client;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Only Windows is supported in phase 1.");
            return 1;
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
