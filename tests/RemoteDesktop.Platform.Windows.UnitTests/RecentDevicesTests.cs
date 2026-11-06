using System.Text.Json;
using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Client.Storage;
using RemoteDesktop.Client.ViewModels;

namespace RemoteDesktop.Platform.Windows.UnitTests;

public class RecentDevicesTests
{
    [Fact]
    public async Task RecentDevicesStore_saves_and_loads_entries_ordered_by_time()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rd_store_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var secrets = new MemorySecrets();
            using var store = new RecentDevicesStore(tempDir, secrets);
            var ct = TestContext.Current.CancellationToken;

            await store.SaveOrUpdateAsync("111 222 333", alias: "Dev1", password: "pwd1", cancellationToken: ct);
            await Task.Delay(20, ct);
            await store.SaveOrUpdateAsync("444 555 666", alias: "Dev2", password: "pwd2", cancellationToken: ct);

            var list = await store.GetAllAsync(ct);
            Assert.Equal(2, list.Count);
            // Most recent first
            Assert.Equal("444 555 666", list[0].DeviceId);
            Assert.Equal("Dev2", list[0].Alias);
            Assert.Equal("111 222 333", list[1].DeviceId);
            Assert.Equal("Dev1", list[1].Alias);

            // Passwords come back, but from the secret store rather than from the list file.
            Assert.Equal("pwd2", await store.GetPasswordAsync("444555666", ct));
            var file = await File.ReadAllTextAsync(Path.Combine(tempDir, "recent_devices.json"), ct);
            Assert.DoesNotContain("pwd1", file, StringComparison.Ordinal);
            Assert.DoesNotContain("pwd2", file, StringComparison.Ordinal);
            Assert.DoesNotContain("Password", file, StringComparison.OrdinalIgnoreCase);

            // Update alias
            await store.UpdateAliasAsync("111222333", "NewDev1", ct);
            var updatedList = await store.GetAllAsync(ct);
            var dev1 = updatedList.FirstOrDefault(d => d.DeviceId == "111 222 333");
            Assert.NotNull(dev1);
            Assert.Equal("NewDev1", dev1.Alias);

            // Remove device
            await store.RemoveAsync("444555666", ct);
            var remaining = await store.GetAllAsync(ct);
            Assert.Single(remaining);
            Assert.Equal("111 222 333", remaining[0].DeviceId);
            Assert.Null(await store.GetPasswordAsync("444 555 666", ct));
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task RecentDevicesStore_keeps_a_removed_device_as_a_tombstone_and_revives_it_on_reconnect()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rd_store_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            using var store = new RecentDevicesStore(tempDir, new MemorySecrets());
            var ct = TestContext.Current.CancellationToken;

            await store.SaveOrUpdateAsync("111 222 333", alias: "Dev1", cancellationToken: ct);
            var added = (await store.GetAllAsync(ct))[0];
            await store.RemoveAsync("111 222 333", ct);

            Assert.Empty(await store.GetAllAsync(ct));

