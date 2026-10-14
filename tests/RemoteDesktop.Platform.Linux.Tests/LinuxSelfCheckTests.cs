using RemoteDesktop.Platform.Linux;

namespace RemoteDesktop.Platform.Linux.Tests;

/// <summary>
/// The report itself, rather than what it found.
/// </summary>
/// <remarks>
/// The answers depend on the machine — CI has no graphical session and no portal, and that is a correct report
/// rather than a failure — so nothing here asserts that any check passed. What it does assert is that every check
/// is still asked: a diagnostic that quietly stops looking at one of the five native subsystems is worse than none,
/// because it answers "all clear" for a machine that is not.
/// </remarks>
public sealed class LinuxSelfCheckTests
{
    private const string LinuxOnly = "The Linux platform layer.";

    /// <summary>The questions that must be asked, whatever the answers turn out to be.</summary>
    private static readonly string[] Expected =
    [
        "Desktop session",
        "Session bus",
        "Runtime directory",
        "Portal and camera",
        "Display layout",
        "PipeWire",
        "libei",
        "libavcodec",
        "H.264 encoders",
    ];

    [Fact]
    public void Every_subsystem_is_still_asked_about()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var probes = LinuxSelfCheck.Run();

        Assert.Equal(Expected, probes.Select(probe => probe.Name));
    }

    [Fact]
    public void An_answer_that_is_not_ok_says_why()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        foreach (var probe in LinuxSelfCheck.Run())
        {
            // A failed check whose detail is empty tells an operator nothing they did not already know, and the
            // whole reason this exists is that the failures otherwise come with no context.
            Assert.False(string.IsNullOrWhiteSpace(probe.Detail), $"{probe.Name} reported nothing.");

            if (!probe.Ok)
            {
                Assert.True(probe.Detail.Length > 0, $"{probe.Name} failed without saying why.");
            }
        }
    }

    [Fact]
    public void Running_it_again_answers_the_same_questions()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // Nothing is cached and nothing is left behind: somebody diagnosing a machine runs this repeatedly, often
        // after changing one thing, and a second run that reports a different set of checks would be useless.
        var first = LinuxSelfCheck.Run().Select(probe => probe.Name);
        var second = LinuxSelfCheck.Run().Select(probe => probe.Name);

        Assert.Equal(first, second);
    }
}
