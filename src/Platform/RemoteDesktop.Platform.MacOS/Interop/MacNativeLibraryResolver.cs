using System.Reflection;
using System.Runtime.InteropServices;

namespace RemoteDesktop.Platform.MacOS.Interop;

/// <summary>
/// Finds <c>libRemoteDesktopMacNative.dylib</c> wherever this build happens to have put it.
/// </summary>
/// <remarks>
/// <para>
/// The library is referenced as <see cref="NativeLibraries.MacNative"/>, and the runtime's own probing would often
/// find it unaided — it looks for <c>lib&lt;name&gt;.dylib</c> beside the assembly. This exists because "often" is
/// not good enough for the one dylib the platform layer cannot work without: the failure is an
/// <see cref="DllNotFoundException"/> from deep inside a P/Invoke, which says nothing about the real cause (the
/// helper was never built, or was built for the wrong architecture, or the packaging script did not copy it).
/// Resolving it here means the search order is written down and the miss can be reported with that list.
/// </para>
/// <para>
/// The install name is <c>@rpath/libRemoteDesktopMacNative.dylib</c>, so the same file works from a build output
/// directory and from inside <c>Contents/MacOS</c> without being rebuilt — which is what makes the .app able to
/// carry it at all.
/// </para>
/// </remarks>
internal static class MacNativeLibraryResolver
{
    /// <summary>Registering twice throws, and a test host can load this assembly more than once.</summary>
    private static int _registered;

    /// <summary>
    /// Installs the resolver, once.
    /// </summary>
    /// <remarks>
    /// Called from the static constructor of the interop class that declares the P/Invokes, rather than from a
    /// module initializer: a module initializer in a library is what CA2255 warns about, and the warning is right —
    /// a library should not run code merely because it was loaded. A static constructor on the class that needs the
    /// library is the narrower promise, and the runtime guarantees it has run before any of that class's members
    /// are reached, which is exactly the ordering <see cref="NativeLibrary.SetDllImportResolver"/> requires.
    /// </remarks>
    internal static void EnsureRegistered()
    {
        // The guard keeps this a no-op off macOS, where the assembly is still compiled — it is net10.0 precisely so
        // the Windows CI job can build it.
        if (!OperatingSystem.IsMacOS() || Interlocked.Exchange(ref _registered, 1) != 0)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(MacNativeLibraryResolver).Assembly, Resolve);
    }

    /// <summary>The paths searched, in order. Exposed so a failure can name them.</summary>
    internal static IReadOnlyList<string> SearchPaths { get; } = BuildSearchPaths();

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPaths)
    {
        // Anything else is not ours to resolve; returning zero lets the runtime continue with its own probing.
        if (!string.Equals(libraryName, NativeLibraries.MacNative, StringComparison.Ordinal))
        {
            return 0;
        }

        foreach (var candidate in SearchPaths)
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        // Last resort: let the runtime try the bare name. It covers a library installed somewhere on the default
        // search path, which is how a developer who put it in /usr/local/lib would expect this to behave.
        return NativeLibrary.TryLoad(libraryName, assembly, searchPaths, out var fallback) ? fallback : 0;
    }

    private static string[] BuildSearchPaths()
    {
        var fileName = $"lib{NativeLibraries.MacNative}.dylib";
        var baseDirectory = AppContext.BaseDirectory;

        var candidates = new List<string>
        {
            // The normal case: a build output directory, and Contents/MacOS inside the bundle — both are the
            // directory the assembly itself sits in.
            Path.Combine(baseDirectory, fileName),
            // A structured bundle would put it here; the packaging script uses a flat Contents/MacOS today, and
            // this costs nothing while P10 decides whether to move to Contents/Frameworks for notarization.
            Path.Combine(baseDirectory, "..", "Frameworks", fileName),
        };

        // The build output of the native script, for a test host running out of a directory that predates the
        // copy, or a developer who built the library by hand and has not rebuilt the managed side yet.
        var repositoryRoot = FindRepositoryRoot(baseDirectory);
        if (repositoryRoot is not null)
        {
            candidates.Add(Path.Combine(repositoryRoot, "artifacts", "native", "macos", fileName));
        }

        return [.. candidates.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Walks up looking for the marker file that identifies the repository root, or returns null.</summary>
    private static string? FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RemoteDesktop.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
