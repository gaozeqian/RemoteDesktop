using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using RemoteDesktop.Application.Hosting;
using RemoteDesktop.Domain;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The settings file the person owns, off Windows: written by "修改服务器地址" and read by every host that runs as
/// that person.
/// </summary>
/// <remarks>
/// Worth testing rather than eyeballing because of what it writes into: the file is created on a machine that may
/// already have one — with a key in it, or with a comment, or with a syntax error somebody made by hand. None of
/// those may lose the setting being saved, and none of them may lose what was already there.
/// </remarks>
public sealed class UserSettingsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("rd-settings-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string SettingsPath => UserSettings.PathFor(_directory);

    private JsonObject ReadSettings() => JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();

    [Fact]
    public void It_writes_the_address_where_every_host_will_read_it()
    {
        UserSettings.SaveRendezvousUri(_directory, RendezvousAddress.Parse("wss://rd.example.com/ws"));

        Assert.Equal(
            "wss://rd.example.com/ws",
            ReadSettings()["Transport"]!["RendezvousUri"]!.GetValue<string>());
    }

    [Fact]
    public void The_folder_is_created_if_it_is_not_there_yet()
    {
        // The usual case on a machine where nothing has been run yet: the data folder does not exist until a
        // program first needs it, and this one needs it first.
        var nested = Path.Combine(_directory, "not", "yet");
        UserSettings.SaveRendezvousUri(nested, RendezvousAddress.Parse("ws://127.0.0.1:5080/ws"));

        Assert.True(File.Exists(UserSettings.PathFor(nested)));
    }

    [Fact]
    public void What_was_already_in_the_file_is_left_alone()
    {
        // Someone may have set other things here, and one of them decides whether the transport is allowed to
        // connect directly at all. Saving an address must not be a way to lose them.
        File.WriteAllText(SettingsPath, """
            {
              "Logging": { "LogLevel": { "Default": "Debug" } },
              "Transport": { "ForceRelay": true }
            }
            """);

        UserSettings.SaveRendezvousUri(_directory, RendezvousAddress.Parse("ws://rd.example.com:5080/ws"));

        var settings = ReadSettings();
        Assert.Equal("ws://rd.example.com:5080/ws", settings["Transport"]!["RendezvousUri"]!.GetValue<string>());
        Assert.True(settings["Transport"]!["ForceRelay"]!.GetValue<bool>());
        Assert.Equal("Debug", settings["Logging"]!["LogLevel"]!["Default"]!.GetValue<string>());
    }

    [Fact]
    public void A_settings_file_somebody_has_broken_is_replaced_rather_than_refused()
    {
        // Refusing would leave them unable to fix the address from the interface that exists to fix it, and the only
        // thing this ever writes is the one key.
        File.WriteAllText(SettingsPath, "{ this is not json");

        UserSettings.SaveRendezvousUri(_directory, RendezvousAddress.Parse("ws://rd.example.com:5080/ws"));

        Assert.Equal(
            "ws://rd.example.com:5080/ws",
            ReadSettings()["Transport"]!["RendezvousUri"]!.GetValue<string>());
    }

    [Fact]
    public void The_overlay_sits_under_the_command_line_but_over_the_shipped_file()
    {
        // The whole point of the file is that a person can change the address without touching the installed one;
        // the whole point of where it is inserted is that a redirect passed on the command line (a test, a support
        // session) still wins — otherwise it would look like the argument was ignored. The in-memory source stands in
        // for the command line: it is just another source that was added before the overlay.
        var shipped = Path.Combine(_directory, "shipped", "appsettings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(shipped)!);
        File.WriteAllText(shipped, "{}");
        File.WriteAllText(SettingsPath, """{ "Transport": { "RendezvousUri": "ws://saved.example.com/ws" } }""");

        var configuration = new ConfigurationManager();
        configuration.AddJsonFile(shipped, optional: true);
        configuration.AddInMemoryCollection([new("Transport:RendezvousUri", "ws://cli.example.com/ws")]);

        UserSettings.AddConfigurationSource(configuration, _directory);

        // The later source still wins: appending the overlay at the end instead would silently override it.
        Assert.Equal("ws://cli.example.com/ws", configuration["Transport:RendezvousUri"]);

        // And where nothing else says anything, the saved value overrides the shipped file.
        var plain = new ConfigurationManager();
        plain.AddJsonFile(shipped, optional: true);
        UserSettings.AddConfigurationSource(plain, _directory);
        Assert.Equal("ws://saved.example.com/ws", plain["Transport:RendezvousUri"]);
    }

    [Fact]
    public void A_missing_file_adds_nothing_and_installs_no_watcher()
    {
        var state = new ConfigurationManager();
        var before = state.Sources.Count;

        UserSettings.AddConfigurationSource(state, _directory);

        Assert.Equal(before, state.Sources.Count);
    }
}
