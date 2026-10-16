using Tmds.DBus.Protocol;

namespace RemoteDesktop.Platform.Linux.XdgPortal;

/// <summary>What a portal request answered, in terms this project uses.</summary>
/// <param name="Response">The portal's verdict: 0 agreed, 1 cancelled, 2 refused.</param>
/// <param name="SessionHandle">The session the request created, for <c>CreateSession</c>; otherwise <c>null</c>.</param>
/// <param name="Streams">The screens the operator agreed to share, for <c>Start</c>; otherwise empty.</param>
/// <param name="RestoreToken">What to present next time, when the portal offers to remember the grant.</param>
/// <param name="Failure">
/// Why the answer could not be read at all, or <c>null</c>. Travels back as part of the result rather than as a
/// thrown exception, for the reason given on <see cref="Read"/>.
/// </param>
internal readonly record struct PortalRequestResult(
    uint Response,
    string? SessionHandle,
    IReadOnlyList<SharedScreen> Streams,
    string? RestoreToken,
    string? Failure)
{
    /// <summary>
    /// Reads a <c>Response</c> signal, or the reply to a request that answers immediately.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every portal request answers the same way — a <c>uint</c> verdict followed by an <c>a{sv}</c> whose keys
    /// depend on which request it was — so one reader covers all of them and ignores the keys it does not know.
    /// </para>
    /// <para>
    /// It is written so that it cannot throw, which is not defensive habit but a requirement of how it is called.
    /// This runs inside the message subscription, and a subscription whose reader throws reports nothing at all —
    /// no value, no error, nothing the caller can observe. The request then looks exactly like one the desktop never
    /// answered: the caller waits out its whole timeout and is told the portal did not reply, when in fact it replied
    /// immediately. That is what happened here, from a single call to the wrong accessor. Reporting the failure as
    /// part of the result is what keeps a mistake in this file a mistake in this file.
    /// </para>
    /// </remarks>
    public static PortalRequestResult Read(Message message)
    {
        try
        {
            return ReadCore(message);
        }
#pragma warning disable CA1031 // Deliberate: nothing may escape into a subscription, or the answer is lost silently.
        catch (Exception error)
#pragma warning restore CA1031
        {
            return new PortalRequestResult(0, null, [], null, $"{error.GetType().Name}: {error.Message}");
        }
    }

    private static PortalRequestResult ReadCore(Message message)
    {
        var reader = message.GetBodyReader();
        var response = reader.ReadUInt32();

        string? session = null;
        string? restore = null;
        IReadOnlyList<SharedScreen> streams = [];

        foreach (var (key, value) in reader.ReadDictionaryOfStringToVariantValue())
        {
            switch (key)
            {
                case "session_handle":
                    session = ReadText(value);
                    break;

                case "restore_token":
                    restore = ReadText(value);
                    break;

                case "streams":
                    streams = ReadStreams(value);
                    break;

                default:
                    // Each request carries its own extra keys — `devices` for SelectDevices, `clipboards` for the
                    // clipboard portal. None of them are needed, and none of them are worth failing over.
                    break;
            }
        }

        return new PortalRequestResult(response, session, streams, restore, null);
    }

    /// <summary>A string value, whichever of the two string types it arrived as.</summary>
    /// <remarks>
    /// The portal documents these as object paths and GNOME's backend sends at least one of them — the session
    /// handle — as a plain string. Being strict about which one it should have been does not make it arrive as that;
    /// it only decides whether the answer can be read. Both are accepted, and anything else is not a string.
    /// </remarks>
    private static string? ReadText(VariantValue value) => value.Type switch
    {
        VariantValueType.String => value.GetString(),
        VariantValueType.ObjectPath => value.GetObjectPathAsString(),
        _ => null,
    };

    /// <summary>Reads <c>a(ua{sv})</c>: one entry per shared screen.</summary>
    /// <remarks>
    /// Walked through the <c>VariantValue</c> reach rather than through the message's reader. A variant holds its own
    /// position, so this cannot drift out of step with a reader that is somewhere else entirely.
    /// </remarks>
    private static List<SharedScreen> ReadStreams(VariantValue value)
    {
        var streams = new List<SharedScreen>();

        for (var i = 0; i < value.Count; i++)
        {
            var element = value.GetItem(i);
            if (element.Type != VariantValueType.Struct || element.Count < 2)
            {
                continue;
            }

            // (u node_id, a{sv} properties)
            var node = element.GetItem(0).GetUInt32();
            var properties = element.GetItem(1);

            var position = ReadPair(properties, "position");
            var size = ReadPair(properties, "size");

            streams.Add(new SharedScreen(node, position.X, position.Y, size.X, size.Y));
        }

        return streams;
    }

    /// <summary>One <c>(ii)</c> out of a stream's properties, or the origin when it is not there.</summary>
    private static (int X, int Y) ReadPair(VariantValue properties, string name)
    {
        for (var i = 0; i < properties.Count; i++)
        {
            var entry = properties.GetDictionaryEntry(i);
            if (!string.Equals(ReadText(entry.Key), name, StringComparison.Ordinal))
            {
                continue;
            }

            var value = entry.Value;
            if (value.Type == VariantValueType.Struct && value.Count >= 2)
            {
                return (value.GetItem(0).GetInt32(), value.GetItem(1).GetInt32());
            }
        }

        // A compositor that does not describe its screens' geometry is not a reason to refuse the session; the
        // picture still arrives, it just cannot be placed against the others.
        return (0, 0);
    }
}
