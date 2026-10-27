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
    /// hand-edited database or an older version left behind.
    /// </summary>
    public static bool TokenCanTravelTo(string baseUrl, string? nodeToken)
    {
        if (string.IsNullOrEmpty(nodeToken))
        {
            return true;
        }

        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback);
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
