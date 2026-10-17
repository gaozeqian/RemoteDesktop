using System.Text;
using Microsoft.Extensions.Configuration;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;

namespace RemoteDesktop.Server.Ops;

/// <summary>
/// The commands an installer runs before the service exists: today, creating the first administrator.
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
        if (args.Length == 0 || !string.Equals(args[0], "--create-admin", StringComparison.Ordinal))
        {
            return false;
        }

        exitCode = CreateAdminAsync(args, output).GetAwaiter().GetResult();
        return true;
    }

    private static async Task<int> CreateAdminAsync(string[] args, TextWriter output)
    {
        var username = args.Length > 1 ? args[1] : null;
        if (string.IsNullOrWhiteSpace(username))
        {
            output.WriteLine("用法: RemoteDesktop.Server.Ops --create-admin <用户名>");
            return 2;
        }

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var options = configuration.GetSection(OpsOptions.SectionName).Get<OpsOptions>() ?? new OpsOptions();
        try
        {
            options.Database.Validate();
        }
        catch (InvalidOperationException ex)
        {
            output.WriteLine(ex.Message);
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
