using RemoteDesktop.Protocol;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// These values are on the wire between two builds that may be years apart. Renumbering a channel or quietly
/// changing the protocol major would still pass every round-trip test in this repository, because both ends would
/// share the mistake.
/// </summary>
public class WireConstantsTests
{
    [Fact]
    public void Channel_type_wire_values_are_pinned()
    {
        Assert.Equal((byte)0, (byte)ChannelType.Control);
        Assert.Equal((byte)1, (byte)ChannelType.Video);
        Assert.Equal((byte)2, (byte)ChannelType.Audio);
        Assert.Equal((byte)3, (byte)ChannelType.Input);
        Assert.Equal((byte)4, (byte)ChannelType.Clipboard);
        Assert.Equal((byte)5, (byte)ChannelType.FileTransfer);
        Assert.Equal((byte)6, (byte)ChannelType.Terminal);
        Assert.Equal((byte)7, (byte)ChannelType.Camera);
        Assert.Equal(8, Enum.GetValues<ChannelType>().Length);
    }

    [Fact]
    public void The_current_protocol_version_is_the_srp_one()
    {
        var current = ProtocolVersion.Current;

        Assert.Equal(3, current.Major);
        Assert.Equal(0, current.Minor);
        Assert.Equal("3.0", current.ToString());
        Assert.True(current.IsCompatibleWith(new ProtocolVersion(3, 9)));
        Assert.False(current.IsCompatibleWith(new ProtocolVersion(2, 0)));
    }
}
