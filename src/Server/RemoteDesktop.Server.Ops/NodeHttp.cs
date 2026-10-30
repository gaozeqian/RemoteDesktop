using System.Text;

namespace RemoteDesktop.Server.Ops;

/// <summary>
/// Reads node responses with a hard size limit.
/// </summary>
/// <remarks>
/// A node is an administrative target, but it is still a remote process: a compromised or misconfigured one can answer
/// <c>/metrics</c> or <c>/internal/v1/devices</c> with a body large enough to exhaust the console's memory. The
/// <see cref="HttpClient.MaxResponseContentBufferSize"/> on the named clients only covers buffered convenience reads;
/// the streaming call sites here need the limit applied while reading.
/// </remarks>
internal static class NodeHttp
{
    /// <summary>The largest response the console accepts from a node.</summary>
    public const int MaxResponseBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Whether a node's address can carry the configured node token. HTTP to a non-loopback address would put the
    /// bearer credential on the wire in the clear; the node API refuses such a row on create, and this catches one a
    /// hand-edited database or an older version left behind. A non-empty <paramref name="allowedHosts"/> narrows it
    /// further to the servers the deployment named.
    /// </summary>
    public static bool TokenCanTravelTo(
        string baseUrl,
        string? nodeToken,
        bool allowInsecureHttp = false,
        IReadOnlyCollection<string>? allowedHosts = null)
    {
        if (string.IsNullOrEmpty(nodeToken))
        {
            return true;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback && !(allowInsecureHttp && uri.Scheme == Uri.UriSchemeHttp))
        {
            return false;
        }

        return HostMayReceiveToken(uri, allowedHosts);
    }

    /// <summary>Why the node token must not be sent to this address, or <c>null</c> when it may be.</summary>
    /// <remarks>
    /// One sentence, so the scrape failure, the node API's refusal and the action error all say the same thing. The two
    /// reasons are kept apart on purpose: a plain-http address is a transport mistake, an unlisted host is a policy one.
    /// </remarks>
    public static string? RefusalReason(string baseUrl, OpsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrEmpty(options.NodeToken))
        {
            return null;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return "the node address is not an absolute URL";
        }

        if (TokenCanTravelTo(baseUrl, options.NodeToken, options.AllowInsecureNodeHttp, options.NodeTokenHosts))
        {
            return null;
        }

        return uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback && !options.AllowInsecureNodeHttp
            ? "refusing to send NodeToken over plain HTTP to a non-loopback address (use https, a loopback address, or Ops:AllowInsecureNodeHttp)"
            : $"refusing to send NodeToken to '{uri.Host}', which is not listed in Ops:NodeTokenHosts";
    }

    /// <summary>
    /// Where the token may go: loopback always (the nodes the installer seeds), then the configured list, then — when
    /// nothing is listed — any host the scheme rules already allowed.
    /// </summary>
    private static bool HostMayReceiveToken(Uri uri, IReadOnlyCollection<string>? allowedHosts)
    {
        if (uri.IsLoopback || allowedHosts is null || allowedHosts.Count == 0)
        {
            return true;
        }

        foreach (var allowed in allowedHosts)
        {
            if (string.Equals(allowed?.Trim(), uri.Host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads the body, failing as soon as it grows past <paramref name="maxBytes"/>.</summary>
    public static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.Headers.ContentLength is { } length && length > maxBytes)
        {
            throw new NodeResponseTooLargeException(maxBytes);
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new NodeResponseTooLargeException(maxBytes);
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Reads the body as UTF-8 text, with the same limit.</summary>
    public static async Task<string> ReadStringBoundedAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedAsync(content, maxBytes, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }
}

/// <summary>A node answered with more bytes than the console will buffer.</summary>
internal sealed class NodeResponseTooLargeException(int maxBytes)
    : Exception($"the response was larger than the {maxBytes / (1024 * 1024)} MB limit");
