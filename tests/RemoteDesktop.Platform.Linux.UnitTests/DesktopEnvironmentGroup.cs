namespace RemoteDesktop.Platform.Linux.UnitTests;

/// <summary>
/// The tests that move an <c>XDG_*</c> variable, and the ones that read the folders those variables name.
/// </summary>
/// <remarks>
/// <c>XDG_DATA_HOME</c> is process-wide and xUnit runs classes in parallel, so a class that points it at a temporary
/// folder decides what the class beside it sees. The symptom is the quiet kind: the other test does not fail, it takes
/// the wrong data directory and either skips or passes for the wrong reason. Two classes need the rule today —
/// <see cref="PortalAppIdTests"/> moves the variable and <see cref="SessionEndToEndTests"/> reads it through the data
/// directory — and the collection is where a third one belongs.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DesktopEnvironmentGroup
{
    /// <summary>The collection every class that moves or reads an <c>XDG_*</c> variable belongs to.</summary>
    public const string Name = "desktop environment";
}
