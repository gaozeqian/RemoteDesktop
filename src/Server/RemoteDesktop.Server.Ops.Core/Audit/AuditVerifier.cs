namespace RemoteDesktop.Server.Ops.Core;

/// <summary>One audit row, exactly as stored.</summary>
public sealed record AuditRecord(
    string Id,
    long Timestamp,
    string Actor,
    string Action,
    string Target,
    string DetailJson,
    string Ip,
    string Result,
    string PrevHash,
    string Hash);

/// <summary>What a verification found.</summary>
/// <param name="IsValid">True when every entry links to its predecessor and its own hash.</param>
/// <param name="FirstInvalidId">The oldest entry that does not check out; null when the chain is sound.</param>
/// <param name="Checked">How many entries were walked.</param>
public sealed record AuditVerification(bool IsValid, string? FirstInvalidId, int Checked);

/// <summary>
/// Recomputes the audit chain and reports the first entry that does not match.
/// </summary>
/// <remarks>
/// "First" in the sense of oldest: a broken entry makes every later hash wrong too, so the useful answer is where the
/// damage starts, not the last row that happens to disagree. The check covers both halves — the stored hash of an
/// entry, and its link to the hash before it — because either one being wrong means somebody edited the table.
/// </remarks>
public static class AuditVerifier
{
    /// <summary>Verifies <paramref name="records"/>, which must be ordered oldest first.</summary>
    public static AuditVerification Verify(IReadOnlyList<AuditRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var previous = AuditChain.Genesis;
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];

            var expectedLink = index == 0 ? AuditChain.Genesis : records[index - 1].Hash;
            if (!string.Equals(record.PrevHash, expectedLink, StringComparison.Ordinal))
            {
                return new AuditVerification(false, record.Id, index);
            }

            var expected = AuditChain.Compute(
                record.PrevHash,
                AuditChain.Fields(
                    record.Timestamp,
                    record.Actor,
                    record.Action,
                    record.Target,
                    record.DetailJson,
                    record.Ip,
                    record.Result));

            if (!string.Equals(expected, record.Hash, StringComparison.Ordinal))
            {
                return new AuditVerification(false, record.Id, index);
            }

            previous = record.Hash;
        }

        return new AuditVerification(true, null, records.Count);
    }
}
