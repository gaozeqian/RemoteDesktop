namespace RemoteDesktop.Domain;

/// <summary>
/// Where the signalling server is, as somebody typed it.
/// </summary>
/// <remarks>
/// <para>
/// Here rather than beside the Windows code that first needed it, because it is not a Windows rule: the address is
/// typed by hand on both platforms, and it reaches a process that will use it to open an outbound connection and,
/// on the controlled end, as a value that decides who may reach this machine. Two validators — one per platform —
/// would be two answers to the same question, and the interesting failures are the addresses where they disagree.
/// </para>
/// <para>
/// The host is held to what a DNS name or an IP literal can contain. That is stricter than <see cref="Uri"/> needs
/// to be: the value is typed by hand into a box, and a "host" carrying quotes or semicolons is somebody's fragment
/// of something else.
/// </para>
/// </remarks>
public readonly record struct RendezvousAddress
{
    private RendezvousAddress(Uri value) => Value = value;

    public Uri Value { get; }

    /// <summary>As it will be written into the settings file.</summary>
    public string AbsoluteUri => Value.AbsoluteUri;

    /// <returns><c>true</c> when <paramref name="address"/> is a <c>ws://</c> or <c>wss://</c> address with a host.</returns>
    public static bool TryParse(string? address, out RendezvousAddress parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var candidate)
            || candidate.Scheme is not ("ws" or "wss")
            || candidate.Host.Length == 0)
        {
            return false;
        }

        // The brackets around an IPv6 literal are not part of the address grammar this check is about.
        if (candidate.HostNameType != UriHostNameType.IPv6
            && !candidate.Host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
        {
            return false;
        }

        parsed = new RendezvousAddress(candidate);
        return true;
    }

    public static RendezvousAddress Parse(string address) =>
        TryParse(address, out var parsed)
            ? parsed
            : throw new FormatException($"'{address}' is not a ws:// or wss:// address.");

    public override string ToString() => AbsoluteUri;
}
