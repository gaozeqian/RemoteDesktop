using RemoteDesktop.Transport;

namespace RemoteDesktop.Application.Hosting;

/// <summary>
/// Reads the settings a host cannot start without, and says why when it cannot.
/// </summary>
/// <remarks>
/// A mistyped server address used to end in an unhandled exception thrown while the host was being built — before any
/// log existed. The background service was then simply not running: the only trace was an entry from the runtime in
/// the event log, and the interface could say no more than that it could not reach the service. The address is a
/// setting an installer writes and an operator edits by hand, so the failure has to say which setting and where to
/// read about it.
/// </remarks>
public static class StartupSettings
{
    /// <summary>What a host exits with when it cannot read its settings; distinct from a crash.</summary>
    public const int InvalidSettingsExitCode = 3;

    /// <summary>
    /// The transport settings, or <c>null</c> when they are unusable — in which case <paramref name="reason"/> holds
    /// the sentence to show, and the same sentence has been appended to <paramref name="reportPath"/>.
    /// </summary>
    /// <param name="read">
    /// Binds the settings. A delegate rather than a configuration object because the binding itself is what throws
    /// when a value is the wrong shape, and that has to happen inside here; the hosts own the configuration, this
    /// layer only knows what a usable answer looks like.
    /// </param>
    /// <param name="reportPath">The file an operator is told to look at: the host's own log or crash log.</param>
    /// <param name="reason">Why the settings cannot be used; empty when they can.</param>
    public static TransportOptions? TryReadTransport(Func<TransportOptions> read, string reportPath, out string reason)
    {
        ArgumentNullException.ThrowIfNull(read);
        reason = string.Empty;

        try
        {
            var transport = read();
            transport.Validate();
            return transport;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
        {
            reason = $"The settings could not be read, so this program did not start: {ex.Message}";
            Report(reportPath, reason);
            return null;
        }
    }

    /// <summary>
    /// Writes the sentence down where somebody will look for it later. What is shown to whoever is at the screen is
    /// the caller's business: a service has a console at best, a windowed client has neither.
    /// </summary>
    private static void Report(string reportPath, string reason)
    {
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            return;
        }

        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] startup: {reason}{Environment.NewLine}";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.AppendAllText(reportPath, line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // The line on standard error is what is left; a host that cannot write its own log is not going to be
            // helped by throwing here as well.
        }
    }
}
