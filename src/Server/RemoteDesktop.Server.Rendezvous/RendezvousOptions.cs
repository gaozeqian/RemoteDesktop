namespace RemoteDesktop.Server.Rendezvous;

public sealed class RendezvousOptions
{
    public const string SectionName = "Rendezvous";

    /// <summary>UDP port of the address reflector; 0 picks a free port (tests).</summary>
    public int ReflectorPort { get; set; } = 5081;

    /// <summary>How long a connect request may wait for the host to answer.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Relay endpoint handed to clients, as "host:port". Empty disables the relay fallback.</summary>
    public string RelayEndpoint { get; set; } = string.Empty;

    /// <summary>Base64 HMAC key shared with the relay.</summary>
    public string RelaySharedKey { get; set; } = string.Empty;

    public TimeSpan RelayTicketLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Optional PostgreSQL connection string. When provided, device ids are persisted across restarts.</summary>
    public string? PostgresConnectionString { get; set; }

    /// <summary>
    /// Checks the settings the server cannot work without. Called when the options are first used (ValidateOnStart), so
    /// a misconfigured deployment stops at start-up with a sentence instead of failing later on the first connection.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrEmpty(RelayEndpoint))
        {
            return;
        }

        if (!System.Net.IPEndPoint.TryParse(RelayEndpoint, out _))
        {
            throw new InvalidOperationException($"Rendezvous:RelayEndpoint must look like host:port (got '{RelayEndpoint}').");
        }

        // The key signs the tickets that let a peer use the relay; an empty or short one would let anybody relay through
        // this server. The relay checks the same thing on its side, so a mismatch is caught at start-up as well.
        var key = Array.Empty<byte>();
        try
        {
            key = string.IsNullOrEmpty(RelaySharedKey) ? [] : Convert.FromBase64String(RelaySharedKey);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Rendezvous:RelaySharedKey must be base64 (generate one with: openssl rand -base64 48).");
        }

        if (key.Length < 32)
        {
            throw new InvalidOperationException(
                $"Rendezvous:RelaySharedKey must be at least 32 bytes when Rendezvous:RelayEndpoint is set (it is {(key.Length == 0 ? "empty" : $"only {key.Length} bytes")}). Generate one with: openssl rand -base64 48");
        }
    }
}
