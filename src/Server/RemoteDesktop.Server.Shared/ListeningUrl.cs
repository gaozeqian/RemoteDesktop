using System.Diagnostics.CodeAnalysis;

namespace RemoteDesktop.Server.Shared;

/// <summary>
/// Reads the <c>Urls</c> list the way Kestrel does, so a check on "is this listener public plaintext" sees the same
/// addresses Kestrel will bind. <see cref="Uri"/> alone does not: it rejects the wildcard hosts <c>*</c> and <c>+</c>,
/// which Kestrel accepts and which mean every interface, and the value may hold several addresses separated by
/// semicolons.
/// </summary>
public static class ListeningUrl
{
    private const string AllInterfaces = "0.0.0.0";

    /// <summary>
    /// Parses one address. A wildcard host (<c>*</c> or <c>+</c>) is returned as <c>0.0.0.0</c>, which is what it
    /// binds, so it counts as non-loopback.
    /// </summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd > 0)
        {
            var hostStart = schemeEnd + 3;
            if (hostStart < text.Length && (text[hostStart] is '*' or '+'))
            {
                text = string.Concat(text.AsSpan(0, hostStart), AllInterfaces, text.AsSpan(hostStart + 1));
            }
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https")
        {
            uri = parsed;
            return true;
        }

        return false;
    }

    /// <summary>The individual addresses of a semicolon-separated <c>Urls</c> value.</summary>
    public static string[] Split(string? urls) =>
        string.IsNullOrWhiteSpace(urls)
            ? []
            : urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Whether any address in the list serves plain HTTP on something other than loopback.</summary>
    public static bool HasPublicPlaintext(string? urls)
    {
        foreach (var value in Split(urls))
        {
            if (TryParse(value, out var uri) && uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
            {
                return true;
            }
        }

        return false;
    }
}
