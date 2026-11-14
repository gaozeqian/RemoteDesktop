using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Versioning;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Abstractions.Audio;
using RemoteDesktop.Abstractions.Camera;
using RemoteDesktop.Abstractions.Clipboard;
using RemoteDesktop.Abstractions.Media;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Application;
using RemoteDesktop.Application.Hosting;
using RemoteDesktop.Application.Links;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Client.Setup;
using RemoteDesktop.Client.Storage;
using RemoteDesktop.Domain;
using RemoteDesktop.Features.Camera;
using RemoteDesktop.Features.Desktop;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.Features.Terminal;
using RemoteDesktop.Media;
using RemoteDesktop.Platform.Linux.Setup;
using RemoteDesktop.Transport;
using RemoteDesktop.Transport.Rendezvous;

namespace RemoteDesktop.Client.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    [RelayCommand]
    private void OpenServerAddressEditor()
    {
        NewServerAddress = HostRendezvousUri;
        ServerAddressError = null;
        IsEditingServerAddress = true;
    }

    [RelayCommand]
    private void CancelServerAddressEditor()
    {
        NewServerAddress = string.Empty;
        ServerAddressError = null;
        IsEditingServerAddress = false;
    }

    [RelayCommand]
    private async Task SaveServerAddressAsync()
    {
        // Parsed here, before anything is started as administrator, and by the same rule the host applies when it
        // writes the setting — so a rejected address costs the user a sentence, not a UAC prompt or a restart that
        // comes back broken. The rule lives in the domain because every platform needs it: two validators would be
        // two answers to the same question, and the interesting failures are the addresses where they disagree.
        if (!RendezvousAddress.TryParse(NewServerAddress, out var rendezvous))
        {
            ServerAddressError = "地址要写成 ws://主机:端口/ws 或 wss://主机/ws";
            return;
        }

        try
        {
            // Where the address has to go depends on who is being controlled and on what the installation looks
            // like. Everywhere but Windows it is the person's own settings file — this program reads it, and on
            // Linux, where the controlled end runs as the same person in the same session, so does it. See
            // UserSettings for the whole story.
            if (OperatingSystem.IsWindows())
            {
                // Windows keeps the two halves under different accounts, so the installed service owns the machine
                // file under %ProgramData%\RemoteDesktop and only an elevated run can write it.
                if (!await ApplyOnWindowsAsync(rendezvous))
                {
                    return;
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                UserSettings.SaveRendezvousUri(DataDirectory, rendezvous);
                LinuxSetup.RestartHostServiceIfInstalled();
            }
            else if (OperatingSystem.IsMacOS())
            {
                // No service on macOS: this process is the controlled side, so the address is written down for this
                // process and the app restarts to pick it up.
                UserSettings.SaveRendezvousUri(DataDirectory, rendezvous);
            }

            IsEditingServerAddress = false;

            // Restart the client application so it picks up the new address for outbound connections.
            RestartClient();
        }
        catch (Exception ex)
        {
            ServerAddressError = $"无法应用配置：{ex.Message}";
        }
    }

    /// <summary>
    /// Hands the address to the installed service, which writes it and restarts itself.
    /// </summary>
    /// <remarks>
    /// This is the only place where something the user typed reaches a process running as administrator, which is
    /// why the address is parsed before this and passed as its own argument rather than folded into a command line.
    /// A method of its own so that the platform attribute covers every Windows-only call in it.
    /// </remarks>
    /// <returns><c>false</c> when the address was not applied, with the reason left in <see cref="ServerAddressError"/>.</returns>
    [SupportedOSPlatform("windows")]
    private async Task<bool> ApplyOnWindowsAsync(RendezvousAddress rendezvous)
    {
        var exePath = System.IO.Path.Combine(AppContext.BaseDirectory, "RemoteDesktop.HostService.exe");
        if (!System.IO.File.Exists(exePath))
        {
            ServerAddressError = "找不到后台服务程序，可能客户端未作为服务安装。";
            return false;
        }

        // The service executable is asked directly, as administrator, to write the address and restart itself.
        var startInfo = HostServiceSetup.BuildStartInfo(exePath, rendezvous.Value);

        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process != null)
        {
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                ServerAddressError = "由于权限拒绝或配置失败，无法修改信令服务器地址。";
                return false;
            }
        }

        return true;
    }

    /// <summary>Starts a fresh copy of this process and leaves, so a changed address takes effect immediately.</summary>
    private static void RestartClient()
    {
        var clientExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(clientExe))
        {
            System.Diagnostics.Process.Start(clientExe);
            Environment.Exit(0);
        }
    }

    [RelayCommand]
    private void OpenFixedPasswordEditor()
    {
        NewFixedPassword = string.Empty;
        FixedPasswordError = null;
        IsEditingFixedPassword = true;
    }

    [RelayCommand]
    private void CancelFixedPasswordEditor()
    {
        NewFixedPassword = string.Empty;
        FixedPasswordError = null;
        IsEditingFixedPassword = false;
    }

    /// <summary>
    /// The rule for a new fixed password, asked of the policy the host enforces rather than restated here.
    /// </summary>
    /// <remarks>
    /// The window used to hold its own copy of the number — 6 — while <see cref="FixedPasswordPolicy.MinimumLength"/>
    /// had been raised to 8. A 6- or 7-character password therefore passed this check, reached the host, and the
    /// refusal escaped the command onto the UI thread and killed the process. One source of truth; and a refusal
    /// that still arrives is shown, never thrown.
    /// </remarks>
    /// <returns>The error to show, or <see langword="null"/> when the password may be saved.</returns>
    public static string? ValidateFixedPassword(string? password) =>
        FixedPasswordPolicy.IsAcceptable(password)
            ? null
            : $"固定密码长度至少需要 {FixedPasswordPolicy.MinimumLength} 个字符。";

    [RelayCommand]
    private async Task SaveFixedPasswordAsync()
    {
        var password = NewFixedPassword?.Trim();
        if (ValidateFixedPassword(password) is { } validationError)
        {
            FixedPasswordError = validationError;
            return;
        }

        FixedPasswordError = null;
        try
        {
            // The host applies the same policy, but it is the authority. A build whose policy is newer than this
            // window's refuses here, and the refusal has to stay in the editor rather than end the process.
            await _hostControl.SetFixedPasswordAsync(password, CancellationToken.None);
        }
        catch (ArgumentException)
        {
            FixedPasswordError = "被控端拒绝了该固定密码。";
            return;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or UnauthorizedAccessException)
        {
            FixedPasswordError = $"{HostUnreachable}，固定密码未保存。";
            return;
        }

        IsEditingFixedPassword = false;
        NewFixedPassword = string.Empty;
        await RefreshHostStatusAsync();
    }

    [RelayCommand]
    private async Task ClearFixedPasswordAsync()
    {
        try
        {
            await _hostControl.SetFixedPasswordAsync(null, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or UnauthorizedAccessException)
        {
            FixedPasswordError = $"{HostUnreachable}，固定密码未更改。";
            return;
        }

        IsEditingFixedPassword = false;
        NewFixedPassword = string.Empty;
        FixedPasswordError = null;
        await RefreshHostStatusAsync();
    }

    /// <summary>
    /// Allows the controlled end to use its cameras.
    /// </summary>
    /// <remarks>
    /// The grant is written in the host process, not here: the portal keys it by the app id of the process that asks,
    /// and that is the host service (or this process, when it is its own host), not the window.
    /// </remarks>
    [RelayCommand]
    private Task AllowCameraAccessAsync() => SetCameraAccessAsync(allowed: true);

    [RelayCommand]
    private Task RevokeCameraAccessAsync() => SetCameraAccessAsync(allowed: false);

    private async Task SetCameraAccessAsync(bool allowed)
    {
        if (IsCameraAccessBusy)
        {
            return;
        }

        IsCameraAccessBusy = true;
        CameraAccessError = null;
        try
        {
            await _hostControl.SetCameraAccessAsync(allowed, CancellationToken.None);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            // The host's own sentence names the reason (no app id, no portal, no permission store); keep it.
            CameraAccessError = ex.Message;
            return;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or UnauthorizedAccessException)
        {
            CameraAccessError = $"{HostUnreachable}，摄像头权限未更改。";
            return;
        }
        finally
        {
            IsCameraAccessBusy = false;
        }

        await RefreshHostStatusAsync();
    }

    /// <summary>Reads the login-item state macOS reports. Called at start-up and from the status poll, because the user can also change it in System Settings → 登录项.</summary>
    private void RefreshLoginItemState()
    {
        if (_loginItem is null)
        {
            return;
        }

        IsLoginItemRegistered = _loginItem.IsRegistered;
        LoginItemStatus = IsLoginItemRegistered ? "已开启：登录时自动启动" : "未开启";
    }

    [RelayCommand]
    private void EnableLoginItem()
    {
        if (_loginItem is null)
        {
            return;
        }

        LoginItemError = null;
        try
        {
            // SMAppService only accepts a signed .app sitting in /Applications; a bare binary or an ad-hoc build is
            // refused with exactly that sentence, which is the whole troubleshooting story for this button.
            _loginItem.Register();
            RefreshLoginItemState();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            LoginItemError = ex.Message;
        }
    }

    [RelayCommand]
    private void DisableLoginItem()
    {
        if (_loginItem is null)
        {
            return;
        }

        LoginItemError = null;
        try
        {
            _loginItem.Unregister();
            RefreshLoginItemState();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            LoginItemError = ex.Message;
        }
    }

    [RelayCommand]
    private async Task RefreshPasswordAsync()
    {
        try
        {
            await _hostControl.RotateTemporaryPasswordAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or InvalidOperationException)
        {
            StatusMessage = $"{HostUnreachable}。";
        }

        await RefreshHostStatusAsync();
    }

}
