using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RemoteDesktop.Platform.Windows.UnitTests;

/// <summary>An installer database, opened read-only through the Windows Installer API.</summary>
internal sealed class MsiPackage : IDisposable
{
    private readonly object _database;

    public MsiPackage(string path)
    {
        var type = Type.GetTypeFromProgID("WindowsInstaller.Installer")
            ?? throw new InvalidOperationException("Windows Installer is not available.");
        var installer = Activator.CreateInstance(type)!;
        _database = installer.GetType().InvokeMember("OpenDatabase", BindingFlags.InvokeMethod, null, installer, [path, 0], CultureInfo.InvariantCulture)!;
    }

    /// <summary>The rows of a query as string arrays; an empty list when the table does not exist.</summary>
    /// <remarks>The installer's SQL subset has no LIKE, so tables are read whole and filtered by the caller.</remarks>
    public List<string[]> Rows(string sql, int columns)
    {
        object view;
        try
        {
            view = _database.GetType().InvokeMember("OpenView", BindingFlags.InvokeMethod, null, _database, [sql], CultureInfo.InvariantCulture)!;
        }
        catch (TargetInvocationException)
        {
            return [];
        }

        view.GetType().InvokeMember("Execute", BindingFlags.InvokeMethod, null, view, [null], CultureInfo.InvariantCulture);
        var rows = new List<string[]>();
        while (true)
        {
            object? record;
            try
            {
                record = view.GetType().InvokeMember("Fetch", BindingFlags.InvokeMethod, null, view, null, CultureInfo.InvariantCulture);
            }
            catch (COMException)
            {
                break; // no more rows
            }

            if (record is null)
            {
                break;
            }

            var row = new string[columns];
            for (var i = 1; i <= columns; i++)
            {
                row[i - 1] = Read(record, i);
            }

            rows.Add(row);
        }

        return rows;
    }

    // A column holds text or a number; anything missing comes back as an empty string.
    private static string Read(object record, int index)
    {
        try
        {
            return (string)record.GetType().InvokeMember("StringData", BindingFlags.GetProperty, null, record, [index], CultureInfo.InvariantCulture)!;
        }
        catch (TargetInvocationException)
        {
        }

        try
        {
            return Convert.ToString(record.GetType().InvokeMember("IntegerData", BindingFlags.GetProperty, null, record, [index], CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)!;
        }
        catch (TargetInvocationException)
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        // The database closes when it is collected; there is nothing to release explicitly.
    }
}
