using System.Security.Cryptography;
using System.Text;
using RemoteDesktop.Platform.MacOS;

namespace RemoteDesktop.Platform.MacOS.UnitTests;

/// <summary>The real login keychain.</summary>
public class KeychainSecretStoreTests
{
    [Fact]
    public void A_secret_survives_a_round_trip_and_is_removed_on_delete()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The keychain is macOS only.");
        using var scratch = new ScratchDirectory();
        var store = new KeychainSecretStore(scratch.Path);
        var name = $"test-{Guid.NewGuid():N}";
        var secret = RandomNumberGenerator.GetBytes(64);

        try
        {
            Assert.Null(store.Load(name));

            store.Save(name, secret);
            Assert.Equal(secret, store.Load(name));

            // Saving again replaces the value rather than failing with a duplicate item.
            var replacement = RandomNumberGenerator.GetBytes(32);
            store.Save(name, replacement);
            Assert.Equal(replacement, store.Load(name));

            store.Delete(name);
            Assert.Null(store.Load(name));

            // Deleting something that is already gone is not an error.
            store.Delete(name);
        }
        finally
        {
            store.Delete(name);
        }
    }

    [Fact]
    public void Two_data_directories_are_two_devices()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The keychain is macOS only.");
        using var first = new ScratchDirectory();
        using var second = new ScratchDirectory();
        var one = new KeychainSecretStore(first.Path);
        var two = new KeychainSecretStore(second.Path);
        var name = $"test-{Guid.NewGuid():N}";

        try
        {
            one.Save(name, Encoding.UTF8.GetBytes("first"));
            Assert.Null(two.Load(name));

            two.Save(name, Encoding.UTF8.GetBytes("second"));
            Assert.Equal("first", Encoding.UTF8.GetString(one.Load(name)!));
            Assert.Equal("second", Encoding.UTF8.GetString(two.Load(name)!));
        }
        finally
        {
            one.Delete(name);
            two.Delete(name);
        }
    }

    private sealed class ScratchDirectory : IDisposable
    {
        public ScratchDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rd-macos-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
