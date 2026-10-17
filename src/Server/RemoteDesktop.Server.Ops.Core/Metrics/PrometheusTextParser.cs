using System.Globalization;
using System.Text;

namespace RemoteDesktop.Server.Ops.Core;

/// <summary>One parsed sample: its identity, the metric it belongs to, and the number.</summary>
/// <param name="Key">Name plus canonical labels — what the console stores and queries by.</param>
/// <param name="Name">The metric name without labels, for looking up the type.</param>
/// <param name="Value">The sample value. Non-finite values never reach here.</param>
public readonly record struct PrometheusSample(string Key, string Name, double Value);

/// <summary>What one scrape produced.</summary>
/// <param name="Types">Metric name to kind, from the exposition's <c># TYPE</c> lines.</param>
/// <param name="Samples">Samples by <see cref="PrometheusSample.Key"/>; a duplicate within one scrape keeps the last.</param>
/// <param name="Skipped">Lines that were neither comments nor usable samples.</param>
public sealed record PrometheusSnapshot(
    IReadOnlyDictionary<string, MetricKind> Types,
    IReadOnlyList<PrometheusSample> Samples,
    int Skipped);

/// <summary>
/// Parses Prometheus' text exposition format, which is what the server's <c>/metrics</c> already speaks.
/// </summary>
/// <remarks>
/// Hand-written rather than regex-per-line: a scrape is a few hundred lines every ten seconds per node, and the format
/// has exactly two shapes worth supporting. Anything unrecognised is counted and skipped — one malformed line must not
/// cost a whole scrape, and a parser that throws on surprises is a monitoring outage waiting for a server upgrade.
///
/// Labels are canonicalised (sorted, whitespace removed, escapes decoded) before they become part of the series key,
/// so the same series always lands on the same row regardless of how the emitter ordered its labels.
/// </remarks>
public static class PrometheusTextParser
{
    /// <summary>Parses <paramref name="text"/>; never throws for malformed input.</summary>
    public static PrometheusSnapshot Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var types = new Dictionary<string, MetricKind>(StringComparer.Ordinal);
        var samples = new Dictionary<string, PrometheusSample>(StringComparer.Ordinal);
        var skipped = 0;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '#')
            {
                ReadTypeLine(line, types);
                continue;
            }

            if (TryReadSample(line, out var sample))
            {
                samples[sample.Key] = sample;
            }
            else
            {
                skipped++;
            }
        }

        return new PrometheusSnapshot(types, [.. samples.Values], skipped);
    }

    private static void ReadTypeLine(string line, Dictionary<string, MetricKind> types)
    {
        // "# TYPE <name> <type>"; HELP and anything else is ignored.
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 4 || !string.Equals(parts[1], "TYPE", StringComparison.Ordinal))
        {
            return;
        }

        if (MetricKinds.FromPrometheusType(parts[3]) is { } kind)
        {
            types[parts[2]] = kind;
        }
    }

    private static bool TryReadSample(string line, out PrometheusSample sample)
    {
        sample = default;

        var end = SeriesEnd(line);
        var series = line[..end];
        var rest = line[end..].TrimStart();
        if (series.Length == 0 || rest.Length == 0)
        {
            return false;
        }

        var valueEnd = rest.IndexOf(' ', StringComparison.Ordinal);
        var valueText = valueEnd < 0 ? rest : rest[..valueEnd];
        if (!double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value))
        {
            // NaN and infinities are legal in the format and useless in a chart. They are skipped, not stored as null:
            // a gap says "no sample", which is the truth.
            return false;
        }

        var brace = series.IndexOf('{', StringComparison.Ordinal);
        var name = brace < 0 ? series : series[..brace];
        if (!IsMetricName(name))
        {
            return false;
        }

        string key;
        if (brace < 0)
        {
            key = name;
        }
        else
        {
            if (!TryCanonicalLabels(series[brace..], out var labels))
            {
                return false;
            }

            key = name + labels;
        }

        sample = new PrometheusSample(key, name, value);
        return true;
    }

    /// <summary>The index just past the series token: whitespace outside braces and quotes.</summary>
    private static int SeriesEnd(string line)
    {
        var braces = 0;
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (quoted)
            {
                if (character == '\\')
                {
                    index++;
                }
                else if (character == '"')
                {
                    quoted = false;
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    quoted = true;
                    break;
                case '{':
                    braces++;
                    break;
                case '}':
                    braces--;
                    break;
                case ' ' when braces == 0:
                case '\t' when braces == 0:
                    return index;
            }
        }

        return line.Length;
    }

    private static bool IsMetricName(string name)
    {
        if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_' || name[0] == ':'))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('_' or ':'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Turns <c>{b="2",a="1"}</c> into <c>{a="1",b="2"}</c>, decoding the three escapes the format defines.
    /// </summary>
    private static bool TryCanonicalLabels(string text, out string canonical)
    {
        canonical = string.Empty;

        var pairs = new List<(string Name, string Value)>();
        var index = 1; // past '{'

        while (index < text.Length)
        {
            while (index < text.Length && (text[index] == ' ' || text[index] == ','))
            {
                index++;
            }

            if (index >= text.Length || text[index] == '}')
            {
                break;
            }

            var nameStart = index;
            while (index < text.Length && text[index] != '=' && text[index] != ',')
            {
                index++;
            }

            if (index >= text.Length || text[index] != '=')
            {
                return false;
            }

            var name = text[nameStart..index].Trim();
            index++; // '='
            if (index >= text.Length || text[index] != '"')
            {
                return false;
            }

            index++; // opening quote
            var value = new StringBuilder();
            var closed = false;
            while (index < text.Length)
            {
                var character = text[index++];
                if (character == '"')
                {
                    closed = true;
                    break;
                }

                if (character != '\\')
                {
                    value.Append(character);
                    continue;
                }

                if (index >= text.Length)
                {
                    return false;
                }

                var escaped = text[index++];
                value.Append(escaped switch
                {
                    'n' => '\n',
                    '\\' => '\\',
                    '"' => '"',
                    _ => escaped,
                });
            }

            if (!closed || name.Length == 0)
            {
                return false;
            }

            pairs.Add((name, value.ToString()));
        }

        pairs.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));

        var builder = new StringBuilder("{");
        for (var i = 0; i < pairs.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(pairs[i].Name).Append("=\"").Append(Escape(pairs[i].Value)).Append('"');
        }

        canonical = builder.Append('}').ToString();
        return true;
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);
}
