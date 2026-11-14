using System.Reflection;
using System.Runtime.InteropServices;
using RemoteDesktop.Application;

namespace RemoteDesktop.Platform.Windows.UnitTests;

/// <summary>
/// Finds the installer a package test should open.
/// </summary>
/// <remarks>
/// <para>
/// A path in the environment comes from the packaging script and is trusted: it is the package that was just built.
/// The fallback discovery is what a developer gets without that variable, and it must not treat a package left over
/// from an earlier build as this one. The product version is the only thing a stale package cannot fake, so a
/// discovered package whose <c>ProductVersion</c> is not this build's version is reported as a skip rather than
/// tested. Without that check a same-version stale package would pass and a newer one would fail the suite for a
/// reason that says nothing about the code.
/// </para>
/// </remarks>
internal static class InstallerLocator
{
    /// <param name="pattern">File-name glob under <c>artifacts/installer/windows</c>, role included.</param>
    /// <param name="environmentVariable">The packaging script's override.</param>
    /// <param name="buildHint">What to run to produce the package, used as the skip reason when none is found.</param>
    public static InstallerCandidate Find(string pattern, string environmentVariable, string buildHint)
    {
        var given = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrEmpty(given))
        {
            // The packaging script's package is authoritative, even if its version is not this checkout's: that is
            // a build problem for the packaging test to report, not a reason to silently skip.
            return File.Exists(given)
                ? new InstallerCandidate(given, string.Empty)
                : new InstallerCandidate(null, $"The installer path in {environmentVariable} does not exist.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return new InstallerCandidate(null, "Windows Installer is Windows only.");
        }

        var root = FindRepositoryRoot();
        if (root is null)
        {
            return new InstallerCandidate(null, "No RemoteDesktop.slnx above the test binary.");
        }

        var directory = Path.Combine(root, "artifacts", "installer", "windows");
        if (!Directory.Exists(directory))
        {
            return new InstallerCandidate(null, buildHint);
        }

        var candidate = Directory
            .GetFiles(directory, pattern)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (candidate is null)
        {
            return new InstallerCandidate(null, buildHint);
        }

        var expected = $"{ProductVersion.Display}.0";
        var actual = TryReadProductVersion(candidate);
        if (actual is null)
        {
            return new InstallerCandidate(
                null,
                $"Could not read ProductVersion from {Path.GetFileName(candidate)}; rebuild it or set {environmentVariable}.");
        }

        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            return new InstallerCandidate(
                null,
                $"{Path.GetFileName(candidate)} is version {actual}, not {expected}; rebuild it or set {environmentVariable}.");
        }

        return new InstallerCandidate(candidate, string.Empty);
    }

    private static string? TryReadProductVersion(string path)
    {
        try
        {
            using var package = new MsiPackage(path);
            var rows = package.Rows("SELECT `Value` FROM `Property` WHERE `Property` = 'ProductVersion'", 1);
            return rows.Count == 1 ? rows[0][0] : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or TargetInvocationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RemoteDesktop.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName;
    }
}

/// <summary>The package to test, or the sentence explaining why there is none.</summary>
internal readonly record struct InstallerCandidate(string? Path, string SkipReason);
