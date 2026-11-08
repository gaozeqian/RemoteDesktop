using RemoteDesktop.Application.Sessions;
using RemoteDesktop.Domain;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// Granted permissions arrive from a peer as an untrusted 32-bit number. The wire layer, not the feature modules, is
/// where bits outside the documented flags are discarded; a regression there would let a controller claim a
/// permission the host never defined.
/// </summary>
public class ControlMessagesWireTests
{
    [Theory]
    [InlineData(0x8000_0000u)]
    [InlineData(0x0001_0000u)]
    [InlineData(0x7FFF_0000u)]
    public void Unknown_permission_bits_are_dropped_on_the_wire(uint raw)
    {
        Assert.Equal(Permissions.None, ControlMessages.FromWire(raw));
    }

    [Fact]
    public void Known_permission_bits_survive_even_when_unknown_bits_are_present()
    {
        Assert.Equal(Permissions.All, ControlMessages.FromWire((uint)Permissions.All | 0x8000_0000u));
        Assert.Equal(
            Permissions.ViewScreen | Permissions.ControlInput,
            ControlMessages.FromWire(0x8000_0003u));
        Assert.Equal(Permissions.None, ControlMessages.FromWire(0u));
    }
}
