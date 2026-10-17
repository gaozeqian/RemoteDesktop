using RemoteDesktop.Server.Ops.Core;

namespace RemoteDesktop.Server.Ops.UnitTests;

/// <summary>
/// The exposition's type line is the only thing that keeps a counter out of an average. These tests pin the two names
/// and — more importantly — that everything else is refused rather than guessed.
/// </summary>
public class MetricKindTests
{
    [Theory]
    [InlineData("counter", MetricKind.Counter)]
    [InlineData("COUNTER", MetricKind.Counter)]
    [InlineData("gauge", MetricKind.Gauge)]
    [InlineData("Gauge", MetricKind.Gauge)]
    public void The_two_aggregatable_types_are_read(string type, MetricKind expected)
    {
        Assert.Equal(expected, MetricKinds.FromPrometheusType(type));
    }

    [Theory]
    [InlineData("histogram")]
    [InlineData("summary")]
    [InlineData("untyped")]
    [InlineData("")]
    [InlineData(null)]
    public void Everything_else_is_refused_instead_of_guessed(string? type)
    {
        Assert.Null(MetricKinds.FromPrometheusType(type));
    }
}
