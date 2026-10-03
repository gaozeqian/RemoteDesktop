namespace RemoteDesktop.Server.Relay;

public sealed class RelayOptions
{
    public const string SectionName = "Relay";

    public int Port { get; set; } = 5082;

    /// <summary>Base64 HMAC key shared with the rendezvous server, which signs the tickets.</summary>
    public string SharedKey { get; set; } = string.Empty;

    /// <summary>A pairing without traffic for this long is dropped.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(2);
}
