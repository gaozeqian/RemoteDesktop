using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace RemoteDesktop.Server.Storage;

/// <summary>
/// The device registry over any relational database: the mapping between a device certificate fingerprint and a
/// 9-digit device id, kept in a <c>device_registrations</c> table so that the id survives a server restart.
/// </summary>
/// <remarks>
/// One implementation, one dialect per database. Everything that is worth testing carefully — the cache, the random
/// id, the retry when two devices draw the same one, and the difference between "this call created the id" and "the
/// device came back" — is here, so a new provider only has to say how its database spells four statements.
/// </remarks>
public abstract partial class RelationalDeviceRegistry : IDeviceRegistryStore, IDeviceCatalog, IDisposable
{
    private const int MaxAllocationAttempts = 10;
    private const uint LowestDeviceId = 100_000_000;

    /// <summary>The shortest gap between two writes of one device's <c>last_seen_at</c>.</summary>
    private static readonly TimeSpan TouchInterval = TimeSpan.FromSeconds(60);

    /// <summary>Exclusive: ids are nine digits, so the last one is 999 999 999.</summary>
    private const uint HighestDeviceIdExclusive = 1_000_000_000;

    /// <summary>
    /// A row from the table, not <c>SELECT 1</c>: an empty database answers either way, and "the table is gone" — the
    /// file was deleted, the volume is not mounted — is exactly what the health check has to notice.
    /// </summary>
    private const string ProbeSql = "SELECT 1 FROM device_registrations LIMIT 1;";

    private readonly DbDataSource _dataSource;
    private readonly IDeviceRegistryDialect _dialect;
    private readonly ILogger _logger;
    private readonly Func<uint> _allocateId;
    private readonly ConcurrentDictionary<string, uint> _cache = new(StringComparer.Ordinal);

    /// <summary>When (Unix milliseconds) each fingerprint's <c>last_seen_at</c> was last written, to throttle the touches.</summary>
    private readonly ConcurrentDictionary<string, long> _lastTouched = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    private protected RelationalDeviceRegistry(
        DbDataSource dataSource,
        IDeviceRegistryDialect dialect,
        ILogger logger,
        Func<uint>? allocateId = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(logger);

        _dataSource = dataSource;
        _dialect = dialect;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _allocateId = allocateId ?? (() => (uint)RandomNumberGenerator.GetInt32((int)LowestDeviceId, (int)HighestDeviceIdExclusive));
    }

