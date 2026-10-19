using System.Reflection;

namespace RemoteDesktop.Application;

/// <summary>
/// The product's version, in the two shapes the client and the application layer use.
/// </summary>
/// <remarks>
/// <para>
/// There is one number: <c>&lt;Version&gt;</c> in the repository's <c>Directory.Build.props</c>. The SDK derives three
/// artefacts from it — <c>AssemblyVersion</c> (<c>0.3.0.0</c>), <c>FileVersion</c> (<c>0.3.0.0</c>) and
/// <c>AssemblyInformationalVersion</c> (<c>0.3.0+&lt;commit&gt;</c>) — and each surface used to pick whichever one it
/// happened to reach for: the sidebar showed four parts, a package showed three, and telemetry carried the commit hash.
/// </para>
/// <para>
/// <see cref="Display"/> is what a person should see and what a package or bundle is named after: the three parts, with
/// no commit suffix. <see cref="Full"/> is for diagnostics and telemetry, where telling two builds of the same version
/// apart is the whole point.
/// </para>
/// <para>
/// The server has its own copy in <c>RemoteDesktop.Server.Shared</c>, because the client and the server deliberately
/// share no utility assembly — <c>Signaling</c> is wire contracts only, and the server may not depend on this layer.
/// <c>ProductVersionTests</c> asserts that the two copies agree, so the duplication cannot turn into two answers.
/// </para>
/// </remarks>
public static class ProductVersion
{
    private static readonly string Value = Read();

    /// <summary>The version without the commit suffix, e.g. <c>0.3.0</c>.</summary>
    public static string Display { get; } = Value.Split('+', 2)[0];

    /// <summary>The informational version as built, e.g. <c>0.3.0+db9820fe…</c>.</summary>
    public static string Full => Value;

    private static string Read()
    {
        var assembly = typeof(ProductVersion).Assembly;

        // The informational version is present on every SDK-built assembly. The fallback is for the theoretical case
        // of one stripped after the fact; a three-part answer is still better than "unknown".
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
    }
}
