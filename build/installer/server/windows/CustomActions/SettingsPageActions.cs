using WixToolset.Dtf.WindowsInstaller;
using System.Globalization;
using System.Net.Sockets;

namespace RemoteDesktop.Server.Installer.CustomActions;

/// <summary>
/// The two things the settings page cannot do by itself: ask for a file, and find out whether a database is there.
/// </summary>
/// <remarks>
/// Both are immediate actions in the user-interface sequence, which is what makes them able to talk to the page at
/// all: they run in msiexec's own process, so the properties the page wrote are simply readable, and setting one
/// changes what the page shows. A deferred action has neither.
/// </remarks>
public static class SettingsPageActions
{
    /// <summary>How long a database gets to accept a connection before the page says it is not there.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Asks for a certificate file and puts the answer where the settings page shows it.
    /// </summary>
    /// <remarks>
    /// Windows Installer's own browse dialog picks folders, not files, which is why the field for the certificate path
    /// was a plain text box with a path nobody can remember. This is the whole reason this project exists.
    /// </remarks>
    [CustomAction]
    public static ActionResult BrowseForCertificate(Session session)
    {
        var current = session["TLSCERTIFICATEPATH"];
        if (string.IsNullOrWhiteSpace(current) || current.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            current = string.Empty;
        }

        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Title = "选择证书文件（.pfx）",
            Filter = "证书文件 (*.pfx;*.p12)|*.pfx;*.p12|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            FileName = current,
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            session["TLSCERTIFICATEPATH"] = dialog.FileName;
        }

        return ActionResult.Success;
    }

    /// <summary>
    /// Says whether the database address in the connection string accepts a connection at all.
    /// </summary>
    /// <remarks>
    /// A reachability test, not the full one: this assembly cannot use Npgsql (net48), so what it can honestly answer
    /// is "nothing is listening where you said", which is the mistake people actually make. Whether the credentials
    /// work and the schema can be created is checked by the deferred configuration action, where the server's own
    /// code runs.
    /// </remarks>
    [CustomAction]
    public static ActionResult CheckDatabaseAddress(Session session)
    {
        var connectionString = session["POSTGRESCONNECTIONSTRING"];
        if (string.IsNullOrWhiteSpace(connectionString) || connectionString.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            session["DBCHECKRESULT"] = "未配置数据库：设备代码只存在内存里，信令服务重启后会全部改变。";
            return ActionResult.Success;
        }

        if (!TryReadEndpoint(connectionString, out var host, out var port, out var problem))
        {
            session["DBCHECKRESULT"] = problem;
            session["DBCHECKFAILED"] = "1";
            return ActionResult.Success;
        }

        try
        {
            using var client = new TcpClient();
            var connected = client.ConnectAsync(host, port).Wait(ConnectTimeout);
            if (!connected || !client.Connected)
            {
                session["DBCHECKRESULT"] = $"{host}:{port} 没有响应（{ConnectTimeout.TotalSeconds:0} 秒）：数据库没在跑，或者地址/端口写错了。";
                session["DBCHECKFAILED"] = "1";
                return ActionResult.Success;
            }

            session["DBCHECKRESULT"] = $"{host}:{port} 可以连接。用户名、口令与建表权限由安装程序随后验证。";
            session["DBCHECKFAILED"] = string.Empty;
            return ActionResult.Success;
        }
        catch (Exception ex) when (ex is SocketException or AggregateException or InvalidOperationException)
        {
            session["DBCHECKRESULT"] = $"{host}:{port} 连不上：{ex.GetBaseException().Message}";
            session["DBCHECKFAILED"] = "1";
            return ActionResult.Success;
        }
    }

    /// <summary>
    /// Pulls the host and port out of a PostgreSQL connection string, without a PostgreSQL client library.
    /// </summary>
    private static bool TryReadEndpoint(string connectionString, out string host, out int port, out string problem)
    {
        host = "127.0.0.1";
        port = 5432;
        problem = string.Empty;

        foreach (var part in connectionString.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator < 0)
            {
                continue;
            }

            var name = part.Substring(0, separator).Trim();
            var value = part.Substring(separator + 1).Trim();
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) || name.Equals("Server", StringComparison.OrdinalIgnoreCase))
            {
                host = value;
            }
            else if (name.Equals("Port", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || port is <= 0 or > ushort.MaxValue)
                {
                    problem = $"连接串里的端口 '{value}' 不是一个端口号。";
                    return false;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            problem = "连接串里没有 Host=，不知道要连哪台机器。";
            return false;
        }

        return true;
    }
}
