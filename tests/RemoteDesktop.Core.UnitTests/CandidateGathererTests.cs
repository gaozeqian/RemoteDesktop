using System.Net;
using RemoteDesktop.Signaling.Messages;
using RemoteDesktop.Transport.Nat;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The candidate list is what we tell the peer to try. It has to end with loopback.
/// </summary>
/// <remarks>
/// Two devices on one machine can only reach each other over loopback when the machine's own addresses are unusable
/// — on a CI runner that is a firewall profile dropping inbound UDP or a virtual adapter with no route — and the
/// reflective endpoint is only known if the reflector answered inside its timeout. When both of those were true, a
/// connection between two processes on the same machine had no way to succeed: every candidate was dead and the
/// relay was the only thing left, which is how a release run failed on a test that merely connects (2026-10-11).
/// </remarks>
public class CandidateGathererTests
{
    [Fact]
    public void Loopback_is_the_last_candidate_and_is_there_without_a_reflexive_endpoint()
    {
        var candidates = CandidateGatherer.Gather(51820, reflexive: null);

        Assert.Contains(candidates, candidate => candidate.Address.Equals(IPAddress.Loopback) && candidate.Port == 51820);
        Assert.Equal(IPAddress.Loopback, candidates[^1].Address);
    }

    /// <summary>A peer that lists thousands of addresses must not make this side send thousands of packets.</summary>
    [Fact]
    public void A_peer_cannot_make_us_try_more_than_the_candidate_limit()
    {
        var flood = Enumerable.Range(1, 3000)
            .Select(i => new PeerEndpoint { Address = $"10.0.{i / 250}.{i % 250 + 1}", Port = 40000 })
            .ToList();

        var candidates = CandidateGatherer.FromMessages(flood);

        Assert.Equal(CandidateGatherer.MaxPeerCandidates, candidates.Count);
        Assert.Equal(IPAddress.Parse("10.0.0.2"), candidates[0].Address);
    }

    [Fact]
    public void A_normal_candidate_list_is_taken_whole_and_in_order()
    {
        var list = new[]
        {
            new PeerEndpoint { Address = "192.168.1.5", Port = 50000 },
            new PeerEndpoint { Address = "203.0.113.9", Port = 50001 },
            new PeerEndpoint { Address = "127.0.0.1", Port = 50000 },
        };

        var candidates = CandidateGatherer.FromMessages(list);

        Assert.Equal(3, candidates.Count);
        Assert.Equal(IPAddress.Parse("192.168.1.5"), candidates[0].Address);
        Assert.Equal(IPAddress.Loopback, candidates[^1].Address);
    }

    [Fact]
    public void A_reflexive_endpoint_that_is_already_loopback_does_not_appear_twice()
    {
        // What the reflector reports when it runs on the same machine, which is the case in the test fixtures.
        var reflexive = new IPEndPoint(IPAddress.Loopback, 51820);

        var candidates = CandidateGatherer.Gather(51820, reflexive);

        Assert.Single(candidates, candidate => candidate.Address.Equals(IPAddress.Loopback));
    }
}
