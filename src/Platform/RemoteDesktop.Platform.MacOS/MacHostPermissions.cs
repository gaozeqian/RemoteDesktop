using System.Runtime.Versioning;

namespace RemoteDesktop.Platform.MacOS;

/// <summary>
/// Asks for the two grants a macOS controlled end cannot work without, and says what to do when macOS will not ask
/// again.
/// </summary>
/// <remarks>
/// <para>
/// The packaged host called this from its entry point; the client's in-process controlled end did not, so after the
/// host package was removed the fallback hosted, captured and connected but silently dropped every input event until
/// somebody granted Accessibility by hand. The requirement belongs to "this process is the controlled end", not to
/// which binary hosts it, so both callers share this one implementation.
/// </para>
/// <para>
/// A package cannot grant these: they are per-bundle decisions the person makes in System Settings. Requesting them
/// means the first login after installation shows the system prompt instead of the host silently capturing nothing.
/// Screen Recording is only read when a process starts, so the grant is followed by a restart.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public static class MacHostPermissions
{
    /// <summary>Requests Screen Recording and Accessibility, and records the sentences in the data directory.</summary>
    /// <param name="dataDirectory">Where <c>logs/permissions.log</c> belongs; the same place the packaged host uses.</param>
    public static void Request(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        foreach (var grant in new[] { TccGrant.ScreenRecording, TccGrant.Accessibility })
        {
            if (TccGrants.IsGranted(grant))
            {
                continue;
            }

            _ = TccGrants.Request(grant);
            var message = TccGrants.Describe(grant);
            Console.Error.WriteLine(message);

            // A LaunchAgent has no terminal. The same sentence goes next to the other logs, which is where the
            // installer and the documentation tell an operator to look when a host is online but captures nothing.
            try
            {
                var directory = Path.Combine(dataDirectory, "logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "permissions.log"),
                    $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The system prompt is still the primary signal; failing to write the note must not stop the host.
            }
        }
    }
}
