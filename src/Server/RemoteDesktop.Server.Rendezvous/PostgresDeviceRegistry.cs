using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace RemoteDesktop.Server.Rendezvous;

/// <summary>
/// PostgreSQL-backed device registry: stores the mapping between device certificate fingerprints and 9-digit device
/// ids persistently. Device codes survive server restarts, enabling reliable unattended access.
/// </summary>
public sealed partial class PostgresDeviceRegistry : IDeviceRegistry
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresDeviceRegistry> _logger;
    private readonly ConcurrentDictionary<string, uint> _cache = new(StringComparer.Ordinal);

    public PostgresDeviceRegistry(string connectionString, ILogger<PostgresDeviceRegistry> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS device_registrations (
                fingerprint VARCHAR(128) PRIMARY KEY,
                device_id BIGINT UNIQUE NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                last_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE INDEX IF NOT EXISTS idx_device_registrations_device_id ON device_registrations(device_id);
            """;

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            LogSchemaVerified(_logger);
        }
        catch (Exception ex)
        {
            LogSchemaFailed(_logger, ex);
            throw;
        }
    }

    public async Task<DeviceRegistration> GetOrAllocateAsync(string fingerprint, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        if (_cache.TryGetValue(fingerprint, out var cachedId))
        {
            return new DeviceRegistration(cachedId, Created: false);
        }

        // 1. Check if the device is already in the database
        await using (var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            const string selectSql = "SELECT device_id FROM device_registrations WHERE fingerprint = @fingerprint;";
            await using var selectCmd = new NpgsqlCommand(selectSql, connection);
            selectCmd.Parameters.AddWithValue("fingerprint", fingerprint);

            var existing = await selectCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (existing is long existingIdLong)
            {
                var existingId = (uint)existingIdLong;
                _cache[fingerprint] = existingId;

                // Update last seen in the background
                _ = UpdateLastSeenAsync(fingerprint);
                return new DeviceRegistration(existingId, Created: false);
            }
        }

        // 2. Allocate a new 9-digit device id and insert with retry on collision
        const int maxAttempts = 10;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var allocatedId = (uint)RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000);

            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                const string insertSql = """
                    INSERT INTO device_registrations (fingerprint, device_id, created_at, last_seen_at)
                    VALUES (@fingerprint, @device_id, NOW(), NOW())
                    ON CONFLICT (fingerprint) DO UPDATE
                        SET last_seen_at = NOW()
                    RETURNING device_id;
                    """;

                await using var insertCmd = new NpgsqlCommand(insertSql, connection);
                insertCmd.Parameters.AddWithValue("fingerprint", fingerprint);
                insertCmd.Parameters.AddWithValue("device_id", (long)allocatedId);

                var result = await insertCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                var finalId = (uint)(long)result!;

                // The upsert returns our own id only when it inserted: an existing row answers with its id instead,
                // which is how a reconnecting device is told apart from a new one without a second query.
                var created = finalId == allocatedId;
                _cache[fingerprint] = finalId;
                LogAllocated(_logger, finalId, fingerprint);
                return new DeviceRegistration(finalId, created);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // Conflict on device_id (rare collision); retry with another random id
                LogCollision(_logger, allocatedId, attempt, maxAttempts);
            }
        }

        throw new InvalidOperationException("Failed to allocate a unique device id after maximum attempts.");
    }

    private async Task UpdateLastSeenAsync(string fingerprint)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync().ConfigureAwait(false);
            const string sql = "UPDATE device_registrations SET last_seen_at = NOW() WHERE fingerprint = @fingerprint;";
            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("fingerprint", fingerprint);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogUpdateLastSeenFailed(_logger, ex, fingerprint);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "PostgreSQL device registry schema verified.")]
    private static partial void LogSchemaVerified(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to initialize PostgreSQL schema for device registry.")]
    private static partial void LogSchemaFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Allocated persistent device id {DeviceId} for fingerprint {Fingerprint}.")]
    private static partial void LogAllocated(ILogger logger, uint deviceId, string fingerprint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Device ID collision for {DeviceId}, retrying (attempt {Attempt}/{MaxAttempts})...")]
    private static partial void LogCollision(ILogger logger, uint deviceId, int attempt, int maxAttempts);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to update last_seen_at for device {Fingerprint}.")]
    private static partial void LogUpdateLastSeenFailed(ILogger logger, Exception ex, string fingerprint);
}
