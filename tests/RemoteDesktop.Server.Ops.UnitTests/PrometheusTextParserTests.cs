using RemoteDesktop.Server.Ops.Core;

namespace RemoteDesktop.Server.Ops.UnitTests;

/// <summary>
/// The parser is the only thing standing between a server's text and every number on the dashboard. The tests care
/// about the shapes a real exposition actually contains — labels with spaces in values, comments, a stray malformed
/// line — and about the ones it must refuse rather than guess.
/// </summary>
public class PrometheusTextParserTests
{
    [Fact]
    public void Types_and_samples_are_read_together()
    {
        var snapshot = PrometheusTextParser.Parse("""
            # HELP remotedesktop_devices_online Devices online right now.
            # TYPE remotedesktop_devices_online gauge
            remotedesktop_devices_online 12847
            # TYPE remotedesktop_relay_bytes_to_host_total counter
            remotedesktop_relay_bytes_to_host_total 9912
            """);

        Assert.Equal(MetricKind.Gauge, snapshot.Types["remotedesktop_devices_online"]);
        Assert.Equal(MetricKind.Counter, snapshot.Types["remotedesktop_relay_bytes_to_host_total"]);
        Assert.Equal(2, snapshot.Samples.Count);
        Assert.Equal(12847, snapshot.Samples.Single(sample => sample.Name == "remotedesktop_devices_online").Value);
        Assert.Equal(0, snapshot.Skipped);
    }

    [Fact]
    public void Labels_become_part_of_the_key_in_a_stable_order()
    {
        var snapshot = PrometheusTextParser.Parse("""
            # TYPE requests_total counter
            requests_total{zone="sh",reason="offline"} 3
            requests_total{reason="offline",zone="sh"} 4
            """);

        // Same series, written two ways: one row, and the later reading wins.
        var sample = Assert.Single(snapshot.Samples);
        Assert.Equal("""requests_total{reason="offline",zone="sh"}""", sample.Key);
        Assert.Equal(4, sample.Value);
    }

    [Fact]
    public void Label_values_may_contain_spaces_quotes_and_braces()
    {
        var snapshot = PrometheusTextParser.Parse("""
            # TYPE x gauge
            x{note="a \"quoted\" value, with } brace"} 1.5
            """);

        var sample = Assert.Single(snapshot.Samples);
        Assert.Equal("""x{note="a \"quoted\" value, with } brace"}""", sample.Key);
        Assert.Equal(1.5, sample.Value);
    }

    [Fact]
    public void Scientific_notation_and_negative_values_are_read()
    {
        var snapshot = PrometheusTextParser.Parse("""
            # TYPE x gauge
            x 1.5e3
            y -42
            """);

        Assert.Equal(1500, snapshot.Samples.Single(sample => sample.Name == "x").Value);
        Assert.Equal(-42, snapshot.Samples.Single(sample => sample.Name == "y").Value);
    }

    [Theory]
    [InlineData("x NaN")]
    [InlineData("x +Inf")]
    [InlineData("x not-a-number")]
    [InlineData("x")]
    [InlineData("x{a=\"1\" 2")]
    public void Unusable_lines_are_skipped_and_counted(string line)
    {
        var snapshot = PrometheusTextParser.Parse(line);

        Assert.Empty(snapshot.Samples);
        Assert.Equal(1, snapshot.Skipped);
    }

    [Fact]
    public void One_bad_line_does_not_cost_the_scrape()
    {
        var snapshot = PrometheusTextParser.Parse("""
            # TYPE x gauge
            x 1
            this is not a sample
            y 2
            """);

        Assert.Equal(2, snapshot.Samples.Count);
        Assert.Equal(1, snapshot.Skipped);
    }

    [Fact]
    public void A_duplicate_reading_within_one_scrape_keeps_the_last()
    {
        var snapshot = PrometheusTextParser.Parse("""
            # TYPE x gauge
            x 1
            x 9
            """);

        Assert.Equal(9, Assert.Single(snapshot.Samples).Value);
    }

    [Fact]
    public void Histograms_are_ignored_rather_than_half_understood()
    {
        var snapshot = PrometheusTextParser.Parse("""
            # TYPE latency histogram
            latency_bucket{le="1"} 10
            """);

        Assert.False(snapshot.Types.ContainsKey("latency"));
        // The bucket line is still a well-formed sample; it is stored, but no rule may aggregate it as a histogram.
        Assert.Single(snapshot.Samples);
    }
}
