using RemoteDesktop.Domain;

namespace RemoteDesktop.Application.Links;

/// <summary>
/// A <c>remotedesktop://</c> link: what the operating system hands the client when somebody clicks one.
/// </summary>
/// <remarks>
/// <para>
/// Two shapes are accepted, because both are what people end up writing by hand:
/// <c>remotedesktop://connect?code=123456789</c> and the shorthand <c>remotedesktop://123456789</c>. The code may be
/// grouped the way it is displayed ("123 456 789"), percent-encoded, or written with dashes.
/// </para>
/// <para>
/// Only the device code travels. A link never carries a password, and this type has nowhere to put one: the link
/// arrives from a browser, a chat message or an e-mail, so anything else it could carry would be a credential chosen
/// by whoever wrote the link. The client fills the code in and waits for the person to press 连接 — it does not dial
/// out on its own, because a link that connects silently is a link that decides whom you are connecting to.
/// </para>
/// </remarks>
public readonly record struct ConnectionLink
{
    /// <summary>The scheme the installers register on every platform that supports one.</summary>
    public const string Scheme = "remotedesktop";

    /// <summary>Long enough for the longest accepted shape; anything longer is not a link this program wrote.</summary>
    private const int MaxLength = 256;

    private const string ConnectAction = "connect";
    private const string CodeParameter = "code";

    private ConnectionLink(DeviceId device) => Device = device;

    /// <summary>The device the link points at.</summary>
    public DeviceId Device { get; }

    /// <summary>Parses a link, returning <c>false</c> for anything that is not one this client understands.</summary>
    /// <param name="text">The whole URL, as the shell passed it.</param>
    /// <param name="link">The parsed link when this returns <c>true</c>.</param>
    public static bool TryParse(string? text, out ConnectionLink link)
    {
        link = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength)
        {
            return false;
        }

        var value = text.Trim();
        if (!value.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = value[Scheme.Length..];
        if (rest.StartsWith("://", StringComparison.Ordinal))
        {
            rest = rest[3..];
        }
        else if (rest.StartsWith(':'))
        {
            // "remotedesktop:123456789" — the form a shell or a hand-written link may use.
            rest = rest[1..];
        }
        else
        {
            return false;
        }

        // A browser may append a trailing slash to an empty authority, and people paste what they see.
        while (rest.StartsWith('/'))
        {
            rest = rest[1..];
        }

        var queryStart = rest.IndexOf('?', StringComparison.Ordinal);
        var head = queryStart < 0 ? rest : rest[..queryStart];
        var query = queryStart < 0 ? string.Empty : rest[(queryStart + 1)..];

        head = head.TrimEnd('/');
        if (head.Length == 0)
        {
            // "remotedesktop://?code=…" has no action to run.
            return false;
        }

        DeviceId device;
        if (string.Equals(head, ConnectAction, StringComparison.OrdinalIgnoreCase))
        {
            var code = ReadParameter(query, CodeParameter);
            if (!TryReadCode(code, out device))
            {
                return false;
            }
        }
        else
        {
            // The shorthand carries the code where the authority would be; a query alongside it is not something
            // this parser wrote, so it has to be empty rather than silently ignored. Browsers percent-encode spaces
            // in an authority, just as they do in the query form, so decode before applying the displayed-code rules.
            if (query.Length > 0 || !TryReadCode(Uri.UnescapeDataString(head), out device))
            {
                return false;
            }
        }

        link = new ConnectionLink(device);
        return true;
    }

    private static string? ReadParameter(string query, string name)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            if (string.Equals(pair[..separator], name, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        return null;
    }

    private static bool TryReadCode(string? code, out DeviceId device)
    {
        device = default;
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        // DeviceId.TryParse already tolerates the spaces used for display; dashes are the other way people group a
        // code they were given over the phone.
        return DeviceId.TryParse(code.Replace("-", string.Empty, StringComparison.Ordinal), out device);
    }
}
