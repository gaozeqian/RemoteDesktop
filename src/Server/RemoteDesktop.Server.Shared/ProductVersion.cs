using System.Reflection;

namespace RemoteDesktop.Server.Shared;

/// <summary>
/// The product's version, in the two shapes the servers use.
/// </summary>
/// <remarks>
/// <para>
/// There is one number: <c>&lt;Version&gt;</c> in the repository's <c>Directory.Build.props</c>. The SDK derives three
/// artefacts from it — <c>AssemblyVersion</c> (<c>0.3.0.0</c>), <c>FileVersion</c> (<c>0.3.0.0</c>) and
/// <c>AssemblyInformationalVersion</c> (<c>0.3.0+&lt;commit&gt;</c>) — and each surface used to pick whichever one it
/// happened to reach for: a node's reported version showed four parts while its package showed three.
/// </para>
/// <para>
/// <see cref="Display"/> is what a person should see and what a package is named after: the three parts, with no commit
/// suffix. <see cref="Full"/> is for diagnostics, where telling two builds of the same version apart is the point.
/// </para>
/// <para>
/// This is deliberately a second copy of <c>RemoteDesktop.Application.ProductVersion</c>: the server may not depend on
/// the application layer, and <c>Signaling</c> is wire contracts only. <c>ProductVersionTests</c> asserts that the two
/// agree, so the duplication cannot turn into two answers.
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

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";

        // The build puts the commit in an AssemblyMetadata attribute rather than in AssemblyInformationalVersion:
        // Windows shows the informational version as the file's "Product version", and a commit hash there is noise
        // in a field people read. Rejoining the two here keeps telemetry's "0.3.0+<sha>" while the metadata stays
        // three-part.
        string? commit = null;
        foreach (var attribute in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (attribute.Key == "Commit")
            {
                commit = attribute.Value;
                break;
            }
        }

        return string.IsNullOrEmpty(commit) || informational.Contains('+')
            ? informational
            : $"{informational}+{commit}";
    }
}
