using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RemoteDesktop.Signaling.Messages;

namespace RemoteDesktop.Transport.Nat;

/// <summary>Endpoints a peer may reach us on, in the order they should be tried: LAN first, then public.</summary>
public static class CandidateGatherer
{
    public static List<IPEndPoint> Gather(int port, IPEndPoint? reflexive)
    {
        var candidates = new List<IPEndPoint>();
        foreach (var address in GetLocalAddresses())
        {
            candidates.Add(new IPEndPoint(address, port));
        }

        if (reflexive is not null && !candidates.Contains(reflexive))
        {
            candidates.Add(reflexive);
        }

        return candidates;
    }

    public static PeerEndpoint ToMessage(IPEndPoint endPoint)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        return new PeerEndpoint { Address = endPoint.Address.ToString(), Port = (uint)endPoint.Port };
    }

    /// <summary>Parses candidates from the peer, skipping anything malformed.</summary>
    public static List<IPEndPoint> FromMessages(IEnumerable<PeerEndpoint> endpoints) =>
        [.. endpoints
            .Where(e => e.Port is > 0 and <= ushort.MaxValue)
            .Select(e => IPAddress.TryParse(e.Address, out var address) ? new IPEndPoint(address, (int)e.Port) : null)
            .OfType<IPEndPoint>()
            .Where(e => e.AddressFamily == AddressFamily.InterNetwork)
            .Distinct()];

    public static string Describe(IEnumerable<IPEndPoint> endpoints) =>
        string.Join(", ", endpoints.Select(e => e.ToString()));

    private static IEnumerable<IPAddress> GetLocalAddresses()
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            yield break;
        }

        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily == AddressFamily.InterNetwork && !IsLinkLocal(address))
                {
                    yield return address;
                }
            }
        }
    }

    private static bool IsLinkLocal(IPAddress address) =>
        address.ToString().StartsWith("169.254.", false, CultureInfo.InvariantCulture);
}
