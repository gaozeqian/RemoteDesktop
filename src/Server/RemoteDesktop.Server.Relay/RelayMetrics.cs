using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Relay;

/// <summary>
/// What the relay knows about itself at one moment, taken atomically enough for an HTTP request on another thread.
/// </summary>
/// <param name="PairingsActive">Sessions with both endpoints bound right now.</param>
/// <param name="BytesToController">Bytes forwarded host-to-controller since start.</param>
/// <param name="BytesToHost">Bytes forwarded controller-to-host since start.</param>
/// <param name="PacketsForwarded">Datagrams forwarded since start.</param>
/// <param name="PairingsTotal">Pairings completed since start.</param>
public readonly record struct RelaySnapshot(
    int PairingsActive,
    long BytesToController,
    long BytesToHost,
    long PacketsForwarded,
    long PairingsTotal);

/// <summary>
/// The relay's Prometheus exposition. Same format and content type the signaling server uses, because the console
/// scrapes both with one parser.
/// </summary>
public static class RelayMetrics
{
    /// <summary>The exposition format, as Prometheus is told to expect it.</summary>
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    /// <summary>Renders <paramref name="snapshot"/> as HELP / TYPE / sample triples.</summary>
    public static string Render(RelaySnapshot snapshot)
    {
        var text = new System.Text.StringBuilder(1024);
        Write(text, OpsMetricNames.RelayPairingsActive, "gauge", "Relay sessions with both peers bound right now.", snapshot.PairingsActive);
        Write(text, OpsMetricNames.RelayPairingsTotal, "counter", "Relay sessions paired since the process started.", snapshot.PairingsTotal);
        Write(text, OpsMetricNames.RelayBytesToHost, "counter", "Bytes forwarded from controllers to hosts.", snapshot.BytesToHost);
        Write(text, OpsMetricNames.RelayBytesToController, "counter", "Bytes forwarded from hosts to controllers.", snapshot.BytesToController);
        Write(text, OpsMetricNames.RelayPackets, "counter", "Datagrams forwarded in either direction.", snapshot.PacketsForwarded);
        return text.ToString();
    }

    private static void Write(System.Text.StringBuilder text, string name, string type, string help, long value)
    {
        text.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        text.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
        text.Append(name).Append(' ').Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
    }
}
