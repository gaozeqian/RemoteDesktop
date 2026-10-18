using System.Runtime.Versioning;
using RemoteDesktop.Platform.MacOS.Interop;

namespace RemoteDesktop.Platform.MacOS;

/// <summary>
/// A permission macOS makes the user grant by hand, in System Settings.
/// </summary>
/// <remarks>
/// The two capture permissions the port needs, plus the camera. The protected folders are also TCC-gated, but their
/// state cannot be read without the framework that owns them (and a folder grant has no preflight call at all), so
/// they are handled where they are used rather than guessed at here.
/// </remarks>
[SupportedOSPlatform("macos")]
public enum TccGrant
{
    /// <summary>Required before any ScreenCaptureKit stream will start. Also covers system-audio capture.</summary>
    ScreenRecording,

    /// <summary>Required before synthetic input reaches other applications.</summary>
    Accessibility,

    /// <summary>Required before an AVFoundation capture session will deliver pictures.</summary>
    Camera,
}

/// <summary>
/// Reads and requests the TCC permissions, and sends the user to the right pane to grant them.
/// </summary>
/// <remarks>
/// <para>
/// The grants are keyed to the code-signing identity, not to the bundle path. An ad-hoc signature is different on
/// every build, so macOS treats each build as a new application and either re-prompts or silently refuses — which
/// is why the packaging script insists on a stable identity. See docs/ports/README.md, "TCC 授权按签名身份记账".
/// </para>
/// <para>
/// Granting Screen Recording does not take effect for a process that is already running: macOS caches the answer at
/// start-up. The permission must therefore be requested, granted, and then the application restarted — a flow the
/// caller has to drive, which is why <see cref="Request"/> reports what happened rather than looping until it
/// succeeds.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public static class TccGrants
{
    /// <summary>Whether the permission is already granted, without prompting for it.</summary>
    public static bool IsGranted(TccGrant grant) => grant switch
    {
        TccGrant.ScreenRecording => CoreGraphics.CGPreflightScreenCaptureAccess() != 0,
        TccGrant.Accessibility => CoreGraphics.CGPreflightPostEventAccess() != 0,
        TccGrant.Camera => MacNativeInterop.rd_camera_authorization_status() == MacNativeInterop.CameraAuthorized,
        _ => throw new ArgumentOutOfRangeException(nameof(grant)),
    };

    /// <summary>
    /// Asks for the permission, showing the system prompt the first time.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the grant is given. <c>false</c> means the user declined, or dismissed the prompt, or
    /// granted it and the process has not been restarted yet — macOS does not distinguish these to the caller.
    /// </returns>
    public static bool Request(TccGrant grant) => grant switch
    {
        TccGrant.ScreenRecording => CoreGraphics.CGRequestScreenCaptureAccess() != 0,
        TccGrant.Accessibility => CoreGraphics.CGRequestPostEventAccess() != 0,
        TccGrant.Camera => MacNativeInterop.rd_camera_request_access() == MacNativeInterop.CameraAuthorized,
        _ => throw new ArgumentOutOfRangeException(nameof(grant)),
    };

    /// <summary>
    /// Opens System Settings on the pane where the grant is given.
    /// </summary>
    /// <remarks>
    /// Needed because the system prompt can be dismissed, and afterwards macOS will not ask again — the only way
    /// back is the pane. Returns <c>false</c> if the request could not be handed to the workspace at all.
    /// </remarks>
    public static bool OpenSettings(TccGrant grant) =>
        MacNativeInterop.rd_open_settings(SettingsAnchor(grant)) == MacNativeInterop.Ok;

    /// <summary>
    /// The anchor System Settings uses for the pane, e.g. <c>Privacy_ScreenCapture</c>.
    /// </summary>
    /// <remarks>
    /// These are the names the <c>x-apple.systempreferences</c> scheme expects, and they are stable across the
    /// System Settings rewrite in macOS 13 — the panes were redesigned, the anchors were not.
    /// </remarks>
    public static string SettingsAnchor(TccGrant grant) => grant switch
    {
        TccGrant.ScreenRecording => "Privacy_ScreenCapture",
        TccGrant.Accessibility => "Privacy_Accessibility",
        TccGrant.Camera => "Privacy_Camera",
        _ => throw new ArgumentOutOfRangeException(nameof(grant)),
    };

    /// <summary>
    /// A message to show the user for a permission that is not granted, naming the pane to open.
    /// </summary>
    public static string Describe(TccGrant grant) => grant switch
    {
        TccGrant.ScreenRecording =>
            "远程桌面需要「屏幕录制」权限才能采集屏幕。请在「系统设置 → 隐私与安全性 → 屏幕录制」中勾选本应用，"
            + "然后重新启动应用（macOS 只在新进程启动时读取该权限）。",
        TccGrant.Accessibility =>
            "远程桌面需要「辅助功能」权限才能把鼠标键盘事件发送给其他应用。"
            + "请在「系统设置 → 隐私与安全性 → 辅助功能」中勾选本应用。",
        // No restart here, unlike Screen Recording: AVCaptureDevice reports a changed camera decision to the
        // running process, so the next open uses it. Asking for a restart would be a step that changes nothing.
        TccGrant.Camera =>
            "远程桌面需要「摄像头」权限才能把本机摄像头画面转发给远端会话。"
            + "请在「系统设置 → 隐私与安全性 → 摄像头」中允许本应用。",
        _ => throw new ArgumentOutOfRangeException(nameof(grant)),
    };
}
