using System.Text;
using Microsoft.Extensions.Configuration;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Ops;

/// <summary>
/// The commands an installer runs before the service exists: creating the first administrator, and registering the
/// server's own nodes so a fresh console has somewhere to read from.
/// </summary>
/// <remarks>
/// Creating the first account is a command rather than a web page because a "first visit" setup screen is an
/// unprotected instance waiting for whoever finds it first. Here the only way in is someone with a shell on the box.
/// </remarks>
public static class SetupCommands
{
    /// <summary>
    /// Runs a setup command when <paramref name="args"/> names one, and says whether it did.
    /// </summary>
    /// <param name="args">Process arguments.</param>
    /// <param name="output">Where the command writes its instructions — the console in production, a buffer in tests.</param>
    /// <param name="exitCode">The process exit code when this returns true.</param>
    public static bool TryRun(string[] args, TextWriter output, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        exitCode = 0;
        if (args.Length == 0)
        {
            return false;
        }

        if (string.Equals(args[0], "--create-admin", StringComparison.Ordinal))
        {
            exitCode = CreateAdminAsync(args, output).GetAwaiter().GetResult();
            return true;
        }

        if (string.Equals(args[0], "--seed-local-nodes", StringComparison.Ordinal))
        {
            exitCode = SeedLocalNodesAsync(args, output).GetAwaiter().GetResult();
            return true;
        }

        if (string.Equals(args[0], "--sync-local-nodes", StringComparison.Ordinal))
        {
            exitCode = SyncLocalNodesAsync(args, output).GetAwaiter().GetResult();
            return true;
        }

        return false;
    }

    private static async Task<int> CreateAdminAsync(string[] args, TextWriter output)
    {
        var username = args.Length > 1 ? args[1] : null;
        if (string.IsNullOrWhiteSpace(username))
        {
            output.WriteLine("用法: RemoteDesktop.Server.Ops --create-admin <用户名> [--config-directory <目录>]");
            return 2;
        }

        var (options, error) = ReadOptions(args);
        if (error is not null)
        {
            output.WriteLine(error);
            output.WriteLine("If this is an installed package, pass --config-directory with the place the service reads (Linux: /etc/remotedesktop-server/Ops).");
            return 2;
        }

        var store = OpsStore.Open(options.Database);
        await store.EnsureSchemaAsync().ConfigureAwait(false);

        var existing = await store.FindUserAsync(username).ConfigureAwait(false);
        if (existing is not null)
        {
            output.WriteLine($"账号 {existing.Username} 已存在，未做任何修改。改口令请用另一个用户名，或直接更新数据库。");
            return 3;
        }

        var password = ReadSecret(output, "设置口令（至少 12 位）: ");
        if (password.Length < 12)
        {
            output.WriteLine("口令太短：至少 12 位。");
            return 2;
        }

        var confirmation = ReadSecret(output, "再输入一次: ");
        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            output.WriteLine("两次输入不一致。");
            return 2;
        }

        var secret = Totp.GenerateSecret();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var user = new OpsAdminUser(
            Guid.NewGuid().ToString("N"),
            username.Trim(),
            PasswordHasher.Hash(password),
            secret,
            OpsRoles.Admin,
            Disabled: false,
            now);

        await store.CreateUserAsync(user).ConfigureAwait(false);
        await store.AppendAuditAsync(
            new OpsAuditEntry(
                Guid.NewGuid().ToString("N"),
                now,
                "cli",
                "admin.create",
                user.Username,
                "{\"role\":\"admin\"}",
                "-",
                "success")).ConfigureAwait(false);

