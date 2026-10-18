using System.Globalization;
using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Client.ViewModels;

namespace RemoteDesktop.Platform.Windows.UnitTests;

/// <summary>
/// The fixed-password editor asks <see cref="FixedPasswordPolicy"/> for its rule instead of keeping its own copy.
/// </summary>
/// <remarks>
/// It did keep a copy — 6 — after the policy was raised to 8. A 7-character password passed the window's check,
/// reached <c>LocalHostControl</c>, and the <see cref="ArgumentException"/> it threw escaped the command onto the UI
/// thread and killed the process. These assertions run wherever this suite runs, which is all three platforms: the
/// rule is in the shared client, not in the Windows port. They live here because this is the project that already
/// references the client for its platform-sentence tests.
/// </remarks>
public class FixedPasswordValidationTests
{
    /// <summary>Digits only, so the assertion on the number in the message cannot match something else.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1234567")]
    public void Passwords_below_the_policy_minimum_are_refused_with_the_policys_number(string? password)
    {
        var error = MainWindowViewModel.ValidateFixedPassword(password);

        Assert.NotNull(error);
        Assert.Contains(
            FixedPasswordPolicy.MinimumLength.ToString(CultureInfo.InvariantCulture),
            error,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_password_at_the_policy_minimum_is_accepted()
    {
        var password = new string('a', FixedPasswordPolicy.MinimumLength);

        Assert.Null(MainWindowViewModel.ValidateFixedPassword(password));
    }
}
