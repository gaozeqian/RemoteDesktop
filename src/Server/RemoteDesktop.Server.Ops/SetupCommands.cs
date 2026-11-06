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
            exitCode = Run(() => CreateAdminAsync(args, output), output);
            return true;
        }

        if (string.Equals(args[0], "--reset-password", StringComparison.Ordinal))
        {
            exitCode = Run(() => ResetPasswordAsync(args, output), output);
            return true;
        }

        if (string.Equals(args[0], "--reset-totp", StringComparison.Ordinal))
        {
            exitCode = Run(() => ResetTotpAsync(args, output), output);
            return true;
        }

        if (string.Equals(args[0], "--seed-local-nodes", StringComparison.Ordinal))
        {
            exitCode = Run(() => SeedLocalNodesAsync(args, output), output);
            return true;
        }

        if (string.Equals(args[0], "--sync-local-nodes", StringComparison.Ordinal))
        {
            exitCode = Run(() => SyncLocalNodesAsync(args, output), output);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Runs a command and turns a database that cannot be reached or configured into a message and a code. These run
    /// from an installer's post-install step, where an unhandled exception is a stack trace on the operator's screen
    /// and an aborted process, for a problem (a mistyped connection string, a database that is not up yet) that the
    /// operator can fix and that the message can name.
    /// </summary>
    private static int Run(Func<Task<int>> command, TextWriter output)
    {
        try
        {
            return command().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or ArgumentException or InvalidOperationException)
        {
            output.WriteLine($"无法使用数据库：{ex.Message}");
            return 1;
        }
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
            WriteConfigHint(output);
            return 2;
        }

        using var store = OpsStore.Open(options.Database);
        await store.EnsureSchemaAsync().ConfigureAwait(false);

        var existing = await store.FindUserAsync(username).ConfigureAwait(false);
        if (existing is not null)
        {
            output.WriteLine($"账号 {existing.Username} 已存在，未做任何修改。改口令请用 --reset-password {existing.Username}，或让另一位管理员在控制台的账号管理里修改。");
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
        output.WriteLine("密钥只显示这一次；丢失后可用 --reset-totp 重置，或让另一位管理员在控制台重置。");
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
            WriteConfigHint(output);
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
            WriteConfigHint(output);
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
    /// Reads the settings the service itself reads, from the same directory the installed service uses.
    /// </summary>
    /// <remarks>
    /// The default is no longer "next to this executable": on Linux the service runs with
    /// <c>/etc/remotedesktop-server/Ops</c> as its content root and on Windows it reads ProgramData, while the
    /// executable lives under <c>/usr/lib</c> or Program Files. A relative SQLite path is resolved against the
    /// settings directory with the same helper the service uses, so the command and the service cannot open two
    /// different files. A missing directory or a path that expands to nothing is reported as a sentence, not a stack
    /// trace.
    /// </remarks>
    private static (OpsOptions Options, string? Error) ReadOptions(string[] args)
    {
        var explicitDirectory = Argument(args, "--config-directory");
        var configDirectory = explicitDirectory ?? ServerSetup.DefaultConfigDirectory(ServerSetup.OpsFolder);

        try
        {
            configDirectory = Path.GetFullPath(configDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (new OpsOptions(), $"配置目录无效: {ex.Message}");
        }

        if (!Directory.Exists(configDirectory))
        {
            // A container deployment has no /etc copy: the image's WORKDIR holds the shipped appsettings and the
            // compose file supplies provider and connection string through the environment. Falling back to the
            // program directory keeps `docker compose ... run ops --create-admin admin` working. An explicitly named
            // directory is still an error when it does not exist — a typo must not silently use another database.
            if (explicitDirectory is not null)
            {
                return (new OpsOptions(), $"配置目录不存在: {configDirectory}");
            }

            configDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        }

        IConfigurationRoot configuration;
        try
        {
            configuration = new ConfigurationBuilder()
                .SetBasePath(configDirectory)
                .AddJsonFile("appsettings.json", optional: true)
                .AddEnvironmentVariables()
                .Build();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (new OpsOptions(), $"无法读取配置目录 {configDirectory}: {ex.Message}");
        }

        var options = configuration.GetSection(OpsOptions.SectionName).Get<OpsOptions>() ?? new OpsOptions();
        options.Database ??= new OpsDatabaseSettings();

        // Provider names are trimmed by OpsDatabaseSettings.Canonical everywhere else; resolving only an exact
        // "sqlite" here would leave " sqlite " with an unexpanded, relative path.
        try
        {
            options.Database.Path = OpsDatabaseSettings.ResolvePath(
                options.Database.Provider,
                options.Database.Path,
                configDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (options, $"数据库路径无效: {ex.Message}");
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

    /// <summary>
    /// Resets an account's password from the machine. The same change is available in the console, but a console that
    /// cannot be signed into is exactly when the command line is needed.
    /// </summary>
    private static async Task<int> ResetPasswordAsync(string[] args, TextWriter output)
    {
        var username = args.Length > 1 ? args[1] : null;
        if (string.IsNullOrWhiteSpace(username))
        {
            output.WriteLine("用法: RemoteDesktop.Server.Ops --reset-password <用户名> [--config-directory <目录>]");
            return 2;
        }

        var (options, error) = ReadOptions(args);
        if (error is not null)
        {
            output.WriteLine(error);
            WriteConfigHint(output);
            return 2;
        }

        using var store = OpsStore.Open(options.Database);
        await store.EnsureSchemaAsync().ConfigureAwait(false);

        var user = await store.FindUserAsync(username).ConfigureAwait(false);
        if (user is null)
        {
            output.WriteLine($"账号 {username.Trim()} 不存在。");
            return 3;
        }

        var password = ReadSecret(output, "设置新口令（至少 12 位）: ");
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

        if (!await store.UpdateUserPasswordAsync(user.Id, PasswordHasher.Hash(password)).ConfigureAwait(false))
        {
            output.WriteLine($"账号 {user.Username} 不存在。");
            return 3;
        }

        await store.AppendAuditAsync(
            new OpsAuditEntry(
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                "cli",
                "admin.reset-password",
                user.Username,
                "{\"sessions\":\"revoked\"}",
                "-",
                "success")).ConfigureAwait(false);

        output.WriteLine($"账号 {user.Username} 的口令已重置，现有会话已全部退出。");
        return 0;
    }

    /// <summary>Resets an account's TOTP secret and prints the new enrolment URI once.</summary>
    private static async Task<int> ResetTotpAsync(string[] args, TextWriter output)
    {
        var username = args.Length > 1 ? args[1] : null;
        if (string.IsNullOrWhiteSpace(username))
        {
            output.WriteLine("用法: RemoteDesktop.Server.Ops --reset-totp <用户名> [--config-directory <目录>]");
            return 2;
        }

        var (options, error) = ReadOptions(args);
        if (error is not null)
        {
            output.WriteLine(error);
            WriteConfigHint(output);
            return 2;
        }

        using var store = OpsStore.Open(options.Database);
        await store.EnsureSchemaAsync().ConfigureAwait(false);

        var user = await store.FindUserAsync(username).ConfigureAwait(false);
        if (user is null)
        {
            output.WriteLine($"账号 {username.Trim()} 不存在。");
            return 3;
        }

        var secret = Totp.GenerateSecret();
        if (!await store.UpdateUserTotpSecretAsync(user.Id, secret).ConfigureAwait(false))
        {
            output.WriteLine($"账号 {user.Username} 不存在。");
            return 3;
        }

        await store.AppendAuditAsync(
            new OpsAuditEntry(
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                "cli",
                "admin.reset-totp",
                user.Username,
                "{\"sessions\":\"revoked\"}",
                "-",
                "success")).ConfigureAwait(false);

        output.WriteLine($"账号 {user.Username} 的 TOTP 密钥已重置，现有会话已全部退出。");
        output.WriteLine($"TOTP 密钥: {secret}");
        output.WriteLine($"otpauth URI: {Totp.BuildUri("RemoteDesktop", user.Username, secret)}");
        output.WriteLine("密钥只显示这一次。");
        return 0;
    }

    /// <summary>The directories to try when the default is wrong, one per platform that packages the server.</summary>
    private static void WriteConfigHint(TextWriter output) =>
        output.WriteLine(
            "If this is an installed package, pass --config-directory with the directory the service reads: "
            + $"Windows {Path.Combine(ServerSetup.WindowsConfigDirectory, ServerSetup.OpsFolder)}, "
            + "Linux /etc/remotedesktop-server/Ops, macOS the Ops directory under the install root.");

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
