using RemoteDesktop.Protocol;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>The version a peer announces arrives as 32-bit fields; it must not wrap into a compatible one.</summary>
public class ProtocolVersionTests
{
    [Theory]
    [InlineData(65536u + 3u)]
    [InlineData(uint.MaxValue)]
    public void A_major_beyond_16_bits_is_not_compatible_with_the_current_one(uint major)
    {
        var announced = ProtocolVersion.FromWire(major, 0);

        Assert.False(ProtocolVersion.Current.IsCompatibleWith(announced));
    }

    [Fact]
    public void A_version_that_fits_is_taken_as_it_is()
    {
        var announced = ProtocolVersion.FromWire(ProtocolVersion.Current.Major, 7);

        Assert.True(ProtocolVersion.Current.IsCompatibleWith(announced));
        Assert.Equal(7, announced.Minor);
    }
}
