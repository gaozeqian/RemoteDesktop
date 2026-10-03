namespace RemoteDesktop.Abstractions.Media;

/// <summary>Minimal Annex-B inspection: enough to tell key frames and parameter sets apart.</summary>
public static class H264Bitstream
{
    public const int NalIdr = 5;
    public const int NalSps = 7;
    public const int NalPps = 8;

    /// <summary>NAL unit types in order of appearance.</summary>
    public static List<int> GetNalTypes(ReadOnlySpan<byte> annexB)
    {
        var types = new List<int>();
        var i = 0;
        while (TryFindStartCode(annexB, i, out var payload))
        {
            if (payload < annexB.Length)
            {
                types.Add(annexB[payload] & 0x1F);
            }

            i = payload;
        }

        return types;
    }

    public static bool IsKeyFrame(ReadOnlySpan<byte> annexB) => GetNalTypes(annexB).Contains(NalIdr);

    public static bool ContainsParameterSets(ReadOnlySpan<byte> annexB)
    {
        var types = GetNalTypes(annexB);
        return types.Contains(NalSps) && types.Contains(NalPps);
    }

    // Finds the next 00 00 01 at or after `from`; `payload` is the index right after it.
    private static bool TryFindStartCode(ReadOnlySpan<byte> data, int from, out int payload)
    {
        for (var i = from; i + 2 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                payload = i + 3;
                return true;
            }
        }

        payload = data.Length;
        return false;
    }
}
