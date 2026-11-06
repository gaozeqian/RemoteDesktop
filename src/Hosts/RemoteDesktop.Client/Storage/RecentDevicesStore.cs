using System.Text;
using System.Text.Json;
using RemoteDesktop.Abstractions.Platform;

namespace RemoteDesktop.Client.Storage;

public interface IRecentDevicesStore
{
    /// <summary>Devices the user has connected to, most recent first. Removed devices are filtered out.</summary>
    Task<IReadOnlyList<RecentDeviceEntry>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Records a connection: adds the device or refreshes when it was last seen and, on revival, brings it back.</summary>
    Task SaveOrUpdateAsync(string deviceId, string? alias = null, string? password = null, CancellationToken cancellationToken = default);

    Task UpdateAliasAsync(string deviceId, string? alias, CancellationToken cancellationToken = default);

    /// <summary>The password remembered for this device, or null. It never leaves this machine.</summary>
    Task<string?> GetPasswordAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>Remembers the password for a device, or forgets it when <paramref name="password"/> is null or empty.</summary>
    Task UpdatePasswordAsync(string deviceId, string? password, CancellationToken cancellationToken = default);

    /// <summary>Removes the device and forgets its password.</summary>
    Task RemoveAsync(string deviceId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The local device list, kept as versioned JSON in the data directory.
/// </summary>
/// <remarks>
/// Passwords are not part of it: they live in the <see cref="ISecretStore"/>, which is per machine and per user and
/// is kept apart from the list file on purpose — see <c>docs/design/README.md</c> ("密码为什么不进列表"). Removal
/// leaves a tombstone rather than deleting the entry, so a removed device can come back when it is connected to again.
/// </remarks>
public sealed class RecentDevicesStore : IRecentDevicesStore, IDisposable
{
    private const int CurrentVersion = 1;
    private const string FileName = "recent_devices.json";
    private const int ReadAttempts = 3;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(50);

    private readonly string _filePath;
    private readonly string _secretPrefix;
    private readonly ISecretStore _secrets;
    private readonly SemaphoreSlim _lock = new(1, 1);
    // Case-insensitive so a hand-edited file, or one written before the format was versioned, still reads.
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private bool _disposed;

    /// <param name="accountId">
    /// Null for the list that belongs to this machine alone. A signed-in account passes its id here to get its own
    /// list — and its own remembered passwords — under <c>accounts/&lt;id&gt;/</c>, so that switching accounts on one
    /// Windows account cannot mix the two lists.
    /// </param>
    public RecentDevicesStore(string dataDirectory, ISecretStore secretStore, string? accountId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(secretStore);
        _secrets = secretStore;

        if (string.IsNullOrWhiteSpace(accountId))
        {
            _filePath = Path.Combine(dataDirectory, FileName);
            _secretPrefix = "recent-device";
        }
        else
        {
            // Account ids will come from the server: keep anything that is not a plain name out of the file path.
            if (accountId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || accountId.Contains("..", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Invalid account id '{accountId}'.", nameof(accountId));
            }

            _filePath = Path.Combine(dataDirectory, "accounts", accountId, FileName);
            _secretPrefix = $"recent-device-{accountId}";
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _lock.Dispose();
        }
    }

    public async Task<IReadOnlyList<RecentDeviceEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = await LoadInternalAsync(cancellationToken).ConfigureAwait(false);
            return list.Where(entry => entry.DeletedAtUtc is null).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveOrUpdateAsync(string deviceId, string? alias = null, string? password = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = await LoadInternalAsync(cancellationToken).ConfigureAwait(false);
            var normalizedId = NormalizeId(deviceId);
            var index = list.FindIndex(d => NormalizeId(d.DeviceId) == normalizedId);
            var now = DateTimeOffset.UtcNow;

            if (index >= 0)
            {
                var existing = list[index];
                var resolvedAlias = alias ?? existing.Alias;
                var revived = existing.DeletedAtUtc is not null;

                list[index] = existing with
                {
                    Alias = resolvedAlias,
                    LastConnectedUtc = now,
                    // Connecting is an observation, not an edit: only an alias change or a revival moves the edit stamp.
                    UpdatedAtUtc = revived || !string.Equals(resolvedAlias, existing.Alias, StringComparison.Ordinal)
                        ? now
                        : existing.UpdatedAtUtc,
                    DeletedAtUtc = null,
                };
            }
            else
            {
                list.Add(new RecentDeviceEntry
                {
                    DeviceId = deviceId,
                    Alias = alias,
                    LastConnectedUtc = now,
                    UpdatedAtUtc = now,
                });
            }

            list.Sort((a, b) => b.LastConnectedUtc.CompareTo(a.LastConnectedUtc));
            await SaveInternalAsync(list, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(password))
            {
                _secrets.Save(SecretName(normalizedId), Encoding.UTF8.GetBytes(password));
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateAliasAsync(string deviceId, string? alias, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = await LoadInternalAsync(cancellationToken).ConfigureAwait(false);
            var normalizedId = NormalizeId(deviceId);
            // A removed device is not editable: it is only waiting for the tombstone to be merged away.
            var index = list.FindIndex(d => NormalizeId(d.DeviceId) == normalizedId && d.DeletedAtUtc is null);
            if (index >= 0)
            {
                list[index] = list[index] with { Alias = alias, UpdatedAtUtc = DateTimeOffset.UtcNow };
                await SaveInternalAsync(list, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string?> GetPasswordAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var saved = _secrets.Load(SecretName(NormalizeId(deviceId)));
            return saved is null ? null : Encoding.UTF8.GetString(saved);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdatePasswordAsync(string deviceId, string? password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var name = SecretName(NormalizeId(deviceId));
            if (string.IsNullOrEmpty(password))
            {
                _secrets.Delete(name);
            }
            else
            {
                _secrets.Save(name, Encoding.UTF8.GetBytes(password));
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = await LoadInternalAsync(cancellationToken).ConfigureAwait(false);
            var normalizedId = NormalizeId(deviceId);
            var index = list.FindIndex(d => NormalizeId(d.DeviceId) == normalizedId);
            if (index >= 0)
            {
                if (list[index].DeletedAtUtc is null)
                {
                    var now = DateTimeOffset.UtcNow;
                    list[index] = list[index] with { DeletedAtUtc = now, UpdatedAtUtc = now };
                    await SaveInternalAsync(list, cancellationToken).ConfigureAwait(false);
                }

                // The tombstone is bookkeeping; the password is gone for good.
                _secrets.Delete(SecretName(normalizedId));
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Reads every entry, tombstones included. A file written before the format was versioned is a bare array and is
    /// migrated on the way in.
    /// </summary>
    private async Task<List<RecentDeviceEntry>> LoadInternalAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        JsonDocument document;
        try
        {
            document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            PreserveUnreadableFile();
            return [];
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Gone since the existence check: there is nothing on disk that a save could destroy.
            return [];
        }

        // Any other IOException or UnauthorizedAccessException escapes from ReadDocumentAsync on purpose. A file that
        // exists but cannot be read right now (locked by a scanner or a second instance) must not be treated as an
        // empty list: the next save would replace the whole thing with one entry.
        using (document)
        {
            try
            {
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    return await MigrateLegacyAsync(root, cancellationToken).ConfigureAwait(false);
                }

                if (root.ValueKind != JsonValueKind.Object)
                {
                    // Not a shape this store ever wrote. Treat it like any other unreadable file rather than letting
                    // the next save overwrite it.
                    PreserveUnreadableFile();
                    return [];
                }

                return ReadCurrentFile(root);
            }
            catch (JsonException)
            {
                // Valid JSON that is not a valid list (an entry missing a required field, a wrongly typed value).
                // Skipping the bad entries would silently drop them on the next save, so set the whole file aside.
                PreserveUnreadableFile();
                return [];
            }
        }
    }

    /// <summary>
    /// Parses the file. A transient lock is retried a couple of times, briefly; if it persists the IOException or
    /// UnauthorizedAccessException is allowed to escape.
    /// </summary>
    private async Task<JsonDocument> ReadDocumentAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // Closed again before anything else runs: migrating a legacy file rewrites it, and that cannot happen
                // while this read handle is still on it.
                await using var stream = File.OpenRead(_filePath);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                && ex is not (FileNotFoundException or DirectoryNotFoundException)
                && attempt < ReadAttempts)
            {
                await Task.Delay(ReadRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private List<RecentDeviceEntry> ReadCurrentFile(JsonElement root)
    {
        if (!root.TryGetProperty(nameof(RecentDevicesFile.Version), out var version) || !version.TryGetInt32(out var value) || value > CurrentVersion)
        {
            // Written by a newer client, or not by us at all: do not overwrite it with what this one can read.
            PreserveUnreadableFile();
            return [];
        }

        if (!root.TryGetProperty(nameof(RecentDevicesFile.Devices), out var devices) || devices.ValueKind != JsonValueKind.Array)
        {
            // The file says it is ours but has no list in it. Keeping it lets a person recover whatever is left;
            // returning an empty list would let the next save replace it without a word.
            PreserveUnreadableFile();
            return [];
        }

        var list = new List<RecentDeviceEntry>();
        foreach (var element in devices.EnumerateArray())
        {
            if (element.Deserialize<RecentDeviceEntry>(JsonOptions) is not { DeviceId.Length: > 0 } entry)
            {
                // One unreadable entry makes the whole file unreadable: skipping it would drop it on the next save,
                // which is the silent loss this store promises not to do.
                PreserveUnreadableFile();
                return [];
            }

            list.Add(entry);
        }

        return list;
    }

    /// <summary>
    /// Reads the pre-version file, whose entries could still carry a plaintext password. Those move into the secret
    /// store and the file is rewritten without them right away, so upgrading takes the plaintext off the disk instead
    /// of leaving it there until the user happens to change something.
    /// </summary>
    private async Task<List<RecentDeviceEntry>> MigrateLegacyAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var list = new List<RecentDeviceEntry>();
        var movedSecrets = false;

        foreach (var element in root.EnumerateArray())
        {
            if (element.Deserialize<LegacyEntry>(JsonOptions) is not { DeviceId.Length: > 0 } legacy)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(legacy.SavedPassword))
            {
                _secrets.Save(SecretName(NormalizeId(legacy.DeviceId)), Encoding.UTF8.GetBytes(legacy.SavedPassword));
                movedSecrets = true;
            }

            list.Add(new RecentDeviceEntry
            {
                DeviceId = legacy.DeviceId,
                Alias = legacy.Alias,
                LastConnectedUtc = legacy.LastConnectedUtc,
                // The old file had no edit stamp; the connection time is the best account of it we have.
                UpdatedAtUtc = legacy.LastConnectedUtc,
            });
        }

        if (movedSecrets)
        {
            await SaveInternalAsync(list, cancellationToken).ConfigureAwait(false);
        }

        return list;
    }

    private async Task SaveInternalAsync(List<RecentDeviceEntry> list, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Write beside the target and swap it in: a crash part-way through can no longer leave a truncated list
        // behind, which the next run would otherwise read as "no devices" and then overwrite.
        var tempPath = _filePath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                new RecentDevicesFile { Version = CurrentVersion, Devices = list },
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, _filePath, overwrite: true);
    }

    /// <summary>
    /// Moves a file we could not read out of the way before anything can overwrite it. The list can still be
    /// recovered from the copy; the next save would have destroyed it without a word.
    /// </summary>
    private void PreserveUnreadableFile()
    {
        try
        {
            File.Move(_filePath, $"{_filePath}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked or read-only: leave it exactly as it is rather than risk losing it.
        }
    }

    private static string NormalizeId(string id) => id.Replace(" ", string.Empty, StringComparison.Ordinal);

    private string SecretName(string normalizedId) => $"{_secretPrefix}-{normalizedId}";

    /// <summary>The versioned file: <c>{ "Version": 1, "Devices": [ … ] }</c>.</summary>
    private sealed record RecentDevicesFile
    {
        public int Version { get; init; }

        public List<RecentDeviceEntry> Devices { get; init; } = [];
    }

    /// <summary>The pre-version format — a bare array, whose entries could still carry a plaintext password.</summary>
    private sealed record LegacyEntry
    {
        public string? DeviceId { get; init; }

        public string? Alias { get; init; }

        public string? SavedPassword { get; init; }

        public DateTimeOffset LastConnectedUtc { get; init; }
    }
}
