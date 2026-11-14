namespace RemoteDesktop.Client;

/// <summary>
/// The sentences in 高级设置 that name something only one operating system has: the secret store, the portal caveat,
/// and what "the host did not answer" means. They are presentation, not platform ports, so they stay in the client —
/// but they are chosen once, where the platform layer is chosen, instead of being re-derived beside every use.
/// </summary>
public interface IClientPlatformPresentation
{
    /// <summary>The caption under 固定密码, naming the store this platform actually uses.</summary>
    string FixedPasswordStorageNote { get; }

    /// <summary>Linux's portal caveat; null where a fixed password can make the machine unattended.</summary>
    string? FixedPasswordPlatformNote { get; }

    /// <summary>Whether <see cref="FixedPasswordPlatformNote"/> has anything to say here.</summary>
    bool ShowFixedPasswordPlatformNote { get; }

    /// <summary>The sentence for "the host did not answer", naming what is actually unreachable.</summary>
    string HostUnreachable { get; }

    /// <summary>The caption under 服务器地址, which also depends on who hosts the controlled end.</summary>
    string DescribeServerAddress(bool controlledEndInThisProcess);
}

/// <summary>Picks the presentation for the operating system this client is running on.</summary>
public static class ClientPlatformPresentation
{
    public static IClientPlatformPresentation ForCurrentPlatform() =>
        OperatingSystem.IsWindows() ? new WindowsPresentation()
        : OperatingSystem.IsMacOS() ? new MacOSPresentation()
        : new LinuxPresentation();

    /// <summary>
    /// The caption explaining 本机控制端代码. It branches by where the controlled end runs, not by operating system:
    /// a background service has an identity of its own, so the window's code and the home page's are two different
    /// codes; a controlled end inside this process shows the same code on both.
    /// </summary>
    public static string DescribeClientCode(bool controlledEndInThisProcess) =>
        controlledEndInThisProcess
            ? "「本机控制端代码」是这台机器连出去时用的身份。被控端在本进程内运行，所以它和主页显示的是同一个代码——别人控制本机时，他那台机器的会话里显示的就是它。"
            : "「本机控制端代码」是这台机器连出去时用的身份，和主页上那个不一样：主页显示的是后台服务（被控端）的代码。别人控制本机时，他那台机器的会话里显示的就是这个代码——被问起「谁在连我」时用它对。";

    private sealed class WindowsPresentation : IClientPlatformPresentation
    {
        public string FixedPasswordStorageNote =>
            "设置后本机可用同一密码随时接入，无需现场人员告知临时密码。密码以 Windows DPAPI 加密保存在本机。";

        public string? FixedPasswordPlatformNote => null;

        public bool ShowFixedPasswordPlatformNote => false;

        public string HostUnreachable => "无法联系后台服务";

        public string DescribeServerAddress(bool controlledEndInThisProcess) =>
            controlledEndInThisProcess
                ? "两台机器必须连同一台服务器。被控端在本进程内运行；修改地址请关闭客户端，改启动参数（--Transport:RendezvousUri）或 appsettings.json 后再启动。"
                : "两台机器必须连同一台服务器。修改配置将请求管理员权限 (UAC)，完成后将自动重启服务与本客户端以使双端同时生效。";
    }

    private sealed class MacOSPresentation : IClientPlatformPresentation
    {
        public string FixedPasswordStorageNote =>
            "设置后本机可用同一密码随时接入，无需现场人员告知临时密码。密码以 macOS 钥匙串加密保存在本机。";

        public string? FixedPasswordPlatformNote => null;

        public bool ShowFixedPasswordPlatformNote => false;

        public string HostUnreachable => "无法读取本机被控状态";

        public string DescribeServerAddress(bool controlledEndInThisProcess) =>
            "两台机器必须连同一台服务器。修改后本应用会自动重启：macOS 上客户端与被控端是同一个进程，重启后两边一起换到新地址。";
    }

    private sealed class LinuxPresentation : IClientPlatformPresentation
    {
        public string FixedPasswordStorageNote =>
            "设置后本机可用同一密码随时接入，无需现场人员告知临时密码。密码以仅本用户可读写的文件（0600）保存在本机。";

        public string? FixedPasswordPlatformNote =>
            "注意：本平台无法让控制变成无人值守——portal 不允许远程桌面会话持久授权，控制权限每次连接都要重新授权（被观看不受此限制）。要在无人值守的机器上被操作，只能用 Windows 的被控端。";

        public bool ShowFixedPasswordPlatformNote => true;

        public string HostUnreachable => "无法联系后台服务";

        public string DescribeServerAddress(bool controlledEndInThisProcess) =>
            controlledEndInThisProcess
                ? "两台机器必须连同一台服务器。修改无需管理员权限：地址写入用户配置文件后，客户端会重启并生效。"
                : "两台机器必须连同一台服务器。修改无需管理员权限：地址写入用户配置文件后，客户端与已安装的被控端服务会各自重启，两边一起换到新地址。";
    }
}
