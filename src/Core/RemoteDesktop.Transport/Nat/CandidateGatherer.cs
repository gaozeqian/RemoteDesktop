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

        if (Socket.OSSupportsIPv6)
        {
            var ipv6Loopback = new IPEndPoint(IPAddress.IPv6Loopback, port);
            if (!candidates.Contains(ipv6Loopback))
            {
                candidates.Add(ipv6Loopback);
            }
        }

        // Loopback goes last, and unconditionally — never left out because the reflector did not answer.
        //
        // Two devices on one machine can only be reached by each other over loopback when the machine's own
        // addresses are not usable: on a CI runner (a firewall profile that drops inbound UDP, a virtual adapter with
        // no route) the interface addresses are exactly that, and the reflective endpoint is only known if the
        // reflector answered within its timeout. A peer that gets no usable candidate falls through to the relay,
        // and if that fails too the connection is simply impossible — on the same machine.
        //
        // The cost for a genuinely remote peer is one attempt against an address that is not listening, at the end of
        // the list: a closed loopback port answers (or refuses) immediately rather than waiting out the timeout.
        var loopback = new IPEndPoint(IPAddress.Loopback, port);
        if (!candidates.Contains(loopback))
        {
            candidates.Add(loopback);
        }

        return candidates;
    }

    public static PeerEndpoint ToMessage(IPEndPoint endPoint)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        return new PeerEndpoint { Address = endPoint.Address.ToString(), Port = (uint)endPoint.Port };
    }

    /// <summary>
    /// Most candidates taken from a peer. Far more than any machine has addresses; the limit exists because every
    /// candidate costs a few punch packets sent to an address the peer chose, and a 64 KiB signaling message can
    /// otherwise carry thousands.
    /// </summary>
    public const int MaxPeerCandidates = 64;

    /// <summary>Parses candidates from the peer, skipping anything malformed and keeping the first <see cref="MaxPeerCandidates"/>.</summary>
    public static List<IPEndPoint> FromMessages(IEnumerable<PeerEndpoint> endpoints) =>
        [.. endpoints
            .Where(e => e.Port is > 0 and <= ushort.MaxValue)
            .Select(e => IPAddress.TryParse(e.Address, out var address) ? new IPEndPoint(address, (int)e.Port) : null)
            .OfType<IPEndPoint>()
            .Where(e => e.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .Distinct()
            .Take(MaxPeerCandidates)];

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
                else if (address.AddressFamily == AddressFamily.InterNetworkV6
                         && !address.IsIPv6LinkLocal
                         && !address.IsIPv6Multicast
                         && !address.IsIPv6SiteLocal)
                {
                    yield return address;
                }
            }
        }
    }

    private static bool IsLinkLocal(IPAddress address) =>
        address.ToString().StartsWith("169.254.", false, CultureInfo.InvariantCulture);
}
