using System.Text.Json.Nodes;
using Microsoft.Win32;
using RemoteDesktop.Platform.Windows.Setup;

namespace RemoteDesktop.Platform.Windows.Tests;

/// <summary>
/// The installer's registry and settings steps. Everything runs against a scratch key under HKCU and a temporary
/// folder, so the machine's real SoftwareSASGeneration policy is never read or written.
/// </summary>
public sealed class SystemSetupTests : IDisposable
{
    private const string SasValue = "SoftwareSASGeneration";

    private readonly string _keyRoot = $@"Software\RemoteDesktop.Tests\{Guid.NewGuid():N}";
    private readonly string _folder = Directory.CreateTempSubdirectory("rd-setup-").FullName;
    private readonly SystemSetup _setup;

    public SystemSetupTests() => _setup = new SystemSetup(new SystemSetupLocations(
        Registry.CurrentUser,
        $@"{_keyRoot}\Policy",
        $@"{_keyRoot}\Marker"));

    private string PolicyPath => $@"{_keyRoot}\Policy";

    private string MarkerPath => $@"{_keyRoot}\Marker";

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\RemoteDesktop.Tests", throwOnMissingSubKey: false);
        Directory.Delete(_folder, recursive: true);
    }

    private int? Policy => (int?)Registry.CurrentUser.OpenSubKey(PolicyPath)?.GetValue(SasValue);

    private void SetPolicy(int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(PolicyPath, writable: true);
        key.SetValue(SasValue, value, RegistryValueKind.DWord);
    }

    [Fact]
    public void Installing_allows_the_service_to_send_ctrl_alt_del_and_removing_takes_that_back()
    {
        Assert.Null(Policy);

        _setup.ConfigureSasPolicy();
        Assert.Equal(1, Policy);

        _setup.RevertSasPolicy();
        Assert.Null(Policy); // the value is removed again, not left as 0
        Assert.Null(Registry.CurrentUser.OpenSubKey(MarkerPath));
    }

    [Fact]
    public void Installing_twice_changes_nothing()
    {
        _setup.ConfigureSasPolicy();
        _setup.ConfigureSasPolicy();

        Assert.Equal(1, Policy);
        _setup.RevertSasPolicy();
        Assert.Null(Policy);
    }

    [Fact]
    public void Other_bits_of_the_policy_are_kept_when_they_were_already_there()
    {
        SetPolicy(2); // set by something else (ease-of-access apps)

        _setup.ConfigureSasPolicy();
        Assert.Equal(3, Policy);

        _setup.RevertSasPolicy();
        Assert.Equal(2, Policy); // only our bit is taken away
    }

    [Fact]
    public void A_policy_set_by_someone_else_is_never_touched()
    {
        SetPolicy(1); // an administrator allowed it; no marker of ours

        _setup.RevertSasPolicy();

        Assert.Equal(1, Policy);
    }

    [Fact]
    public void The_installer_remembers_that_it_added_the_bit_even_if_the_policy_later_changes()
    {
        _setup.ConfigureSasPolicy();
        SetPolicy(3); // somebody adds another feature's bit afterwards

        _setup.RevertSasPolicy();

        Assert.Equal(2, Policy);
    }

    [Fact]
    public void The_server_address_is_written_into_a_new_settings_file()
    {
        SystemSetup.SetRendezvousUri(_folder, "ws://rd.example.com:5080/ws");

        var transport = JsonNode.Parse(File.ReadAllText(Path.Combine(_folder, "appsettings.json")))!["Transport"]!;
        Assert.Equal("ws://rd.example.com:5080/ws", transport["RendezvousUri"]!.GetValue<string>());
    }

    [Fact]
    public void Existing_settings_are_kept_and_only_the_address_changes()
    {
        var path = Path.Combine(_folder, "appsettings.json");
        File.WriteAllText(path, """
            {
              // a comment that must survive being rewritten
              "Logging": { "LogLevel": { "Default": "Warning" } },
              "Transport": { "RendezvousUri": "ws://old:5080/ws", "ForceRelay": true }
            }
            """);

        SystemSetup.SetRendezvousUri(_folder, "wss://new.example.com/ws");

        var root = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("wss://new.example.com/ws", root["Transport"]!["RendezvousUri"]!.GetValue<string>());
        Assert.True(root["Transport"]!["ForceRelay"]!.GetValue<bool>());
        Assert.Equal("Warning", root["Logging"]!["LogLevel"]!["Default"]!.GetValue<string>());
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("rd.example.com:5080")]
    [InlineData("http://rd.example.com")]
    [InlineData("ws://")]
    [InlineData("just some text")]
    public void An_address_that_is_not_a_websocket_url_is_refused(string uri) =>
        Assert.Throws<ArgumentException>(() => SystemSetup.SetRendezvousUri(_folder, uri));

    [Fact]
    public void The_data_folder_is_removed_only_when_it_is_really_the_data_folder()
    {
        var data = Path.Combine(_folder, "RemoteDesktop");
        Directory.CreateDirectory(Path.Combine(data, "secrets"));
        File.WriteAllText(Path.Combine(data, "secrets", "device-identity.bin"), "x");

        Assert.True(SystemSetup.RemoveData(data));
        Assert.False(Directory.Exists(data));
        Assert.False(SystemSetup.RemoveData(data)); // already gone, nothing to do

        // Anything else is refused, however the path looks.
        var other = Directory.CreateDirectory(Path.Combine(_folder, "Documents")).FullName;
        Assert.Throws<ArgumentException>(() => SystemSetup.RemoveData(other));
        Assert.Throws<ArgumentException>(() => SystemSetup.RemoveData(_folder));
        Assert.Throws<ArgumentException>(() => SystemSetup.RemoveData(@"C:\"));
        Assert.True(Directory.Exists(other));
    }
}
