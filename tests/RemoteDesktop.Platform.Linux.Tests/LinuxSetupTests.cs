using System.Text.Json.Nodes;
using RemoteDesktop.Platform.Linux.Setup;

namespace RemoteDesktop.Platform.Linux.Tests;

/// <summary>
/// Writing the one setting a person has to change on Linux.
/// </summary>
/// <remarks>
/// Worth testing rather than eyeballing because of what it writes into: this is the file both the client and the
/// controlled end read their settings from, and it is created on a machine that may already have one — with a key
/// in it, or with a comment, or with a syntax error somebody made by hand. None of those may lose the setting being
/// saved, and none of them may lose what was already there.
/// </remarks>
public sealed class LinuxSetupTests : IDisposable
{
    private const string LinuxOnly = "The Linux platform layer.";

    private readonly string _directory = Directory.CreateTempSubdirectory("rd-setup-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string SettingsPath => Path.Combine(_directory, "appsettings.json");

    private JsonObject ReadSettings() => JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();

    [Fact]
    public void It_writes_the_address_where_both_programs_will_read_it()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        LinuxSetup.SetRendezvousUri(_directory, "wss://rd.example.com/ws");

        Assert.Equal(
            "wss://rd.example.com/ws",
            ReadSettings()["Transport"]!["RendezvousUri"]!.GetValue<string>());
    }

    [Fact]
    public void The_folder_is_created_if_it_is_not_there_yet()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // The usual case on a machine where nothing has been run yet: the data folder does not exist until a
        // program first needs it, and this one needs it first.
        var nested = Path.Combine(_directory, "not", "yet");
        LinuxSetup.SetRendezvousUri(nested, "ws://127.0.0.1:5080/ws");

        Assert.True(File.Exists(Path.Combine(nested, "appsettings.json")));
    }

    [Fact]
    public void What_was_already_in_the_file_is_left_alone()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // Someone may have set other things here, and one of them decides whether the transport is allowed to
        // connect directly at all. Saving an address must not be a way to lose them.
        File.WriteAllText(SettingsPath, """
            {
              "Logging": { "LogLevel": { "Default": "Debug" } },
              "Transport": { "ForceRelay": true }
            }
            """);

        LinuxSetup.SetRendezvousUri(_directory, "ws://rd.example.com:5080/ws");

        var settings = ReadSettings();
        Assert.Equal("ws://rd.example.com:5080/ws", settings["Transport"]!["RendezvousUri"]!.GetValue<string>());
        Assert.True(settings["Transport"]!["ForceRelay"]!.GetValue<bool>());
        Assert.Equal("Debug", settings["Logging"]!["LogLevel"]!["Default"]!.GetValue<string>());
    }

    [Fact]
    public void A_settings_file_somebody_has_broken_is_replaced_rather_than_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        // Refusing would leave them unable to fix the address from the interface that exists to fix it, and the only
        // thing this ever writes is the one key.
        File.WriteAllText(SettingsPath, "{ this is not json");

        LinuxSetup.SetRendezvousUri(_directory, "ws://rd.example.com:5080/ws");

        Assert.Equal(
            "ws://rd.example.com:5080/ws",
            ReadSettings()["Transport"]!["RendezvousUri"]!.GetValue<string>());
    }

    [Fact]
    public void An_address_that_is_not_one_is_refused_before_anything_is_written()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        File.WriteAllText(SettingsPath, """{ "Transport": { "RendezvousUri": "ws://old.example.com/ws" } }""");

        Assert.Throws<ArgumentException>(() => LinuxSetup.SetRendezvousUri(_directory, "http://rd.example.com/ws"));

        // Unchanged, not emptied: a rejected address must not cost the one that was working.
        Assert.Equal(
            "ws://old.example.com/ws",
            ReadSettings()["Transport"]!["RendezvousUri"]!.GetValue<string>());
    }
}
