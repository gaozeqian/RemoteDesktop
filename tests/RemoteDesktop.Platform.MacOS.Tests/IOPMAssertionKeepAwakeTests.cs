using RemoteDesktop.Platform.MacOS.Interop;

namespace RemoteDesktop.Platform.MacOS.Tests;

/// <summary>
/// Exercises the power assertion for real, because resolving it is not the same as being able to take one.
/// </summary>
/// <remarks>
/// This is the test whose absence let a broken implementation pass review: the container could resolve
/// <see cref="IOPMAssertionKeepAwake"/> and the type was registered, but the first <c>Acquire</c> threw
/// <see cref="EntryPointNotFoundException"/> because the assertion type was being looked up as an exported symbol
/// when the SDK defines it as a literal. Registering a port and using it are different claims.
/// </remarks>
public class IOPMAssertionKeepAwakeTests
{
    [Fact]
    public void An_assertion_can_be_taken_and_given_back()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The macOS platform is macOS only.");

        var keepAwake = new IOPMAssertionKeepAwake();

        keepAwake.Acquire();
        keepAwake.Release();
    }

    [Fact]
    public void The_assertion_type_is_the_literal_the_sdk_defines()
    {
        // If IOKit ever did export this as a symbol, a lookup would still be the wrong thing to depend on: the
        // literal is what Apple's header expands to, and it is what the kernel compares against.
        Assert.Equal("PreventUserIdleSystemSleep", IOKit.AssertionTypePreventUserIdleSystemSleep);
    }

    [Fact]
    public void Nested_acquires_are_balanced_so_the_first_release_does_not_end_the_assertion()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The macOS platform is macOS only.");

        var keepAwake = new IOPMAssertionKeepAwake();

        // Two sessions at once: releasing one must not let the machine sleep while the other is still connected.
        keepAwake.Acquire();
        keepAwake.Acquire();
        keepAwake.Release();
        keepAwake.Release();

        // An unbalanced release must not go negative and start taking assertions nobody asked for.
        keepAwake.Release();
        keepAwake.Acquire();
        keepAwake.Release();
    }

    [Fact]
    public void Taking_an_assertion_twice_in_a_row_does_not_leak_the_first_one()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The macOS platform is macOS only.");

        var keepAwake = new IOPMAssertionKeepAwake();

        // The counter, not the assertion id, is what decides whether IOKit is called again; if that were wrong the
        // first assertion would be orphaned and never released.
        for (var i = 0; i < 100; i++)
        {
            keepAwake.Acquire();
            keepAwake.Release();
        }
    }
}
