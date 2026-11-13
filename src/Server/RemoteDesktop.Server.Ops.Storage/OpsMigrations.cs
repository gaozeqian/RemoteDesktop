namespace RemoteDesktop.Server.Ops.Storage;

/// <summary>One step from the previous schema version to <paramref name="Version"/>.</summary>
/// <param name="Version">The version this step produces; versions only ever increase.</param>
/// <param name="Sql">Statements that take a database at the previous version to this one.</param>
/// <param name="PostgresSql">
/// Statements for PostgreSQL when they differ from <paramref name="Sql"/>. SQLite and PostgreSQL share a type system
/// for everything the console added until the real-precision change, which has no SQLite equivalent because its
/// <c>REAL</c> is already eight bytes.
/// </param>
public sealed record OpsMigration(int Version, string Sql, string? PostgresSql = null);

/// <summary>
/// Changes made after the baseline schema, in order.
/// </summary>
/// <remarks>
/// The baseline in <see cref="OpsSchema.Sql"/> is append-only: editing it would help a fresh database and do nothing
/// for every existing one, which is the opposite of what a schema change is for. So a column added later is a
/// migration even when it feels like it belongs in the CREATE TABLE, and a fresh database simply runs the baseline
/// followed by every migration.
/// <para>
/// SQLite has no <c>ADD COLUMN IF NOT EXISTS</c>, which is why this is versioned rather than idempotent-per-statement:
/// the recorded version is what decides whether a statement runs, and each migration runs inside its own transaction.
/// </para>
/// </remarks>
public static class OpsMigrations
{
    /// <summary>Every migration, oldest first.</summary>
    public static IReadOnlyList<OpsMigration> All { get; } =
    [
        // 2 — what a client heartbeat can add to a session: the platform and version of the controlled device.
        // Bitrate and loss are deliberately absent: neither is measurable on the client either, and a column that can
        // only ever hold NULL invites a page to render it as zero.
        new(2, """
            ALTER TABLE ops_session ADD COLUMN platform TEXT;
            ALTER TABLE ops_session ADD COLUMN version TEXT;
            """),

        // 3 — the management actions. client_session_id keeps the id the host actually knows a session by (for a
        // relayed session the relay's correlation id is not it), which a disconnect must address; the action table is
        // the two-person review queue that turns an operator's intent into an approved, audited command.
        new(3, """
            ALTER TABLE ops_session ADD COLUMN client_session_id TEXT;
            CREATE TABLE IF NOT EXISTS ops_admin_action (
                id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                node_id TEXT NOT NULL,
                device_id TEXT NOT NULL,
                session_id TEXT,
                detail_json TEXT NOT NULL,
                state TEXT NOT NULL,
                requested_by TEXT NOT NULL,
                requested_at BIGINT NOT NULL,
                approved_by TEXT,
                approved_at BIGINT,
                executed_at BIGINT,
                error TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_ops_admin_action_state ON ops_admin_action (state, requested_at);
            """),

        // 4 — two facts the console did not keep before. last_attempt_at lets a failed webhook be retried with a
        // backoff instead of being forgotten after its first attempt. last_totp_step makes a TOTP code single-use:
        // without it the same six digits can open another session inside the ±30-second acceptance window.
        new(4, """
            ALTER TABLE ops_notification_attempt ADD COLUMN last_attempt_at BIGINT;
            ALTER TABLE ops_admin_user ADD COLUMN last_totp_step BIGINT;
            CREATE INDEX IF NOT EXISTS ix_ops_event_received ON ops_event (received_at);
            """),

        // 5 — PostgreSQL's REAL is four bytes while SQLite's is eight. A byte counter above 2^24 loses precision on
        // PostgreSQL, which is exactly the range a long-lived relay reaches. SQLite needs no equivalent statement,
        // so its side of this migration is a no-op that still records the version.
        new(
            5,
            "SELECT 1;",
            """
            ALTER TABLE ops_metric_sample ALTER COLUMN value TYPE DOUBLE PRECISION;
            ALTER TABLE ops_metric_rollup ALTER COLUMN first_value TYPE DOUBLE PRECISION;
            ALTER TABLE ops_metric_rollup ALTER COLUMN last_value TYPE DOUBLE PRECISION;
            ALTER TABLE ops_metric_rollup ALTER COLUMN min_value TYPE DOUBLE PRECISION;
            ALTER TABLE ops_metric_rollup ALTER COLUMN max_value TYPE DOUBLE PRECISION;
            ALTER TABLE ops_metric_rollup ALTER COLUMN avg_value TYPE DOUBLE PRECISION;
            ALTER TABLE ops_session ALTER COLUMN rtt_ms TYPE DOUBLE PRECISION;
            ALTER TABLE ops_session ALTER COLUMN loss_pct TYPE DOUBLE PRECISION;
            ALTER TABLE ops_alert_rule ALTER COLUMN threshold TYPE DOUBLE PRECISION;
            ALTER TABLE ops_alert ALTER COLUMN value TYPE DOUBLE PRECISION;
            """),

        // 6 — the one thing the planned silence table was missing: why it exists. The baseline has held
        // ops_silence (matcher_json, starts_at, ends_at, created_by) since the first version, waiting for the feature;
        // an operator reading the list needs the reason next to the window, and a reason is not a matcher.
        new(6, "ALTER TABLE ops_silence ADD COLUMN reason TEXT;"),

        // 7 — one token per node, so a leaked console-side credential is one node's, not every node's. Null keeps the
        // deployment-wide Ops:NodeToken, which is what every existing row gets: an upgrade must not silently stop
        // authenticating against machines nobody has touched.
        new(7, "ALTER TABLE ops_node ADD COLUMN token TEXT;"),

        // 8 — the incremental rollup filters by ts and looks up the newest bucket by (step_seconds, bucket). Neither
        // predicate is served by the baseline indexes: the sample table's primary key starts with node_id/metric, and
        // the rollup table's starts with node_id/metric/step_seconds. Without these the range filter scans the whole
        // retention window, which is the cost the incremental pass exists to avoid.
        new(8, """
            CREATE INDEX IF NOT EXISTS ix_ops_metric_sample_ts ON ops_metric_sample (ts);
            CREATE INDEX IF NOT EXISTS ix_ops_metric_rollup_step_bucket ON ops_metric_rollup (step_seconds, bucket);
            """),

        // 9 — retention deletes resolved alerts by state and resolve time. The baseline index leads with opened_at,
        // so the resolved_at branch of that predicate had no index of its own; the OR the query now uses is served
        // by this index and by the existing (state, opened_at) one.
        new(9, "CREATE INDEX IF NOT EXISTS ix_ops_alert_resolved ON ops_alert (state, resolved_at);"),
    ];
}