            // The entry is still on disk as a tombstone, with an edit stamp newer than the entry it removes, so a
            // future sync of another machine's copy cannot bring the device back.
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(tempDir, "recent_devices.json"), ct));
            var tombstone = document.RootElement.GetProperty("Devices")[0];
            Assert.True(tombstone.GetProperty("DeletedAtUtc").ValueKind is not JsonValueKind.Null);
            Assert.True(tombstone.GetProperty("UpdatedAtUtc").GetDateTimeOffset() >= added.UpdatedAtUtc);

            // Connecting again revives it rather than leaving it invisible for good.
            await store.SaveOrUpdateAsync("111222333", alias: "Dev1", cancellationToken: ct);
            var revived = Assert.Single(await store.GetAllAsync(ct));
            Assert.Null(revived.DeletedAtUtc);
            Assert.Equal("Dev1", revived.Alias);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task RecentDevicesStore_migrates_a_legacy_file_and_takes_the_plaintext_password_off_disk()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rd_store_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var file = Path.Combine(tempDir, "recent_devices.json");
            var lastConnected = DateTimeOffset.UtcNow.AddDays(-1);
            await File.WriteAllTextAsync(file, $$"""
                [
                  {
                    "DeviceId": "111 222 333",
                    "Alias": "Dev1",
                    "SavedPassword": "pwd1",
                    "LastConnectedUtc": "{{lastConnected:O}}"
                  }
                ]
                """, TestContext.Current.CancellationToken);

            var secrets = new MemorySecrets();
            using var store = new RecentDevicesStore(tempDir, secrets);
            var ct = TestContext.Current.CancellationToken;

            var entry = Assert.Single(await store.GetAllAsync(ct));
            Assert.Equal("111 222 333", entry.DeviceId);
            Assert.Equal("Dev1", entry.Alias);
            Assert.Equal(lastConnected, entry.LastConnectedUtc);
            Assert.Equal(lastConnected, entry.UpdatedAtUtc);
            Assert.Equal("pwd1", await store.GetPasswordAsync("111 222 333", ct));

            // Reading the old file rewrites it without the password instead of leaving the plaintext there.
            var rewritten = await File.ReadAllTextAsync(file, ct);
            Assert.DoesNotContain("pwd1", rewritten, StringComparison.Ordinal);
            Assert.Equal(1, JsonDocument.Parse(rewritten).RootElement.GetProperty("Version").GetInt32());
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task RecentDevicesStore_sets_aside_a_file_it_cannot_read_instead_of_overwriting_it()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rd_store_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var file = Path.Combine(tempDir, "recent_devices.json");
            await File.WriteAllTextAsync(file, """{ "version": 1, "devices": [ """, TestContext.Current.CancellationToken);

            using var store = new RecentDevicesStore(tempDir, new MemorySecrets());
            var ct = TestContext.Current.CancellationToken;

            Assert.Empty(await store.GetAllAsync(ct));

            // The broken file is preserved, so a list that only failed to parse can still be recovered by hand.
            var preserved = Directory.GetFiles(tempDir, "recent_devices.json.corrupt-*");
            Assert.Single(preserved);

            await store.SaveOrUpdateAsync("111 222 333", cancellationToken: ct);
            Assert.Single(await store.GetAllAsync(ct));
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task RecentDevicesStore_does_not_replace_the_list_when_the_file_cannot_be_opened()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rd_store_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var file = Path.Combine(tempDir, "recent_devices.json");
            using var store = new RecentDevicesStore(tempDir, new MemorySecrets());
            var ct = TestContext.Current.CancellationToken;
            await store.SaveOrUpdateAsync("111 222 333", alias: "Dev1", cancellationToken: ct);
            var before = await File.ReadAllBytesAsync(file, ct);

            // Another process holding the file open without sharing read access: reading it fails with a sharing
            // violation, while replacing it (FileShare.Delete) would still go through if the store pressed on.
            await using (var holder = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Delete))
            {
                await Assert.ThrowsAnyAsync<IOException>(() => store.GetAllAsync(ct));
                await Assert.ThrowsAnyAsync<IOException>(() => store.SaveOrUpdateAsync("444 555 666", alias: "Dev2", cancellationToken: ct));
            }

            // Nothing was written over the list, and nothing was set aside as corrupt either.
            Assert.Equal(before, await File.ReadAllBytesAsync(file, ct));
            Assert.Empty(Directory.GetFiles(tempDir, "recent_devices.json.corrupt-*"));
            Assert.Equal("111 222 333", Assert.Single(await store.GetAllAsync(ct)).DeviceId);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task RecentDevicesStore_sets_aside_a_file_whose_entries_do_not_deserialize()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rd_store_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var file = Path.Combine(tempDir, "recent_devices.json");
            // Valid JSON, current version, but the entry lacks the required UpdatedAtUtc.
            await File.WriteAllTextAsync(file, """
                { "Version": 1, "Devices": [ { "DeviceId": "111 222 333", "Alias": "Dev1", "LastConnectedUtc": "2026-01-01T00:00:00Z" } ] }
                """, TestContext.Current.CancellationToken);

            using var store = new RecentDevicesStore(tempDir, new MemorySecrets());
            var ct = TestContext.Current.CancellationToken;

            Assert.Empty(await store.GetAllAsync(ct));
            Assert.Single(Directory.GetFiles(tempDir, "recent_devices.json.corrupt-*"));

            // The store works again instead of failing on every read and every save.
            await store.SaveOrUpdateAsync("444 555 666", cancellationToken: ct);
            Assert.Equal("444 555 666", Assert.Single(await store.GetAllAsync(ct)).DeviceId);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task RecentDevicesStore_sets_aside_a_file_without_a_devices_array()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rd_store_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var file = Path.Combine(tempDir, "recent_devices.json");
            // Ours by version, but the list itself is missing: a save would otherwise replace it with an empty list.
            await File.WriteAllTextAsync(file, """{ "Version": 1 }""", TestContext.Current.CancellationToken);

            using var store = new RecentDevicesStore(tempDir, new MemorySecrets());
            var ct = TestContext.Current.CancellationToken;

            Assert.Empty(await store.GetAllAsync(ct));
            Assert.Single(Directory.GetFiles(tempDir, "recent_devices.json.corrupt-*"));

            await store.SaveOrUpdateAsync("444 555 666", cancellationToken: ct);
            Assert.Equal("444 555 666", Assert.Single(await store.GetAllAsync(ct)).DeviceId);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task RecentDevicesStore_sets_aside_a_file_with_an_entry_that_has_no_device_id()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rd_store_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var file = Path.Combine(tempDir, "recent_devices.json");
            // Deserializes, but the id is empty: skipping the entry would silently drop it on the next save.
            await File.WriteAllTextAsync(file, """
                { "Version": 1, "Devices": [ { "DeviceId": "", "Alias": "Dev1", "LastConnectedUtc": "2026-01-01T00:00:00Z", "UpdatedAtUtc": "2026-01-01T00:00:00Z" } ] }
                """, TestContext.Current.CancellationToken);

            using var store = new RecentDevicesStore(tempDir, new MemorySecrets());
            var ct = TestContext.Current.CancellationToken;

            Assert.Empty(await store.GetAllAsync(ct));
            Assert.Single(Directory.GetFiles(tempDir, "recent_devices.json.corrupt-*"));
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task RecentDevicesStore_forgets_the_password_but_keeps_the_device_when_it_is_cleared()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rd_store_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            using var store = new RecentDevicesStore(tempDir, new MemorySecrets());
            var ct = TestContext.Current.CancellationToken;
            await store.SaveOrUpdateAsync("111 222 333", alias: "Dev1", password: "pwd1", cancellationToken: ct);

            await store.UpdatePasswordAsync("111 222 333", null, ct);
            Assert.Null(await store.GetPasswordAsync("111 222 333", ct));
            Assert.Equal("Dev1", Assert.Single(await store.GetAllAsync(ct)).Alias);

            await store.UpdatePasswordAsync("111 222 333", "pwd2", ct);
            Assert.Equal("pwd2", await store.GetPasswordAsync("111 222 333", ct));

            await store.UpdatePasswordAsync("111 222 333", string.Empty, ct);
            Assert.Null(await store.GetPasswordAsync("111 222 333", ct));
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    /// <summary>
    /// Unticking "remember password" must forget what was stored, not merely stop writing: SaveOrUpdateAsync treats a
    /// null password as "leave it", so without an explicit clear the old password was read back and prefilled.
    /// </summary>
    [Fact]
    public async Task Connecting_with_remember_password_off_clears_the_stored_password_but_keeps_the_device()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rd_store_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            using var store = new RecentDevicesStore(tempDir, new MemorySecrets());
            var ct = TestContext.Current.CancellationToken;

            await MainWindowViewModel.RecordConnectedDeviceAsync(store, "111 222 333", "Dev1", "pwd1", rememberPassword: true, ct);
            Assert.Equal("pwd1", await store.GetPasswordAsync("111 222 333", ct));

            await MainWindowViewModel.RecordConnectedDeviceAsync(store, "111 222 333", "Dev1", "pwd2", rememberPassword: false, ct);
            Assert.Null(await store.GetPasswordAsync("111 222 333", ct));
            Assert.Equal("Dev1", Assert.Single(await store.GetAllAsync(ct)).Alias);

            // And ticking it again stores the password again.
            await MainWindowViewModel.RecordConnectedDeviceAsync(store, "111 222 333", "Dev1", "pwd3", rememberPassword: true, ct);
            Assert.Equal("pwd3", await store.GetPasswordAsync("111 222 333", ct));
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task RecentDevicesStore_scopes_the_list_and_its_passwords_to_an_account()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rd_store_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var secrets = new MemorySecrets();
            var ct = TestContext.Current.CancellationToken;

            using (var anonymous = new RecentDevicesStore(tempDir, secrets))
            using (var account = new RecentDevicesStore(tempDir, secrets, accountId: "acct-1"))
            {
                await anonymous.SaveOrUpdateAsync("111 222 333", alias: "Local", password: "local-pwd", cancellationToken: ct);
                await account.SaveOrUpdateAsync("111 222 333", alias: "Account", password: "account-pwd", cancellationToken: ct);

                // Same device, two lists and two passwords: neither leaks into the other.
                Assert.Equal("Local", (await anonymous.GetAllAsync(ct))[0].Alias);
                Assert.Equal("Account", (await account.GetAllAsync(ct))[0].Alias);
                Assert.Equal("local-pwd", await anonymous.GetPasswordAsync("111222333", ct));
                Assert.Equal("account-pwd", await account.GetPasswordAsync("111222333", ct));

                Assert.True(File.Exists(Path.Combine(tempDir, "recent_devices.json")));
                Assert.True(File.Exists(Path.Combine(tempDir, "accounts", "acct-1", "recent_devices.json")));
            }

            // An account id that could escape its directory is refused outright.
            Assert.Throws<ArgumentException>(() => new RecentDevicesStore(tempDir, secrets, accountId: "../escape"));
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task RecentDeviceItemViewModel_edits_and_saves_alias()
    {
        var entry = new RecentDeviceEntry
        {
            DeviceId = "123456789",
            Alias = "Original",
            LastConnectedUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        var savedAlias = string.Empty;
        var vm = new RecentDeviceItemViewModel(
            entry,
            onSaveAlias: item =>
            {
                savedAlias = item.Alias;
                return Task.FromResult(true);
            });

        Assert.Equal("Original (123 456 789)", vm.DisplayTitle);
        Assert.False(vm.IsEditingAlias);

        vm.StartEditAliasCommand.Execute(null);
        Assert.True(vm.IsEditingAlias);

        vm.NewAlias = "Updated Name";
        await vm.SaveAliasCommand.ExecuteAsync(null);

        Assert.False(vm.IsEditingAlias);
        Assert.Equal("Updated Name", vm.Alias);
        Assert.Equal("Updated Name", savedAlias);
        Assert.Equal("Updated Name (123 456 789)", vm.DisplayTitle);
    }

    [Fact]
    public async Task RecentDeviceItemViewModel_puts_the_alias_back_when_it_could_not_be_saved()
    {
        var entry = new RecentDeviceEntry
        {
            DeviceId = "123456789",
            Alias = "Original",
            LastConnectedUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        var vm = new RecentDeviceItemViewModel(entry, onSaveAlias: _ => Task.FromResult(false));

        vm.StartEditAliasCommand.Execute(null);
        vm.NewAlias = "Updated Name";
        await vm.SaveAliasCommand.ExecuteAsync(null);

        // The list on disk still says "Original": showing the new name would have the row and the file disagree.
        Assert.Equal("Original", vm.Alias);
        Assert.False(vm.IsEditingAlias);
    }

    /// <summary>
    /// A successful connection updates the stored password. The row's box must follow it unless the user typed
    /// something themselves; keeping the old box sent a fixed password that had been cleared or a rotated temporary
    /// one, and the row failed with "密码错误" until the client was restarted.
    /// </summary>
    [Theory]
    [InlineData("old", "old", "new", "new")]      // untouched auto-fill: follow the store
    [InlineData("typed", "old", "new", "typed")]  // an edit the user made is theirs
    [InlineData("", null, "new", "new")]          // first password remembered for this device
    [InlineData("", null, null, "")]              // nothing remembered, nothing to fill
    public void A_reloaded_row_refreshes_the_password_only_when_the_user_has_not_edited_it(
        string current,
        string? previouslySaved,
        string? saved,
        string expected)
    {
        Assert.Equal(expected, MainWindowViewModel.PasswordAfterReload(current, previouslySaved, saved));
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            try { Directory.Delete(path, recursive: true); } catch { }
        }
    }

    /// <summary>Stands in for the DPAPI store so the tests do not depend on the machine's key material.</summary>
    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, byte[]> _secrets = [];

        public void Save(string name, ReadOnlySpan<byte> secret) => _secrets[name] = secret.ToArray();

        public byte[]? Load(string name) => _secrets.TryGetValue(name, out var value) ? value : null;

        public void Delete(string name) => _secrets.Remove(name);
    }
}
