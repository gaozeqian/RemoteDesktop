using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RemoteDesktop.Server.Ops.Core;

/// <summary>
/// The audit log's hash chain: each entry commits to the one before it, so changing a row means rewriting every row
/// after it. Not tamper-proof — whoever owns the database can rebuild the chain — but it turns "quietly edit one
/// record" into "rewrite the whole log", which is the difference between an accident and a decision.
/// </summary>
public static class AuditChain
{
    /// <summary>The value the first entry links to.</summary>
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>
    /// SHA-256 over the previous hash and length-prefixed fields. Length prefixes matter: without them, fields
    /// ("ab", "c") and ("a", "bc") hash the same, and a log that can be rearranged is not a log.
    /// </summary>
    public static string Compute(string previousHash, IReadOnlyList<string> fields)
    {
        ArgumentNullException.ThrowIfNull(previousHash);
        ArgumentNullException.ThrowIfNull(fields);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(previousHash));
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>The canonical field list of one entry, in the order they are hashed.</summary>
    public static IReadOnlyList<string> Fields(
        long timestamp,
        string actor,
        string action,
        string target,
        string detailJson,
        string ip,
        string result) =>
    [
        timestamp.ToString(CultureInfo.InvariantCulture),
        actor,
        action,
        target,
        detailJson,
        ip,
        result,
    ];
}