        var uri = Totp.BuildUri("RemoteDesktop", user.Username, secret);
        output.WriteLine();
        output.WriteLine($"账号: {user.Username}（角色 admin）");
        output.WriteLine($"TOTP 密钥: {secret}");
        output.WriteLine($"otpauth URI: {uri}");
        output.WriteLine();
        output.WriteLine("把上面的 URI 导入验证器（大多数应用也支持手动输入密钥）。");
        output.WriteLine("密钥只显示这一次；丢失后只能再建一个管理员账号。");
        return 0;
    }

    /// <summary>
    /// Registers the signaling and relay servers shipped beside this console. The installer calls this once: a console
    /// with no nodes has nowhere to read devices from, and these two addresses are the ones it already configured.
    /// A table that already has rows is left alone, so an operator's own list — including the removal of one of these
    /// rows — survives a reinstall.
    /// </summary>
    private static async Task<int> SeedLocalNodesAsync(string[] args, TextWriter output)
    {
        var candidates = LocalNodeCandidates(args, "--seed-local-nodes", output);
        if (candidates is null)
        {
            return 2;
        }

        var (options, error) = ReadOptions(args);
        if (error is not null)
        {
            output.WriteLine(error);
            output.WriteLine("If this is an installed package, pass --config-directory with the place the service reads (Linux: /etc/remotedesktop-server/Ops).");
            return 2;
        }

        using var store = OpsStore.Open(options.Database);
        await store.EnsureSchemaAsync().ConfigureAwait(false);
        if (await store.CountNodesAsync().ConfigureAwait(false) > 0)
        {
            output.WriteLine("控制台里已经登记过节点，未做任何修改。");
            return 0;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var (name, role, url) in candidates)
        {
            await store.CreateNodeAsync(
                new OpsNode(
                    Guid.NewGuid().ToString("N"),
                    name,
                    role,
                    url,
                    Region: null,
                    Enabled: true,
                    LastScrapeAt: null,
                    LastScrapeOk: null,
                    now)).ConfigureAwait(false);
            output.WriteLine($"已登记节点: {name}（{role}）→ {url}");
        }

        output.WriteLine("控制台启动后约 10 秒出现第一份节点数据；设备管理页从信令节点读取设备列表。");
        return 0;
    }

    /// <summary>
    /// Updates the addresses of the two local nodes that are already registered. An upgrade that changes signaling
    /// from http to https must do this: the rows are seeded only once, so without a sync the console keeps scraping the
    /// old scheme against the new listener and every scrape fails.
    /// </summary>
    /// <remarks>
    /// Only rows that exist are updated. A local node an operator deleted stays deleted — this is a migration of the
    /// installer's own rows, not a re-seed.
    /// </remarks>
    private static async Task<int> SyncLocalNodesAsync(string[] args, TextWriter output)
    {
        var candidates = LocalNodeCandidates(args, "--sync-local-nodes", output);
        if (candidates is null)
        {
            return 2;
        }

        var (options, error) = ReadOptions(args);
        if (error is not null)
        {
            output.WriteLine(error);
            output.WriteLine("If this is an installed package, pass --config-directory with the place the service reads (Linux: /etc/remotedesktop-server/Ops).");
            return 2;
        }

        using var store = OpsStore.Open(options.Database);
        await store.EnsureSchemaAsync().ConfigureAwait(false);
        foreach (var (name, _, url) in candidates)
        {
            if (await store.UpdateNodeBaseUrlAsync(name, url).ConfigureAwait(false))
            {
                output.WriteLine($"已更新节点地址: {name} → {url}");
            }
            else
            {
                output.WriteLine($"节点 {name} 不在控制台里，未创建。");
            }
        }

        return 0;
    }

    private static List<(string Name, string Role, string Url)>? LocalNodeCandidates(
        string[] args,
        string command,
        TextWriter output)
    {
        var rendezvous = Argument(args, "--rendezvous-base-url");
        var relay = Argument(args, "--relay-base-url");
        if (string.IsNullOrWhiteSpace(rendezvous) && string.IsNullOrWhiteSpace(relay))
        {
            output.WriteLine($"用法: RemoteDesktop.Server.Ops {command} --config-directory <目录> [--rendezvous-base-url <URL>] [--relay-base-url <URL>]");
            return null;
        }

        var candidates = new List<(string Name, string Role, string Url)>(2);
        if (!string.IsNullOrWhiteSpace(rendezvous))
        {
            candidates.Add(("rendezvous-local", "rendezvous", rendezvous.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(relay))
        {
            candidates.Add(("relay-local", "relay", relay.Trim()));
        }

        foreach (var (_, _, url) in candidates)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                output.WriteLine($"地址 '{url}' 必须是 http:// 或 https:// 开头的绝对地址。");
                return null;
            }
        }

        return candidates;
    }

    /// <summary>
    /// Reads the settings the service itself reads. The packaged service keeps them in /etc, not next to the program;
    /// without a way to point the commands at that directory they would use a different database, or refuse to run
    /// because there is no database path at all.
    /// </summary>
    private static (OpsOptions Options, string? Error) ReadOptions(string[] args)
    {
        // The service reads ProgramData on Windows (see ServerSetup.WindowsConfigDirectory) and its working directory
        // on Linux/macOS. The command has to read the same file, or it creates the first administrator in a database
        // the service will never open.
        var configDirectory = Argument(args, "--config-directory")
            ?? (OperatingSystem.IsWindows()
                ? Path.Combine(ServerSetup.WindowsConfigDirectory, ServerSetup.OpsFolder)
                : AppContext.BaseDirectory);

        var configuration = new ConfigurationBuilder()
            .SetBasePath(configDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var options = configuration.GetSection(OpsOptions.SectionName).Get<OpsOptions>() ?? new OpsOptions();

        // A relative SQLite path is relative to the settings it was written in, the same reading the service gives
        // it. Environment variables are expanded here too: the packaged Windows file says %ProgramData%, and the
        // service expands it, so treating it as a relative path created the administrator in
        // "...\Ops\%ProgramData%\..." instead of the database the service uses.
        if (string.Equals(options.Database.Provider, OpsDatabaseSettings.Sqlite, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(options.Database.Path))
        {
            var databasePath = Environment.ExpandEnvironmentVariables(options.Database.Path);
            options.Database.Path = Path.IsPathRooted(databasePath)
                ? databasePath
                : Path.Combine(configDirectory, databasePath);
        }

        try
        {
            options.Database.Validate();
        }
        catch (InvalidOperationException ex)
        {
            return (options, ex.Message);
        }

        return (options, null);
    }

    private static string? Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>
    /// Reads a secret without echoing it, falling back to a plain line when stdin is redirected (automation, tests).
    /// </summary>
    private static string ReadSecret(TextWriter output, string prompt)
    {
        output.Write(prompt);
        output.Flush();

        if (Console.IsInputRedirected)
        {
            return Console.ReadLine() ?? string.Empty;
        }

        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    output.WriteLine();
                    return text.ToString();
                case ConsoleKey.Backspace:
                    if (text.Length > 0)
                    {
                        text.Length--;
                        output.Write("\b \b");
                        output.Flush();
                    }

                    break;
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        text.Append(key.KeyChar);
                        output.Write('*');
                        output.Flush();
                    }

                    break;
            }
        }
    }
}
