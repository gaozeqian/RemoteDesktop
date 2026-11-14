namespace RemoteDesktop.Server.Ops.Storage;

/// <summary>
/// The console's tables, written in the intersection of SQLite's and PostgreSQL's type systems so one script creates
/// both.
/// </summary>
/// <remarks>
/// Every timestamp is a BIGINT of Unix milliseconds rather than a native date type. That is a deliberate simplification:
/// the two providers spell date types and their literal formats differently, and the console only ever compares and
/// sorts them. It also removes a whole class of timezone bugs from the ingest path, where the node's clock and the
/// console's are already different things.
///
/// Ids are TEXT UUIDs generated in code. Auto-increment columns are the one place the two providers cannot share a
/// statement, and a UUID is what the event contract already carries.
/// </remarks>
public static class OpsSchema
{
    /// <summary>Statements that create the baseline schema. Safe to run on every start.</summary>
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS ops_schema_version (
            version INTEGER PRIMARY KEY,
            applied_at BIGINT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS ops_node (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL UNIQUE,
            role TEXT NOT NULL,
            base_url TEXT NOT NULL,
            region TEXT,
            enabled INTEGER NOT NULL DEFAULT 1,
            last_scrape_at BIGINT,
            last_scrape_ok INTEGER,
            created_at BIGINT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS ops_metric_catalog (
            metric TEXT PRIMARY KEY,
            kind TEXT NOT NULL,
            unit TEXT
        );

        CREATE TABLE IF NOT EXISTS ops_metric_sample (
            node_id TEXT NOT NULL,
            metric TEXT NOT NULL,
            ts BIGINT NOT NULL,
            value DOUBLE PRECISION NOT NULL,
            PRIMARY KEY (node_id, metric, ts)
        );

        CREATE TABLE IF NOT EXISTS ops_metric_rollup (
            node_id TEXT NOT NULL,
            metric TEXT NOT NULL,
            step_seconds INTEGER NOT NULL,
            bucket BIGINT NOT NULL,
            first_value DOUBLE PRECISION,
            last_value DOUBLE PRECISION,
            min_value DOUBLE PRECISION,
            max_value DOUBLE PRECISION,
            avg_value DOUBLE PRECISION,
            sample_count INTEGER NOT NULL,
            PRIMARY KEY (node_id, metric, step_seconds, bucket)
        );

        CREATE TABLE IF NOT EXISTS ops_event (
            id TEXT PRIMARY KEY,
            event_id TEXT NOT NULL UNIQUE,
            node TEXT NOT NULL,
            kind TEXT NOT NULL,
            occurred_at BIGINT NOT NULL,
            received_at BIGINT NOT NULL,
            correlation_id TEXT,
            payload TEXT NOT NULL,
            schema_version INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS ops_session (
            id TEXT PRIMARY KEY,
            correlation_id TEXT NOT NULL UNIQUE,
            controller_device_id BIGINT,
            host_device_id BIGINT,
            mode TEXT,
            transport TEXT,
            source TEXT NOT NULL,
            relay_node_id TEXT,
            started_at BIGINT,
            ended_at BIGINT,
            end_reason TEXT,
            bytes_in BIGINT,
            bytes_out BIGINT,
            rtt_ms DOUBLE PRECISION,
            loss_pct DOUBLE PRECISION,
            updated_at BIGINT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS ops_alert_rule (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            kind TEXT NOT NULL,
            metric TEXT,
            numerator TEXT,
            denominator TEXT,
            aggregation TEXT NOT NULL,
            comparator TEXT NOT NULL,
            threshold DOUBLE PRECISION NOT NULL,
            window_seconds INTEGER NOT NULL,
            for_seconds INTEGER NOT NULL,
            severity TEXT NOT NULL,
            enabled INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS ops_alert (
            id TEXT PRIMARY KEY,
            rule_id TEXT NOT NULL,
            severity TEXT NOT NULL,
            dedupe_key TEXT NOT NULL,
            state TEXT NOT NULL,
            value DOUBLE PRECISION,
            labels_json TEXT NOT NULL,
            opened_at BIGINT NOT NULL,
            acked_at BIGINT,
            acked_by TEXT,
            resolved_at BIGINT
        );

        CREATE TABLE IF NOT EXISTS ops_notification_channel (
            id TEXT PRIMARY KEY,
            kind TEXT NOT NULL,
            target TEXT NOT NULL,
            secret TEXT,
            enabled INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS ops_notification_attempt (
            alert_id TEXT NOT NULL,
            channel_id TEXT NOT NULL,
            transition TEXT NOT NULL,
            attempts INTEGER NOT NULL,
            last_error TEXT,
            sent_at BIGINT,
            PRIMARY KEY (alert_id, channel_id, transition)
        );

        CREATE TABLE IF NOT EXISTS ops_silence (
            id TEXT PRIMARY KEY,
            matcher_json TEXT NOT NULL,
            starts_at BIGINT NOT NULL,
            ends_at BIGINT NOT NULL,
            created_by TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS ops_admin_user (
            id TEXT PRIMARY KEY,
            username TEXT NOT NULL,
            username_key TEXT NOT NULL UNIQUE,
            password_hash TEXT NOT NULL,
            totp_secret TEXT NOT NULL,
            role TEXT NOT NULL,
            disabled INTEGER NOT NULL DEFAULT 0,
            created_at BIGINT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS ops_admin_session (
            id TEXT PRIMARY KEY,
            user_id TEXT NOT NULL,
            token_hash TEXT NOT NULL UNIQUE,
            issued_at BIGINT NOT NULL,
            expires_at BIGINT NOT NULL,
            ip TEXT,
            user_agent TEXT
        );

        CREATE TABLE IF NOT EXISTS ops_audit_log (
            id TEXT PRIMARY KEY,
            ts BIGINT NOT NULL,
            actor TEXT NOT NULL,
            action TEXT NOT NULL,
            target TEXT NOT NULL,
            detail_json TEXT NOT NULL,
            ip TEXT NOT NULL,
            result TEXT NOT NULL,
            prev_hash TEXT NOT NULL,
            hash TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS ops_lease (
            name TEXT PRIMARY KEY,
            holder TEXT NOT NULL,
            expires_at BIGINT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_ops_metric_sample_metric_ts ON ops_metric_sample (metric, ts);
        CREATE INDEX IF NOT EXISTS ix_ops_event_correlation ON ops_event (correlation_id, occurred_at);
        CREATE INDEX IF NOT EXISTS ix_ops_session_open ON ops_session (ended_at, started_at);
        CREATE INDEX IF NOT EXISTS ix_ops_alert_state ON ops_alert (state, opened_at);
        CREATE INDEX IF NOT EXISTS ix_ops_audit_ts ON ops_audit_log (ts);
        CREATE INDEX IF NOT EXISTS ix_ops_admin_session_expiry ON ops_admin_session (expires_at);
        """;

    /// <summary>The schema version this build writes. Only ever increases.</summary>
    public const int Version = 9;
}