    /// <summary>
    /// Releases the connection pool and the write lock. Called by the container at shutdown.
    /// </summary>
    /// <remarks>
    /// A <c>last_seen</c> update that is still in flight loses its race with this and is dropped — it is best effort
    /// by construction, and the process is going away either way.
    /// </remarks>
    public virtual void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _writeLock.Dispose();
            _dataSource.Dispose();
        }

        _disposed = true;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            await using (var create = connection.CreateCommand())
            {
                create.CommandText = _dialect.CreateSchemaSql;
                await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // The revoked_at column postdates the first release, so a live deployment has the table without it. The
            // ALTER is dialect-specific in whether it is idempotent on its own; the guard is what makes both safe.
            try
            {
                await using var migrate = connection.CreateCommand();
                migrate.CommandText = _dialect.AddRevokedAtSql;
                await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (_dialect.IsDuplicateColumn(ex))
            {
                // Already there from an earlier start.
            }

            LogSchemaVerified(_logger);
        }
        catch (Exception ex)
        {
            LogSchemaFailed(_logger, ex);
            throw;
        }
    }

    public async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = ProbeSql;
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A page of devices, by id. The timestamps are read as whatever the provider hands back and converted here,
    /// because the two providers disagree about how a timestamp is represented and the console only wants a number.
    /// </summary>
    public async Task<IReadOnlyList<DeviceRecord>> ListAsync(
        uint afterDeviceId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT device_id, created_at, last_seen_at, revoked_at FROM device_registrations " +
            "WHERE device_id > @after ORDER BY device_id LIMIT @limit;";
        AddParameter(command, "@after", (long)afterDeviceId);
        AddParameter(command, "@limit", Math.Clamp(limit, 1, 500));

        var records = new List<DeviceRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new DeviceRecord(
                (uint)reader.GetInt64(0),
                ToUnixMilliseconds(reader.GetValue(1)),
                ToUnixMilliseconds(reader.GetValue(2)),
                !reader.IsDBNull(3)));
        }

        return records;
    }

    public async Task<bool> IsRevokedAsync(uint deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = _dialect.IsRevokedSql;
        AddParameter(command, "@device_id", (long)deviceId);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value switch
        {
            true => true,
            false or null or DBNull => false,
            long number => number != 0,
            int number => number != 0,
            _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
        };
    }

    public Task SetRevokedAsync(uint deviceId, bool revoked, CancellationToken cancellationToken = default) =>
        WithWriteLockAsync(
            async () =>
            {
                await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = _dialect.SetRevokedSql;
                AddParameter(command, "@device_id", (long)deviceId);
                AddParameter(command, "@revoked", revoked ? DateTimeOffset.UtcNow : DBNull.Value);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    private static long ToUnixMilliseconds(object value) => value switch
    {
        DateTimeOffset offset => offset.ToUnixTimeMilliseconds(),
        DateTime dateTime => new DateTimeOffset(
            dateTime.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc) : dateTime)
            .ToUnixTimeMilliseconds(),
        string text => DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed) ? parsed.ToUnixTimeMilliseconds() : 0,
        _ => 0,
    };

    public async Task<DeviceRegistration> GetOrAllocateAsync(string fingerprint, CancellationToken cancellationToken = default)    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        if (_cache.TryGetValue(fingerprint, out var cached))
        {
            // A device that reconnects is seen again: last_seen_at is "the last registration", and the cache must not
            // freeze it at the first one. Throttled per fingerprint, see Touch.
            Touch(fingerprint);
            return new DeviceRegistration(cached, Created: false);
        }

        // 1. The device may already have an id from an earlier run — this is the reconnect after a restart.
        if (await SelectDeviceIdAsync(fingerprint, cancellationToken).ConfigureAwait(false) is { } known)
        {
            _cache[fingerprint] = known;
            Touch(fingerprint);
            return new DeviceRegistration(known, Created: false);
        }

        // 2. Allocate a new one, retrying only when the draw collides with another device's id.
        for (var attempt = 1; attempt <= MaxAllocationAttempts; attempt++)
        {
            var allocated = _allocateId();

            try
            {
                if (await InsertAsync(fingerprint, allocated, cancellationToken).ConfigureAwait(false))
                {
                    _cache[fingerprint] = allocated;
                    LogAllocated(_logger, allocated, fingerprint);
                    return new DeviceRegistration(allocated, Created: true);
                }

                // Nothing was inserted because the fingerprint is already there: another connection registered it
                // between the select above and this insert. Its id is the one that counts, and the row exists now,
                // so reading it back is the answer rather than another attempt.
                if (await SelectDeviceIdAsync(fingerprint, cancellationToken).ConfigureAwait(false) is { } raced)
                {
                    _cache[fingerprint] = raced;
                    return new DeviceRegistration(raced, Created: false);
                }
            }
            catch (Exception ex) when (_dialect.IsUniqueViolation(ex))
            {
                // The device id was taken by somebody else. Rare enough to be worth another draw rather than a query.
                LogCollision(_logger, allocated, attempt, MaxAllocationAttempts);
            }
        }

        throw new InvalidOperationException("Failed to allocate a unique device id after maximum attempts.");
    }

    private async Task<uint?> SelectDeviceIdAsync(string fingerprint, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = _dialect.SelectDeviceIdSql;
        AddParameter(command, "@fingerprint", fingerprint);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value switch
        {
            null or DBNull => null,
            long id => (uint)id,
            int id => (uint)id,
            _ => Convert.ToUInt32(value, CultureInfo.InvariantCulture),
        };
    }

    private Task<bool> InsertAsync(string fingerprint, uint deviceId, CancellationToken cancellationToken) =>
        WithWriteLockAsync(
            async () =>
            {
                await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = _dialect.InsertIfAbsentSql;
                AddParameter(command, "@fingerprint", fingerprint);
                AddParameter(command, "@device_id", (long)deviceId);
                var now = _time.GetUtcNow();
                AddParameter(command, "@now", now);

                var inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
                if (inserted)
                {
                    // The row was created with this timestamp, so a reconnect straight afterwards has nothing to refresh.
                    _lastTouched[fingerprint] = now.ToUnixTimeMilliseconds();
                }

                return inserted;
            },
            cancellationToken);

    /// <summary>
    /// Refreshes <c>last_seen_at</c> without making the caller wait, and without ever queueing.
    /// </summary>
    /// <remarks>
    /// The timestamp feeds the console's device page, which is why it is refreshed at all — but it is still best effort:
    /// it must not become a backlog. On a store with a single writer, a burst of reconnects would otherwise leave one
    /// touch per reconnect waiting behind the writer; here the touch is simply skipped when the writer is busy. A store
    /// without that lock (PostgreSQL) would instead start one unbounded background write per reconnect, so a
    /// fingerprint is written at most once per <see cref="TouchInterval"/>: the device page does not need finer
    /// resolution than that, and a reconnect storm costs one write per device rather than one per connection.
    /// </remarks>
    private void Touch(string fingerprint)
    {
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        if (!ReserveTouch(fingerprint, now))
        {
            return;
        }

        var holdsLock = false;
        if (_dialect.SerializeWrites)
        {
            if (!_writeLock.Wait(0))
            {
                // Not written, so the next reconnect may try again instead of waiting out the interval.
                _lastTouched.TryRemove(new KeyValuePair<string, long>(fingerprint, now));
                return;
            }

            holdsLock = true;
        }

        _ = TouchAsync(fingerprint, holdsLock);
    }

    /// <summary>Claims the right to write this fingerprint's timestamp, false when it was written recently enough.</summary>
    private bool ReserveTouch(string fingerprint, long now)
    {
        while (true)
        {
            if (!_lastTouched.TryGetValue(fingerprint, out var last))
            {
                if (_lastTouched.TryAdd(fingerprint, now))
                {
                    return true;
                }

                continue;
            }

            if (now - last < TouchInterval.TotalMilliseconds)
            {
                return false;
            }

            if (_lastTouched.TryUpdate(fingerprint, now, last))
            {
                return true;
            }
        }
    }

    private async Task TouchAsync(string fingerprint, bool holdsLock)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = _dialect.TouchLastSeenSql;
            AddParameter(command, "@fingerprint", fingerprint);
            AddParameter(command, "@now", _time.GetUtcNow());
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogTouchFailed(_logger, ex, fingerprint);
        }
        finally
        {
            if (holdsLock)
            {
                _writeLock.Release();
            }
        }
    }

    private async Task<T> WithWriteLockAsync<T>(Func<Task<T>> write, CancellationToken cancellationToken)
    {
        if (!_dialect.SerializeWrites)
        {
            return await write().ConfigureAwait(false);
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await write().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Serialises a write that has nothing to report, without going through the generic overload.</summary>
    private async Task WithWriteLockAsync(Func<Task> write, CancellationToken cancellationToken)
    {
        if (!_dialect.SerializeWrites)
        {
            await write().ConfigureAwait(false);
            return;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await write().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Device registry schema verified.")]
    private static partial void LogSchemaVerified(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to initialize the device registry schema.")]
    private static partial void LogSchemaFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Allocated persistent device id {DeviceId} for fingerprint {Fingerprint}.")]
    private static partial void LogAllocated(ILogger logger, uint deviceId, string fingerprint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Device ID collision for {DeviceId}, retrying (attempt {Attempt}/{MaxAttempts})...")]
    private static partial void LogCollision(ILogger logger, uint deviceId, int attempt, int maxAttempts);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to update last_seen_at for device {Fingerprint}.")]
    private static partial void LogTouchFailed(ILogger logger, Exception ex, string fingerprint);
}
