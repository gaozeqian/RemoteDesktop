using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using RemoteDesktop.Server.Relay;
using RemoteDesktop.Server.Rendezvous;
using RemoteDesktop.Server.Shared;
using RemoteDesktop.Server.Storage;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// What the server installer writes into the settings files. The point of checking it here rather than by
/// installing is the contract: the files have to be readable by the servers' own option types, and a value that
/// would stop a server from starting has to be refused while somebody is still watching the installation.
/// </summary>
public sealed class ServerSetupTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("rd-serversetup-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Both_settings_files_are_written_and_the_servers_can_read_them()
    {
        var notes = ServerSetup.Configure(_folder, new ServerSetupOptions
        {
            Urls = "https://0.0.0.0:5443",
            RelayPublicEndpoint = "203.0.113.10:5082",
            RelaySharedKey = Convert.ToBase64String(new byte[48]),
            PostgresConnectionString = "Host=db;Port=5432;Database=remotedesktop;Username=rd;Password=x",
            TlsCertificatePath = WriteCertificate(),
            TlsCertificatePassword = "pfx-password",
        });

        Assert.NotEmpty(notes);

        // Read back through the types the servers bind, which is what makes this a contract and not a JSON diff.
        var rendezvous = Read<RendezvousOptions>(ServerSetup.RendezvousFolder, RendezvousOptions.SectionName);
        rendezvous.Validate(); // refuses a key that is too short, or a relay address that is not host:port
        Assert.Equal("203.0.113.10:5082", rendezvous.RelayEndpoint);
        Assert.Equal("Host=db;Port=5432;Database=remotedesktop;Username=rd;Password=x", rendezvous.PostgresConnectionString);

        var relay = Read<RelayOptions>(ServerSetup.RelayFolder, RelayOptions.SectionName);
        Assert.Equal(5082, relay.Port);
        Assert.Equal(rendezvous.RelaySharedKey, relay.SharedKey); // one key, both servers

        var urls = Configuration(ServerSetup.RendezvousFolder)["Urls"];
        Assert.Equal("https://0.0.0.0:5443", urls);
    }

    /// <summary>
    /// Windows keeps the programs under Program Files and the live settings under ProgramData. The shipped file is
    /// the base for a first install, and the live file wins on every later run: the installer must not put the
    /// package's defaults back over what an operator changed.
    /// </summary>
    [Fact]
    public void A_live_file_wins_and_the_shipped_file_is_the_first_install_base()
    {
        var install = Path.Combine(_folder, "install");
        var config = Path.Combine(_folder, "config");
        var shipped = Path.Combine(install, ServerSetup.RendezvousFolder, ServerSetup.SettingsFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(shipped)!);
        File.WriteAllText(shipped, """{"Urls":"http://0.0.0.0:5080","Rendezvous":{"ReflectorPort":6000}}""");

        ServerSetup.Configure(config, new ServerSetupOptions { Urls = "http://0.0.0.0:5080" }, install);

        var live = Path.Combine(config, ServerSetup.RendezvousFolder, ServerSetup.SettingsFileName);
        Assert.True(File.Exists(live));
        Assert.Equal(6000, JsonNode.Parse(File.ReadAllText(live))!["Rendezvous"]!["ReflectorPort"]!.GetValue<int>());

        // A later run reads the live file, not the shipped one.
        var changed = JsonNode.Parse(File.ReadAllText(live))!.AsObject();
        changed["Rendezvous"]!["ReflectorPort"] = 6001;
        File.WriteAllText(live, changed.ToJsonString());

        ServerSetup.Configure(config, new ServerSetupOptions { Urls = "http://0.0.0.0:5081" }, install);

        Assert.Equal(6001, JsonNode.Parse(File.ReadAllText(live))!["Rendezvous"]!["ReflectorPort"]!.GetValue<int>());
        Assert.Equal("http://0.0.0.0:5081", JsonNode.Parse(File.ReadAllText(live))!["Urls"]!.GetValue<string>());
    }

    [Fact]
    public void A_key_is_generated_once_and_kept_afterwards()
    {
        ServerSetup.Configure(_folder, new ServerSetupOptions());
        var first = Read<RendezvousOptions>(ServerSetup.RendezvousFolder, RendezvousOptions.SectionName).RelaySharedKey;

        // An upgrade runs the same step again: rotating the key would cut off a relay on another machine that was
        // given the one printed when it was installed.
        ServerSetup.Configure(_folder, new ServerSetupOptions { Urls = "http://0.0.0.0:5080" });
        var second = Read<RendezvousOptions>(ServerSetup.RendezvousFolder, RendezvousOptions.SectionName).RelaySharedKey;

        Assert.Equal(first, second);
        Assert.True(Convert.FromBase64String(first).Length >= 32);
    }

    [Fact]
    public void The_ops_event_channel_is_generated_once_and_shared_by_all_three_services()
    {
        ServerSetup.Configure(_folder, new ServerSetupOptions());

        var rendezvous = Configuration(ServerSetup.RendezvousFolder);
        var relay = Configuration(ServerSetup.RelayFolder);
        var ops = Configuration(ServerSetup.OpsFolder);
        var key = rendezvous["OpsEvents:Key"];

        Assert.False(string.IsNullOrWhiteSpace(key));
        Assert.Equal(32, Convert.FromBase64String(key).Length);
        Assert.Equal(key, relay["OpsEvents:Key"]);
        Assert.Equal(key, ops["Ops:Ingest:Keys:k1"]);
        Assert.Equal("k1", rendezvous["OpsEvents:KeyId"]);
        Assert.Equal("k1", relay["OpsEvents:KeyId"]);
        Assert.Equal("http://127.0.0.1:5090", rendezvous["OpsEvents:Endpoint"]);
        Assert.Equal("http://127.0.0.1:5090", relay["OpsEvents:Endpoint"]);
        Assert.NotEqual(rendezvous["Rendezvous:RelaySharedKey"], key);

        // Upgrades run the same setup with no event options. The existing secret must not be rotated.
        ServerSetup.Configure(_folder, new ServerSetupOptions { Urls = "http://0.0.0.0:5080" });

        Assert.Equal(key, Configuration(ServerSetup.RendezvousFolder)["OpsEvents:Key"]);
        Assert.Equal(key, Configuration(ServerSetup.RelayFolder)["OpsEvents:Key"]);
        Assert.Equal(key, Configuration(ServerSetup.OpsFolder)["Ops:Ingest:Keys:k1"]);
    }

    [Fact]
    public void An_old_or_incomplete_ops_event_channel_is_repaired_with_the_existing_key()
    {
        ServerSetup.Configure(_folder, new ServerSetupOptions());
        var key = Configuration(ServerSetup.RendezvousFolder)["OpsEvents:Key"];

        var relay = MutableSettings(ServerSetup.RelayFolder);
        relay.Remove("OpsEvents");
        WriteSettings(ServerSetup.RelayFolder, relay);
        var ops = MutableSettings(ServerSetup.OpsFolder);
        ops["Ops"]!.AsObject().Remove("Ingest");
        WriteSettings(ServerSetup.OpsFolder, ops);

        ServerSetup.Configure(_folder, new ServerSetupOptions());

        Assert.Equal(key, Configuration(ServerSetup.RelayFolder)["OpsEvents:Key"]);
        Assert.Equal(key, Configuration(ServerSetup.OpsFolder)["Ops:Ingest:Keys:k1"]);
        Assert.Equal("http://127.0.0.1:5090", Configuration(ServerSetup.RelayFolder)["OpsEvents:Endpoint"]);
    }

    [Fact]
    public void Custom_ops_event_keys_endpoints_and_extra_console_keys_survive_an_upgrade()
    {
        ServerSetup.Configure(_folder, new ServerSetupOptions());
        var customKey = Convert.ToBase64String([.. Enumerable.Repeat((byte)7, 32)]);
        var extraKey = Convert.ToBase64String([.. Enumerable.Repeat((byte)9, 32)]);

        foreach (var folder in new[] { ServerSetup.RendezvousFolder, ServerSetup.RelayFolder })
        {
            var settings = MutableSettings(folder);
            settings["OpsEvents"] = new JsonObject
            {
                ["Endpoint"] = "https://ops.example.test/ingest/events",
                ["KeyId"] = "k2",
                ["Key"] = customKey,
                ["Node"] = "operator-name",
            };
            WriteSettings(folder, settings);
        }

        var ops = MutableSettings(ServerSetup.OpsFolder);
        ops["Ops"]!["Ingest"]!["Keys"] = new JsonObject
        {
            ["k2"] = customKey,
            ["another-node"] = extraKey,
        };
        WriteSettings(ServerSetup.OpsFolder, ops);

        ServerSetup.Configure(_folder, new ServerSetupOptions());

        var rendezvous = Configuration(ServerSetup.RendezvousFolder);
        Assert.Equal("https://ops.example.test/ingest/events", rendezvous["OpsEvents:Endpoint"]);
        Assert.Equal("k2", rendezvous["OpsEvents:KeyId"]);
        Assert.Equal(customKey, rendezvous["OpsEvents:Key"]);
        Assert.Equal("operator-name", rendezvous["OpsEvents:Node"]);
        var console = Configuration(ServerSetup.OpsFolder);
        Assert.Equal(customKey, console["Ops:Ingest:Keys:k2"]);
        Assert.Equal(extraKey, console["Ops:Ingest:Keys:another-node"]);
        Assert.Null(console["Ops:Ingest:Keys:k1"]);
    }

    [Fact]
    public void Conflicting_ops_keys_are_refused_instead_of_one_side_being_overwritten()
    {
        ServerSetup.Configure(_folder, new ServerSetupOptions());
        var relay = MutableSettings(ServerSetup.RelayFolder);
        var conflictingKey = Convert.ToBase64String([.. Enumerable.Repeat((byte)11, 32)]);
        relay["OpsEvents"]!["Key"] = conflictingKey;
        WriteSettings(ServerSetup.RelayFolder, relay);

        var error = Assert.Throws<InvalidOperationException>(() => ServerSetup.Configure(_folder, new ServerSetupOptions()));

        Assert.Contains("different values", error.Message, StringComparison.Ordinal);
        Assert.Equal(conflictingKey, Configuration(ServerSetup.RelayFolder)["OpsEvents:Key"]);
    }

    [Theory]
    [InlineData("not base64 at all")]
    [InlineData("c2hvcnQ=")]
    public void An_invalid_ops_ingest_key_is_refused_before_anything_is_written(string key)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ServerSetup.Configure(_folder, new ServerSetupOptions { OpsIngestKey = key }));

        Assert.False(File.Exists(Path.Combine(_folder, ServerSetup.RendezvousFolder, ServerSetup.SettingsFileName)));
        Assert.False(File.Exists(Path.Combine(_folder, ServerSetup.OpsFolder, ServerSetup.SettingsFileName)));
    }

    /// <summary>
    /// The key is meant to be carried to a relay on another machine, and the notes say so. A base64 key is full of
    /// "+", which the default JSON writer escapes as +: still the same string to anything that parses JSON, and
    /// a different key to anything that does not — a command line, where the operator is told to put it.
    /// </summary>
    [Fact]
    public void The_key_is_written_out_in_a_form_that_can_be_copied()
    {
        var key = Convert.ToBase64String([.. Enumerable.Range(0, 48).Select(value => (byte)(value * 7))]);
        Assert.Contains('+', key); // this key is only interesting if it has one

        ServerSetup.Configure(_folder, new ServerSetupOptions { RelaySharedKey = key });

        var written = File.ReadAllText(Path.Combine(_folder, ServerSetup.RendezvousFolder, ServerSetup.SettingsFileName));
        Assert.Contains(key, written, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u002B", written, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_key_given_by_the_installer_is_used_as_it_is()
    {
        var key = Convert.ToBase64String(new byte[32]);

        ServerSetup.Configure(_folder, new ServerSetupOptions { RelaySharedKey = key });

        Assert.Equal(key, Read<RelayOptions>(ServerSetup.RelayFolder, RelayOptions.SectionName).SharedKey);
    }

    [Fact]
    public void Metrics_are_mounted_only_when_the_installer_asks()
    {
        ServerSetup.Configure(_folder, new ServerSetupOptions());
        Assert.False(Read<RendezvousOptions>(ServerSetup.RendezvousFolder, RendezvousOptions.SectionName).MetricsEnabled);

        ServerSetup.Configure(_folder, new ServerSetupOptions { MetricsEnabled = true });
        Assert.True(Read<RendezvousOptions>(ServerSetup.RendezvousFolder, RendezvousOptions.SectionName).MetricsEnabled);

        // A later run that says nothing about metrics must not switch them off: an operator who turned them on by
        // hand keeps them.
        ServerSetup.Configure(_folder, new ServerSetupOptions());
        Assert.True(Read<RendezvousOptions>(ServerSetup.RendezvousFolder, RendezvousOptions.SectionName).MetricsEnabled);
    }

    [Theory]
    [InlineData("not base64 at all")]
    [InlineData("c2hvcnQ=")] // "short", five bytes
    public void A_key_that_would_stop_a_server_from_starting_is_refused(string key)
    {
        Assert.Throws<InvalidOperationException>(() => ServerSetup.Configure(_folder, new ServerSetupOptions { RelaySharedKey = key }));
    }

    [Theory]
    [InlineData("rd.example.com")] // no port
    [InlineData("rd.example.com:not-a-port")]
    [InlineData(":5082")]
    public void A_relay_address_that_is_not_host_and_port_is_refused(string endpoint) =>
        Assert.Throws<InvalidOperationException>(() => ServerSetup.Configure(_folder, new ServerSetupOptions { RelayPublicEndpoint = endpoint }));

    [Fact]
    public void Nothing_is_written_when_a_value_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => ServerSetup.Configure(_folder, new ServerSetupOptions
        {
            Urls = "http://0.0.0.0:5080",
            RelaySharedKey = "not base64 at all",
        }));

        Assert.False(File.Exists(Path.Combine(_folder, ServerSetup.RendezvousFolder, ServerSetup.SettingsFileName)));
    }

    /// <summary>
    /// The package's default for the relay address is <c>%COMPUTERNAME%:5082</c>, and a name is not something a
    /// client can be sent to: it takes the address and sends UDP there. So the name is expanded and resolved here,
    /// once, and what lands in the settings file is an address — which is what the servers insist on, and what this
    /// checks by asking them.
    /// </summary>
    [Fact]
    public void A_name_in_the_relay_address_is_resolved_to_an_address()
    {
        // "localhost" rather than Environment.MachineName: on a Mac whose own name does not resolve (which is a normal
        // configuration, and was the machine this suite ran on), the machine-name form fails for a reason that has
        // nothing to do with the code under test. The point here is "a name is resolved once and written as an
        // address", and localhost is a name every platform resolves.
        var notes = ServerSetup.Configure(_folder, new ServerSetupOptions { RelayPublicEndpoint = "localhost:5083" });

        var rendezvous = Read<RendezvousOptions>(ServerSetup.RendezvousFolder, RendezvousOptions.SectionName);
        rendezvous.Validate();

        var host = rendezvous.RelayEndpoint[..rendezvous.RelayEndpoint.LastIndexOf(':')];
        Assert.True(IPAddress.TryParse(host, out _), $"The relay address written was '{rendezvous.RelayEndpoint}', which is not an address.");
        Assert.EndsWith(":5083", rendezvous.RelayEndpoint, StringComparison.Ordinal);
        Assert.Equal(5083, Read<RelayOptions>(ServerSetup.RelayFolder, RelayOptions.SectionName).Port);
        Assert.NotEmpty(notes);
    }

    /// <summary>
    /// Nothing said about the database means nothing changed. The installers pass only what somebody filled in, and
    /// this is also the step used months later to fix an address or rotate a certificate — reading "not given" as
    /// "clear it" would drop the connection string, and every device code with it, on an installation that only meant
    /// to change something else.
    /// </summary>
    [Fact]
    public void Nothing_said_about_the_database_leaves_it_alone()
    {
        const string ConnectionString = "Host=db;Port=5432;Database=remotedesktop;Username=rd;Password=x";
        ServerSetup.Configure(_folder, new ServerSetupOptions { PostgresConnectionString = ConnectionString });

        ServerSetup.Configure(_folder, new ServerSetupOptions { Urls = "http://0.0.0.0:5081" });

        var rendezvous = Read<RendezvousOptions>(ServerSetup.RendezvousFolder, RendezvousOptions.SectionName);
        Assert.Equal(DatabaseSettings.Postgres, rendezvous.Database.Provider);
        Assert.Equal(ConnectionString, rendezvous.Database.ConnectionString);
        Assert.Equal(ConnectionString, rendezvous.PostgresConnectionString); // what an older server reads
    }

    /// <summary>
    /// An upgrade or configure-server.sh passes only what changed. An address that was not mentioned has to stay
    /// exactly where the operator put it — including TLS: rewriting it to the build's http default silently downgrades
    /// a server that was configured for https.
    /// </summary>
    [Fact]
    public void Nothing_said_about_the_addresses_leaves_them_alone()
    {
        ServerSetup.Configure(_folder, new ServerSetupOptions
        {
            Urls = "https://0.0.0.0:5443",
            RelayPublicEndpoint = "203.0.113.10:5082",
            DatabaseProvider = "sqlite",
        });

        ServerSetup.Configure(_folder, new ServerSetupOptions { DatabaseProvider = "sqlite" });

        Assert.Equal("https://0.0.0.0:5443", Configuration(ServerSetup.RendezvousFolder)["Urls"]);
        Assert.Equal("203.0.113.10:5082", Read<RendezvousOptions>(
            ServerSetup.RendezvousFolder,
            RendezvousOptions.SectionName).RelayEndpoint);
        Assert.Equal(5082, Read<RelayOptions>(ServerSetup.RelayFolder, RelayOptions.SectionName).Port);
    }

    /// <summary>
    /// A certificate is a TLS request on its own. configure-server.sh documents --tls-certificate as "serve TLS with
    /// this certificate"; requiring the operator to repeat --urls https://… is a trap that leaves the new certificate
    /// unused.
    /// </summary>
    [Fact]
    public void A_certificate_without_an_address_upgrades_the_existing_http_address()
    {
        ServerSetup.Configure(_folder, new ServerSetupOptions { Urls = "http://0.0.0.0:5080" });

        ServerSetup.Configure(_folder, new ServerSetupOptions
        {
            TlsCertificatePath = WriteCertificate(),
            TlsCertificatePassword = "pfx-password",
        });

        var root = Configuration(ServerSetup.RendezvousFolder);
        Assert.Equal("https://0.0.0.0:5080", root["Urls"]);
        Assert.NotNull(root["Kestrel:Certificates:Default:Path"]);
    }

    /// <summary>
    /// Without a database the device codes live in memory, which means every restart hands out different ones — the
    /// machines that were set up to be reached without anybody present stop being reachable. Worth saying out loud.
    /// </summary>
    [Fact]
    public void Having_no_database_is_said_out_loud()
    {
        var notes = ServerSetup.Configure(_folder, new ServerSetupOptions { DatabaseProvider = "none" });

        var rendezvous = Read<RendezvousOptions>(ServerSetup.RendezvousFolder, RendezvousOptions.SectionName);
        Assert.Equal(DatabaseSettings.None, rendezvous.Database.Provider);
        Assert.Contains(notes, note => note.Contains("memory only", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>auto</c> is what the packages ship: keep the database this machine already has, so that upgrading or
    /// re-running the step never changes which one the server uses.
    /// </summary>
    [Fact]
    public void Auto_keeps_postgres_when_the_machine_already_has_one()
    {
        const string ConnectionString = "Host=db;Port=5432;Database=remotedesktop;Username=rd;Password=x";
        ServerSetup.Configure(_folder, new ServerSetupOptions { PostgresConnectionString = ConnectionString });

        ServerSetup.Configure(_folder, new ServerSetupOptions { DatabaseProvider = "auto" });

        var rendezvous = Read<RendezvousOptions>(ServerSetup.RendezvousFolder, RendezvousOptions.SectionName);
        Assert.Equal(DatabaseSettings.Postgres, rendezvous.Database.Provider);
        Assert.Equal(ConnectionString, rendezvous.Database.ConnectionString);
    }

    /// <summary>And starts one when the machine has none — which is the case a single-machine installation is in.</summary>
    [Fact]
    public void Auto_starts_sqlite_when_the_machine_has_no_database()
    {
        var notes = ServerSetup.Configure(_folder, new ServerSetupOptions { DatabaseProvider = "auto" });

        var rendezvous = Read<RendezvousOptions>(ServerSetup.RendezvousFolder, RendezvousOptions.SectionName);
        Assert.Equal(DatabaseSettings.Sqlite, rendezvous.Database.Provider);
        Assert.False(string.IsNullOrWhiteSpace(rendezvous.Database.Path));
        Assert.EndsWith(".db", rendezvous.Database.Path, StringComparison.Ordinal);
        Assert.Contains(notes, note => note.Contains("SQLite", StringComparison.Ordinal));
    }

    /// <summary>
    /// "auto" keeps what the machine already chose. A memory-only installation is a choice, not a missing value: an
    /// upgrade that passes the package's auto default must not turn it into a SQLite file behind the operator's back.
    /// </summary>
    [Fact]
    public void Auto_keeps_the_memory_only_choice()
    {
        ServerSetup.Configure(_folder, new ServerSetupOptions { DatabaseProvider = "none" });

        ServerSetup.Configure(_folder, new ServerSetupOptions { DatabaseProvider = "auto" });

        var rendezvous = Read<RendezvousOptions>(ServerSetup.RendezvousFolder, RendezvousOptions.SectionName);
        Assert.Equal(DatabaseSettings.None, rendezvous.Database.Provider);
    }

    [Fact]
    public void A_sqlite_file_the_installer_names_is_the_one_written()
    {
        var path = Path.Combine(_folder, "data", "rendezvous.db");

        ServerSetup.Configure(_folder, new ServerSetupOptions { DatabaseProvider = "sqlite", DatabasePath = path });

        var rendezvous = Read<RendezvousOptions>(ServerSetup.RendezvousFolder, RendezvousOptions.SectionName);
        Assert.Equal(DatabaseSettings.Sqlite, rendezvous.Database.Provider);
        Assert.Equal(path, rendezvous.Database.Path);
        Assert.Null(rendezvous.PostgresConnectionString);
    }

    /// <summary>
    /// An empty connection string is how the installers have always said "no database", and it has to keep meaning
    /// that: a deployment that deliberately runs without one must not be given a file to lose instead.
    /// </summary>
    [Fact]
    public void An_empty_connection_string_still_means_no_database()
    {
        ServerSetup.Configure(_folder, new ServerSetupOptions { PostgresConnectionString = "" });

        var rendezvous = Read<RendezvousOptions>(ServerSetup.RendezvousFolder, RendezvousOptions.SectionName);
        Assert.Equal(DatabaseSettings.None, rendezvous.Database.Provider);
    }

    [Fact]
    public void An_unknown_database_provider_is_refused_before_anything_is_written()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ServerSetup.Configure(_folder, new ServerSetupOptions { DatabaseProvider = "mysql" }));

        Assert.False(File.Exists(Path.Combine(_folder, ServerSetup.RendezvousFolder, ServerSetup.SettingsFileName)));
    }

    [Fact]
    public void Plain_http_is_configured_and_called_out()
    {
        var notes = ServerSetup.Configure(_folder, new ServerSetupOptions { Urls = "auto" });

        Assert.Equal("http://0.0.0.0:5080", Configuration(ServerSetup.RendezvousFolder)["Urls"]);
        Assert.Contains(notes, note => note.Contains("plain http", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A certificate that is not there yet must not leave a server that cannot start: the installation stays on http
    /// and says what is missing.
    /// </summary>
    [Fact]
    public void A_certificate_that_is_not_there_leaves_the_server_on_http()
    {
        var notes = ServerSetup.Configure(_folder, new ServerSetupOptions
        {
            Urls = "http://0.0.0.0:5080",
            TlsCertificatePath = Path.Combine(_folder, "not-written-yet.pfx"),
        });

        Assert.Equal("http://0.0.0.0:5080", Configuration(ServerSetup.RendezvousFolder)["Urls"]);
        Assert.Contains(notes, note => note.Contains("not there", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_certificate_that_is_there_switches_the_server_to_https()
    {
        ServerSetup.Configure(_folder, new ServerSetupOptions
        {
            Urls = "http://0.0.0.0:5080",
            TlsCertificatePath = WriteCertificate(),
            TlsCertificatePassword = "pfx-password",
        });

        var root = Configuration(ServerSetup.RendezvousFolder);
        Assert.Equal("https://0.0.0.0:5080", root["Urls"]);
        Assert.NotNull(root["Kestrel:Certificates:Default:Path"]);
        Assert.Equal("pfx-password", root["Kestrel:Certificates:Default:Password"]);
    }

    /// <summary>
    /// The console cannot trust a self-signed signaling certificate through the System keychain (macOS refuses the
    /// trust change from a package postinstall), so it pins the certificate file instead.
    /// </summary>
    [Fact]
    public void A_node_certificate_can_be_pinned_in_the_console_settings()
    {
        var certificate = WriteCertificate();

        ServerSetup.Configure(_folder, new ServerSetupOptions { OpsNodeCertificatePath = certificate });

        Assert.Equal(certificate, Configuration(ServerSetup.OpsFolder)["Ops:NodeCertificatePath"]);
    }

    /// <summary>
    /// Turning TLS off has to remove the pin as well. A stale path would leave the console validating every node
    /// call against a certificate the signaling server no longer presents.
    /// </summary>
    [Fact]
    public void An_empty_node_certificate_path_removes_the_pin()
    {
        var certificate = WriteCertificate();
        ServerSetup.Configure(_folder, new ServerSetupOptions { OpsNodeCertificatePath = certificate });

        ServerSetup.Configure(_folder, new ServerSetupOptions { OpsNodeCertificatePath = string.Empty });

        Assert.Null(Configuration(ServerSetup.OpsFolder)["Ops:NodeCertificatePath"]);
    }

    /// <summary>
    /// The password is never stored in the installer's summary, so an upgrade pane sends the certificate path with an
    /// empty password field. Reusing the same PFX must keep the password that is already on the machine, or the
    /// reconfiguration clears it and the server refuses to start.
    /// </summary>
    [Fact]
    public void A_certificate_password_is_kept_when_the_certificate_did_not_change()
    {
        var certificate = WriteCertificate();
        ServerSetup.Configure(_folder, new ServerSetupOptions
        {
            Urls = "https://0.0.0.0:5080",
            TlsCertificatePath = certificate,
            TlsCertificatePassword = "pfx-password",
        });

        ServerSetup.Configure(_folder, new ServerSetupOptions
        {
            Urls = "https://0.0.0.0:5080",
            TlsCertificatePath = certificate,
        });

        Assert.Equal("pfx-password", Configuration(ServerSetup.RendezvousFolder)["Kestrel:Certificates:Default:Password"]);
    }

    /// <summary>An operator's own settings are not the installer's to throw away.</summary>
    [Fact]
    public void Settings_that_were_edited_by_hand_survive()
    {
        var path = Path.Combine(_folder, ServerSetup.RendezvousFolder, ServerSetup.SettingsFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "Logging": { "LogLevel": { "Default": "Warning" } },
              "Rendezvous": { "PresenceEnabled": false }
            }
            """);

        ServerSetup.Configure(_folder, new ServerSetupOptions());

        var root = Configuration(ServerSetup.RendezvousFolder);
        Assert.Equal("Warning", root["Logging:LogLevel:Default"]);
        Assert.False(root.GetValue<bool>("Rendezvous:PresenceEnabled"));
        Assert.False(File.Exists(path + ".tmp"));
    }

    /// <summary>
    /// The step the installer runs, from the command line to the files on disk. Two things about it are only visible
    /// from here: that the arguments the package passes are the ones being read, and that what happened is written
    /// down — the installer runs this through cmd.exe and shows its output to nobody.
    /// </summary>
    [Fact]
    public void The_command_line_the_installer_uses_writes_both_files_and_leaves_a_note()
    {
        var output = new StringWriter();
        var handled = SetupCommands.TryRun(
            [
                "--configure-system",
                "--install-directory", _folder,
                "--urls", "http://0.0.0.0:5080",
                "--relay-endpoint", "203.0.113.10:5082",
                "--relay-key", "auto",
                "--postgres", "auto",
                "--tls-certificate", string.Empty,
                "--tls-password", string.Empty,
            ],
            defaultInstallDirectory: _folder,
            output,
            out var exitCode);

        Assert.True(handled);
        Assert.Equal(SetupCommands.Success, exitCode);
        Assert.NotEmpty(output.ToString());

        var log = File.ReadAllText(Path.Combine(_folder, SetupCommands.LogFileName));
        Assert.Contains("Signaling listens on", log, StringComparison.Ordinal);

        var rendezvous = Read<RendezvousOptions>(ServerSetup.RendezvousFolder, RendezvousOptions.SectionName);
        rendezvous.Validate();
        Assert.Equal("203.0.113.10:5082", rendezvous.RelayEndpoint);

        // This is the command every native installer runs without extra event options. A successful install must
        // already have a working console event channel.
        var key = Configuration(ServerSetup.RendezvousFolder)["OpsEvents:Key"];
        Assert.False(string.IsNullOrWhiteSpace(key));
        Assert.Equal(key, Configuration(ServerSetup.RelayFolder)["OpsEvents:Key"]);
        Assert.Equal(key, Configuration(ServerSetup.OpsFolder)["Ops:Ingest:Keys:k1"]);
    }

    [Fact]
    public void A_remote_node_can_be_given_the_central_ops_endpoint_and_key_without_logging_the_secret()
    {
        var key = Convert.ToBase64String([.. Enumerable.Repeat((byte)5, 32)]);
        var output = new StringWriter();

        var handled = SetupCommands.TryRun(
            [
                "--configure-system",
                "--install-directory", _folder,
                "--ops-events-endpoint", "https://ops.example.test/ingest/events",
                "--ops-ingest-key", key,
            ],
            _folder,
            output,
            out var exitCode);

        Assert.True(handled);
        Assert.Equal(SetupCommands.Success, exitCode);
        Assert.Equal(key, Configuration(ServerSetup.RendezvousFolder)["OpsEvents:Key"]);
        Assert.Equal(key, Configuration(ServerSetup.RelayFolder)["OpsEvents:Key"]);
        Assert.Equal(key, Configuration(ServerSetup.OpsFolder)["Ops:Ingest:Keys:k1"]);
        Assert.Equal("https://ops.example.test/ingest/events", Configuration(ServerSetup.RelayFolder)["OpsEvents:Endpoint"]);
        Assert.DoesNotContain(key, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(key, File.ReadAllText(Path.Combine(_folder, SetupCommands.LogFileName)), StringComparison.Ordinal);
    }

    [Fact]
    public void The_command_line_is_left_alone_when_it_is_not_the_setup_step()
    {
        var output = new StringWriter();

        var handled = SetupCommands.TryRun(["--urls", "http://0.0.0.0:5080"], _folder, output, out _);

        Assert.False(handled); // the server starts as usual
        Assert.Empty(output.ToString());
    }

    [Fact]
    public void A_value_that_cannot_be_used_is_reported_and_written_down()
    {
        var output = new StringWriter();

        var handled = SetupCommands.TryRun(
            ["--configure-system", "--install-directory", _folder, "--relay-key", "not base64 at all"],
            defaultInstallDirectory: _folder,
            output,
            out var exitCode);

        Assert.True(handled);
        Assert.Equal(SetupCommands.InvalidArguments, exitCode);
        Assert.Contains("base64", File.ReadAllText(Path.Combine(_folder, SetupCommands.LogFileName)), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_folder, ServerSetup.RendezvousFolder, ServerSetup.SettingsFileName)));
    }

    [Fact]
    public void A_certificate_value_of_auto_is_the_same_as_none_and_says_so()
    {
        // "auto" is what the installer's property defaults to. It used to fall through to the file check and come back
        // as "the certificate 'auto' is not there", which reads like the operator did something wrong. The address is
        // passed as auto too: a run that says nothing about the address now leaves it alone, and this test is about
        // the certificate value, not about that distinction.
        var notes = ServerSetup.Configure(_folder, new ServerSetupOptions
        {
            Urls = ServerSetupOptions.Auto,
            TlsCertificatePath = ServerSetupOptions.Auto,
        });

        Assert.Contains(notes, note => note.Contains("plain http", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(notes, note => note.Contains("'auto' is not there", StringComparison.Ordinal));

        var rendezvous = Read<RendezvousOptions>(ServerSetup.RendezvousFolder, RendezvousOptions.SectionName);
        rendezvous.Validate();
        Assert.StartsWith("http://", Configuration(ServerSetup.RendezvousFolder)["Urls"], StringComparison.Ordinal);
    }

    /// <summary>
    /// The settings written for a certificate this step makes itself have to say how to load it, or the server does
    /// not start.
    /// </summary>
    /// <remarks>
    /// Skipped without elevation, because making that certificate means writing to the machine's certificate store.
    /// Which is also why this went wrong in the first place: the whole path only ever runs during an installation, and
    /// nothing that runs on a build agent could see it. Kestrel looks a certificate up in the store with "valid only"
    /// unless a setting says otherwise, and a self-signed certificate fails that test on every machine — so the host
    /// stopped with "could not be found in LocalMachine/My" about a certificate that was in there, with its private
    /// key, all along.
    /// </remarks>
    [Fact]
    public void A_certificate_this_step_generates_is_written_so_that_the_server_can_load_it()
    {
        // The platform check has to come first. WindowsIdentity.GetCurrent() throws PlatformNotSupportedException off
        // Windows, so evaluating the elevation test below on macOS failed the run instead of skipping it. What this
        // covers where there is a store is the store form; elsewhere the same step writes PEM files and points
        // Kestrel at them with Path/KeyPath — that form has its own tests below.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The generated certificate goes into the Windows certificate store.");
        Assert.SkipUnless(
            new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator),
            "Writing to the machine's certificate store needs elevation.");

        var notes = ServerSetup.Configure(_folder, new ServerSetupOptions
        {
            Urls = "https://0.0.0.0:5443",
            RelayPublicEndpoint = "203.0.113.10:5082",
            RelaySharedKey = Convert.ToBase64String(new byte[48]),
            TlsCertificatePath = ServerSetupOptions.GenerateCertificate,
        });

        Assert.Contains(notes, note => note.Contains("certificate this step created", StringComparison.Ordinal));

        var certificates = Configuration(ServerSetup.RendezvousFolder).GetSection("Kestrel:Certificates:Default");
        Assert.Equal(SelfSignedCertificate.CertificateStoreLocation, certificates["Location"]);
        Assert.Equal(SelfSignedCertificate.CertificateStoreName, certificates["Store"]);

        // Without the CN=: Kestrel finds a certificate by its subject *name*, so the distinguished form the
        // certificate itself carries matches nothing — and says so as "could not be found in LocalMachine/My".
        Assert.Equal(SelfSignedCertificate.NameForThisMachine, certificates["Subject"]);
        Assert.DoesNotContain("CN=", certificates["Subject"]!, StringComparison.Ordinal);
        Assert.Equal("true", certificates["AllowInvalid"]);

        // And the certificate really is where those settings say it is, with a key the service can use. Looked up the
        // way Kestrel does it, so that a name it cannot find fails here rather than at the first start-up.
        using var store = new X509Store(SelfSignedCertificate.CertificateStoreName, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates.Find(X509FindType.FindBySubjectName, certificates["Subject"]!, validOnly: false);
        Assert.Single(found);
        Assert.True(found[0].HasPrivateKey, "the store kept the certificate without its key, so TLS could not be served with it");
    }

    [Fact]
    public void The_generated_certificate_covers_this_machine_and_keeps_its_private_key()
    {
        using var certificate = SelfSignedCertificate.CreateForThisMachine(SelfSignedCertificate.SubjectForThisMachine);

        // A certificate straight out of CreateSelfSigned has a private key that only lives as long as the RSA object;
        // this one has been through PKCS#12 so that a certificate store can keep it.
        Assert.True(certificate.HasPrivateKey, "the private key did not survive the round trip, so the store cannot keep it");
        Assert.Equal(certificate.Subject, certificate.Issuer); // self-signed
        Assert.True(certificate.NotAfter > DateTime.UtcNow.AddYears(1), "a certificate that expires soon would be worse than none");

        var names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Contains(Environment.MachineName, names.EnumerateDnsNames());

        var usage = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.Contains(usage.EnhancedKeyUsages.Cast<Oid>(), oid => oid.Value == "1.3.6.1.5.5.7.3.1"); // serverAuth
    }

    [Fact]
    public void On_a_system_with_no_certificate_store_the_certificate_is_two_files()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "There is a store on Windows to keep the key in.");

        var notes = ServerSetup.Configure(_folder, new ServerSetupOptions
        {
            Urls = "https://0.0.0.0:5443",
            RelayPublicEndpoint = "203.0.113.10:5082",
            RelaySharedKey = Convert.ToBase64String(new byte[48]),
            TlsCertificatePath = ServerSetupOptions.GenerateCertificate,
        });

        var certificates = Configuration(ServerSetup.RendezvousFolder).GetSection("Kestrel:Certificates:Default");

        // Kestrel is pointed at a certificate and a key rather than at a store entry, which is the only form that
        // works where there is no store.
        var certificatePath = certificates["Path"]!;
        var keyPath = certificates["KeyPath"]!;
        Assert.True(File.Exists(certificatePath), $"Kestrel was pointed at '{certificatePath}', which is not there.");
        Assert.True(File.Exists(keyPath), $"Kestrel was pointed at '{keyPath}', which is not there.");

        // And none of the store form is left behind: a leftover Subject would take precedence and the server would
        // not start.
        Assert.Null(certificates["Subject"]);
        Assert.Null(certificates["Store"]);
        Assert.Null(certificates["Location"]);
        Assert.Null(certificates["AllowInvalid"]);

        Assert.Contains("BEGIN CERTIFICATE", File.ReadAllText(certificatePath), StringComparison.Ordinal);
        Assert.Contains("BEGIN PRIVATE KEY", File.ReadAllText(keyPath), StringComparison.Ordinal);
        Assert.NotEmpty(notes);
    }

    [Fact]
    public void The_private_key_is_readable_only_by_its_owner()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix file permissions.");

        var notes = new List<string>();
        var (certificatePath, keyPath) = SelfSignedCertificate.EnsurePem(Path.Combine(_folder, "tls"), notes);

        // This is the whole of the protection on this side. On Windows the key stays in the machine's store and the
        // folder being world-readable does not matter; here the key is a file, so its permissions are what stands
        // between it and every other account on the machine.
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, new FileInfo(keyPath).UnixFileMode);

        // The certificate is public by design: clients are told to fetch it.
        Assert.True(new FileInfo(certificatePath).UnixFileMode.HasFlag(UnixFileMode.OtherRead));
    }

    [Fact]
    public void Generating_again_keeps_the_certificate_that_is_already_there()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Posix file permissions.");

        var tls = Path.Combine(_folder, "tls");
        var (_, keyPath) = SelfFigned(tls);

        // A client that trusted this certificate yesterday must still trust it today, and upgrading the server runs
        // this step again.
        var before = File.ReadAllText(keyPath);
        var (_, keyPathAgain) = SelfFigned(tls);

        Assert.Equal(keyPath, keyPathAgain);
        Assert.Equal(before, File.ReadAllText(keyPathAgain));

        static (string Certificate, string Key) SelfFigned(string directory) =>
            SelfSignedCertificate.EnsurePem(directory, []);
    }

    [Fact]
    public void The_generated_certificate_is_one_a_server_could_serve()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "The PEM form.");

        var notes = new List<string>();
        var (certificatePath, _) = SelfSignedCertificate.EnsurePem(Path.Combine(_folder, "tls"), notes);

        using var certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);

        Assert.True(certificate.HasPrivateKey == false); // the file holds the public half
        Assert.True(certificate.NotAfter > DateTime.Now.AddYears(2));
        Assert.Contains(SelfSignedCertificate.NameForThisMachine, certificate.Subject, StringComparison.Ordinal);

        // It has to be usable for the address clients are given, and its own machine name is in there.
        var names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Contains(Environment.MachineName, names.EnumerateDnsNames(), StringComparer.OrdinalIgnoreCase);

        // And Kestrel has to accept it: a certificate it cannot read as a certificate reports as a missing file.
        Assert.NotEmpty(certificate.RawData);
        Assert.Contains("self-signed", string.Join(' ', notes), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_console_follows_the_database_provider()
    {
        var notes = ServerSetup.Configure(_folder, new ServerSetupOptions
        {
            DatabaseProvider = "sqlite",
            DatabasePath = Path.Combine(_folder, "data", "rendezvous.db"),
            OpsDatabasePath = Path.Combine(_folder, "data", "ops.db"),
            OpsListen = "http://127.0.0.1:5090",
        });

        var ops = Configuration(ServerSetup.OpsFolder);
        Assert.Equal(DatabaseSettings.Sqlite, ops["Ops:Database:Provider"]);
        Assert.Equal(Path.Combine(_folder, "data", "ops.db"), ops["Ops:Database:Path"]);
        Assert.Equal("http://127.0.0.1:5090", ops["Ops:Listen"]);
        Assert.Contains(notes, note => note.Contains("monitoring console", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_ops_gateway_is_https_on_loopback_upstream_with_an_ip_certificate()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "The generated certificate is written as PEM here.");

        var notes = ServerSetup.Configure(_folder, new ServerSetupOptions
        {
            GatewayListen = "https://0.0.0.0:5443",
            GatewayHostName = "203.0.113.77, ops.internal",
            OpsListen = "http://127.0.0.1:5090",
        });

        var gateway = Configuration(ServerSetup.GatewayFolder);
        Assert.Equal("https://0.0.0.0:5443", gateway["Gateway:Listen"]);
        Assert.Equal(ServerSetup.DefaultGatewayUpstream, gateway["Gateway:Upstream"]);
        Assert.Equal("/{**catch-all}", gateway["ReverseProxy:Routes:ops:Match:Path"]);
        Assert.Equal("Set", gateway["ReverseProxy:Routes:ops:Transforms:0:X-Forwarded"]);
        Assert.Equal(ServerSetup.DefaultGatewayUpstream, gateway["ReverseProxy:Clusters:ops:Destinations:loopback:Address"]);

        // The console stays on loopback. Its trusted-proxy list is what makes the forwarded TLS scheme and real client
        // address usable for Secure cookies, audit entries and sign-in throttling.
        var ops = Configuration(ServerSetup.OpsFolder);
        Assert.Equal("http://127.0.0.1:5090", ops["Ops:Listen"]);
        var trusted = JsonNode.Parse(File.ReadAllText(
            Path.Combine(_folder, ServerSetup.OpsFolder, ServerSetup.SettingsFileName)))!["Ops"]!["TrustedProxies"]!.AsArray();
        Assert.Contains(trusted, entry => entry!.GetValue<string>() == "127.0.0.1");

        // The gateway generates the machine certificate when signaling did not. The extra address becomes an IP SAN,
        // which is what lets an operator open https://203.0.113.77:5443 without a DNS name.
        var certificatePath = gateway["Kestrel:Certificates:Default:Path"]!;
        using var certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
        var names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Contains("203.0.113.77", names.EnumerateIPAddresses().Select(address => address.ToString()));
        Assert.Contains("ops.internal", names.EnumerateDnsNames(), StringComparer.OrdinalIgnoreCase);
        Assert.Contains(notes, note => note.Contains("HTTPS gateway", StringComparison.Ordinal));
    }

    [Fact]
    public void The_ops_gateway_refuses_plain_http_before_writing_any_settings()
    {
        Assert.Throws<InvalidOperationException>(() => ServerSetup.Configure(_folder, new ServerSetupOptions
        {
            GatewayListen = "http://0.0.0.0:5443",
        }));

        Assert.False(File.Exists(Path.Combine(_folder, ServerSetup.GatewayFolder, ServerSetup.SettingsFileName)));
        Assert.False(File.Exists(Path.Combine(_folder, ServerSetup.RendezvousFolder, ServerSetup.SettingsFileName)));
    }

    [Fact]
    public void The_console_shares_the_postgres_database()
    {
        const string ConnectionString = "Host=db;Port=5432;Database=remotedesktop;Username=rd;Password=x";

        ServerSetup.Configure(_folder, new ServerSetupOptions
        {
            DatabaseProvider = "postgres",
            PostgresConnectionString = ConnectionString,
        });

        var ops = Configuration(ServerSetup.OpsFolder);
        Assert.Equal(DatabaseSettings.Postgres, ops["Ops:Database:Provider"]);
        Assert.Equal(ConnectionString, ops["Ops:Database:ConnectionString"]);
        Assert.Null(ops["Ops:Database:Path"]);
    }

    /// <summary>
    /// A console cannot keep its data in memory, so "none" for the signaling server still gives it a SQLite file.
    /// </summary>
    [Fact]
    public void The_console_still_gets_a_database_when_the_signaling_server_has_none()
    {
        ServerSetup.Configure(_folder, new ServerSetupOptions { DatabaseProvider = "none" });

        var ops = Configuration(ServerSetup.OpsFolder);
        Assert.Equal(DatabaseSettings.Sqlite, ops["Ops:Database:Provider"]);
        Assert.False(string.IsNullOrWhiteSpace(ops["Ops:Database:Path"]));
    }

    [Fact]
    public void Postgres_without_a_connection_string_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ServerSetup.Configure(_folder, new ServerSetupOptions { DatabaseProvider = "postgres" }));

        Assert.False(File.Exists(Path.Combine(_folder, ServerSetup.RendezvousFolder, ServerSetup.SettingsFileName)));
    }

    /// <summary>
    /// A file that exists is not a certificate. Loading it the way Kestrel will is what turns a typo into a failed
    /// installation rather than a service that dies on its first start.
    /// </summary>
    [Fact]
    public void An_invalid_certificate_is_refused_before_anything_is_written()
    {
        var path = Path.Combine(_folder, "not-a-certificate.pfx");
        File.WriteAllText(path, "not a certificate");

        Assert.Throws<InvalidOperationException>(() => ServerSetup.Configure(_folder, new ServerSetupOptions
        {
            Urls = "https://0.0.0.0:5080",
            TlsCertificatePath = path,
        }));

        Assert.False(File.Exists(Path.Combine(_folder, ServerSetup.RendezvousFolder, ServerSetup.SettingsFileName)));
    }

    [Fact]
    public void A_pfx_with_the_wrong_password_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => ServerSetup.Configure(_folder, new ServerSetupOptions
        {
            Urls = "https://0.0.0.0:5080",
            TlsCertificatePath = WriteCertificate(),
            TlsCertificatePassword = "not-the-password",
        }));
    }

    [Fact]
    public void A_generated_certificate_carries_the_names_it_was_given()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "The PEM form.");

        ServerSetup.Configure(_folder, new ServerSetupOptions
        {
            Urls = "https://0.0.0.0:5443",
            TlsCertificatePath = ServerSetupOptions.GenerateCertificate,
            TlsHostName = "rd.example.com, 203.0.113.10",
        });

        var certificatePath = Configuration(ServerSetup.RendezvousFolder)["Kestrel:Certificates:Default:Path"]!;
        using var certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
        var names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();

        Assert.Contains("rd.example.com", names.EnumerateDnsNames(), StringComparer.OrdinalIgnoreCase);
        Assert.Contains("203.0.113.10", names.EnumerateIPAddresses().Select(address => address.ToString()));
    }

    /// <summary>
    /// The settings carry the relay key and, in the PostgreSQL case, a password. The service account reads them
    /// through its group; every other account is kept out.
    /// </summary>
    [Fact]
    public void On_unix_the_settings_are_readable_by_the_service_group_only()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix file permissions.");

        ServerSetup.Configure(_folder, new ServerSetupOptions { DatabaseProvider = "sqlite" });

        var path = Path.Combine(_folder, ServerSetup.RendezvousFolder, ServerSetup.SettingsFileName);
        var mode = new FileInfo(path).UnixFileMode;

        Assert.True(mode.HasFlag(UnixFileMode.UserRead));
        Assert.True(mode.HasFlag(UnixFileMode.UserWrite));
        Assert.True(mode.HasFlag(UnixFileMode.GroupRead));
        Assert.False(mode.HasFlag(UnixFileMode.GroupWrite));
        Assert.False(mode.HasFlag(UnixFileMode.OtherRead));
    }

    private JsonObject MutableSettings(string folder) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(_folder, folder, ServerSetup.SettingsFileName)))!.AsObject();

    private void WriteSettings(string folder, JsonObject settings)
    {
        File.WriteAllText(Path.Combine(_folder, folder, ServerSetup.SettingsFileName), settings.ToJsonString());
    }

    private string WriteCertificate()
    {
        // A real PFX, because the step now loads it the way Kestrel will: a file that merely exists is not enough to
        // promise the server will start, which is exactly what the check is for.
        var path = Path.Combine(_folder, "signaling.pfx");
        using var certificate = SelfSignedCertificate.CreateForThisMachine(SelfSignedCertificate.SubjectForThisMachine);
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, "pfx-password"));
        return path;
    }

    private IConfigurationRoot Configuration(string folder) =>
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(_folder, folder, ServerSetup.SettingsFileName))
            .Build();

    private T Read<T>(string folder, string section)
        where T : new() =>
        Configuration(folder).GetSection(section).Get<T>() ?? new T();
}
