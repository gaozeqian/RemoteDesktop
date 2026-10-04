using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Agent;

namespace RemoteDesktop.Platform.Windows.Agent;

/// <summary>Finds out which desktop currently receives keyboard and mouse input.</summary>
[SupportedOSPlatform("windows")]
public static unsafe partial class InputDesktop
{
    private const uint DesktopReadObjects = 0x0001;
    private const int UserObjectName = 2;

    /// <summary>
    /// <see cref="DesktopKind.User"/> while the ordinary <c>Default</c> desktop has the input, otherwise
    /// <see cref="DesktopKind.Secure"/> (login screen, lock screen, UAC prompt, screen saver). A process without the
    /// right to look at the input desktop cannot be sitting on it either, so a failure counts as secure.
    /// </summary>
    public static DesktopKind GetCurrent()
    {
        var desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == 0)
        {
            return DesktopKind.Secure;
        }

        try
        {
            char* buffer = stackalloc char[128];
            if (!GetUserObjectInformation(desktop, UserObjectName, buffer, 128 * sizeof(char), out var needed))
            {
                return DesktopKind.Secure;
            }

            var name = new ReadOnlySpan<char>(buffer, 128);

            var length = Math.Min((int)needed / sizeof(char), name.Length);
            return name[..length].TrimEnd('\0').Equals("Default", StringComparison.OrdinalIgnoreCase) ? DesktopKind.User : DesktopKind.Secure;
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(nint desktop);

    [LibraryImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetUserObjectInformation(nint handle, int index, char* information, uint length, out uint lengthNeeded);
}
