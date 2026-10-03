using RemoteDesktop.Domain;

namespace RemoteDesktop.Core.UnitTests;

public class DeviceIdTests
{
    [Theory]
    [InlineData("123456789", 123456789)]
    [InlineData("123 456 789", 123456789)]
    [InlineData(" 999999999 ", 999999999)]
    public void TryParse_accepts_nine_digits_with_optional_grouping(string text, int expected)
    {
        Assert.True(DeviceId.TryParse(text, out var id));
        Assert.Equal(expected, id.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345678")]
    [InlineData("1234567890")]
    [InlineData("012345678")]
    [InlineData("12345678a")]
    [InlineData("１２３４５６７８９")] // full-width digits
    public void TryParse_rejects_invalid_input(string? text) => Assert.False(DeviceId.TryParse(text, out _));

    [Fact]
    public void ToDisplayString_groups_by_three() =>
        Assert.Equal("123 456 789", new DeviceId(123456789).ToDisplayString());

    [Fact]
    public void Constructor_rejects_out_of_range() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeviceId(99_999_999));
}
