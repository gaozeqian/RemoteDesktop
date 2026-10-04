using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace RemoteDesktop.Platform.Windows.Agent;

/// <summary>
/// Raises Ctrl+Alt+Del through <c>SendSAS</c>, which Windows only honours from a service and only when the
/// <c>SoftwareSASGeneration</c> policy allows services to do so (the installer enables it).
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class SecureAttention
{
    private const string PolicyKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string PolicyValue = "SoftwareSASGeneration";

    /// <summary>Services are allowed to simulate the SAS when bit 0 of the policy is set.</summary>
    public static bool IsAllowedByPolicy()
    {
        using var key = Registry.LocalMachine.OpenSubKey(PolicyKey);
        return key?.GetValue(PolicyValue) is int value && (value & 1) != 0;
    }

    public static void Send(ILogger logger)
    {
        if (!SessionProcessLauncher.IsLocalSystem)
        {
            LogNotService(logger);
            return;
        }

        if (!IsAllowedByPolicy())
        {
            LogPolicyDisabled(logger, PolicyValue);
        }

        SendSAS(false);
    }

    [LibraryImport("sas.dll")]
    private static partial void SendSAS([MarshalAs(UnmanagedType.Bool)] bool asUser);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ctrl+Alt+Del ignored: only the installed background service can raise it.")]
    private static partial void LogNotService(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ctrl+Alt+Del may be ignored by Windows: {Value} does not allow services to simulate it (the installer sets it to 1).")]
    private static partial void LogPolicyDisabled(ILogger logger, string value);
}
