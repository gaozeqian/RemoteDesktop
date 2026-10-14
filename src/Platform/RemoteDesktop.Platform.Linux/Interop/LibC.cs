using System.Runtime.InteropServices;

namespace RemoteDesktop.Platform.Linux.Interop;

/// <summary>
/// The C library entry points this platform layer needs. Hand-written bindings in the same style as the Windows
/// platform project's, rather than a package: the surface is small and a wrapper library would be a dependency to
/// keep up with for a handful of calls.
/// </summary>
internal static partial class LibC
{
    /// <summary>
    /// The effective user id, which is what the kernel checks when the process opens a file. <c>getuid</c> would
    /// answer a different question and is the wrong one here: a setuid program has both, and it is the effective one
    /// that decides what it may reach.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "geteuid")]
    internal static partial uint GetEffectiveUserId();
}
