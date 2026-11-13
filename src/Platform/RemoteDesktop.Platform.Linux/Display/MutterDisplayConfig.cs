using System.Runtime.Versioning;
using Tmds.DBus.Protocol;

namespace RemoteDesktop.Platform.Linux.Display;

/// <summary>One monitor as Mutter describes it, with the pieces this project needs.</summary>
/// <param name="Connector">The port it is plugged into, such as <c>DP-1</c>. Stable for the life of the session.</param>
/// <param name="DisplayName">What the monitor calls itself, for showing to a person.</param>
/// <param name="Geometry">Where it is and how big, in both coordinate systems.</param>
public readonly record struct MutterMonitor(string Connector, string DisplayName, CompositorMonitor Geometry);

/// <summary>
/// Reads the monitor layout from Mutter over the session bus.
/// </summary>
/// <remarks>
/// <para>
/// This is the display list that costs nothing to obtain. Measured on GNOME 50: reading it needs no permission
/// prompt and no portal session, so the host can tell a controller what screens exist before anybody has agreed to
/// share one — which is what lets the consent dialog appear at the moment a screen is actually asked for, rather
/// than at startup or on the first connection.
/// </para>
/// <para>
/// It is specific to Mutter, and the callers treat it as the fallback: once a screen-cast session exists, the portal's
/// own view of the desktop is authoritative, because only the portal knows which screens the operator actually
/// ticked. See <see cref="WaylandDisplayEnumerator"/>.
/// </para>
/// <para>
/// Reading it is synchronous, because the port it implements is. The call is made from the streaming loop, which
/// <c>VideoChannelModule</c> runs inside <c>Task.Run</c>, so the wait occupies a thread-pool thread and never the UI
/// thread. It also never blocks the connection's own read loop, which runs as a task of its own. That is a property
/// of where it is called from rather than of this class, so it is written down here: calling this from a UI thread
/// would freeze the window for the length of a bus round trip.
/// </para>
/// <para>
/// <b>The whole walk is written in one method on purpose.</b> <c>Reader</c> cannot be handed to a helper — not by
/// value, and, measured here, not by <c>ref</c> either. The helper appears to work and returns nothing: every
/// dictionary comes back empty and the caller's position is left somewhere it should not be, so the failure surfaces
/// hundreds of bytes later as "unexpected end of data" or "invalid UTF-8" in a field that has nothing to do with it.
/// This is the same constraint that applies to <c>MessageWriter</c> and delegates, and it is a property of the
/// library's <c>ref struct</c> cursors rather than of this message. Only <c>VariantValue</c>, which carries its own
/// bytes, may cross a method boundary.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class MutterDisplayConfig : IDisposable
{
    private const string Service = "org.gnome.Mutter.DisplayConfig";
    private const string Path = "/org/gnome/Mutter/DisplayConfig";
    private const string Interface = "org.gnome.Mutter.DisplayConfig";

    private readonly DBusConnection _connection;

    public MutterDisplayConfig()
    {
        var address = DBusAddress.Session
            ?? throw new InvalidOperationException("There is no session bus, so the monitor layout cannot be read.");

        _connection = new DBusConnection(address);
        _connection.ConnectAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Whether anything is there to answer. Mutter is not the only compositor this has to run under.</summary>
    public static bool IsAvailable()
    {
        // Asked of the bus rather than by calling: a compositor that does not implement this answers with an error,
        // and an error read as an empty layout would look like a machine with no screens.
        try
        {
            using var config = new MutterDisplayConfig();
            return config.Read().Count > 0;
        }
        catch (Exception error) when (error is DBusExceptionBase or InvalidOperationException or TimeoutException)
        {
            return false;
        }
    }

    /// <summary>The monitors, in the order Mutter lists them.</summary>
    public IReadOnlyList<MutterMonitor> Read() => ReadAsync().GetAwaiter().GetResult();

    public async Task<IReadOnlyList<MutterMonitor>> ReadAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var w = _connection.GetMessageWriter();
        w.WriteMethodCallHeader(Service, Path, Interface, "GetCurrentState", string.Empty, MessageFlags.None);
        var message = w.CreateMessage();

        return await _connection
            .CallMethodAsync(message, static (Message m, object? _) => Parse(m), null)
            .ConfigureAwait(false);
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>
    /// Reads <c>ua((ssss)a(siiddada{sv})a{sv})a(iiduba(ssss)a{sv})a{sv}</c>.
    /// </summary>
    /// <remarks>
    /// The layout arrives in two halves that only make sense together: a list of monitors, each with all the modes it
    /// supports, and a separate list of logical monitors saying where each one is on the desktop and at what scale.
    /// The size of a monitor is not in either — it is the size of whichever mode is marked <c>is-current</c>. That is
    /// why the modes are walked rather than skipped: taking the first mode, or the preferred one, would report the
    /// wrong size on any monitor whose current mode is not its preferred one, which is most of them after somebody
    /// has changed a setting.
    /// </remarks>
    private static List<MutterMonitor> Parse(Message message)
    {
        var reader = message.GetBodyReader();
        _ = reader.ReadUInt32(); // serial, which changes on every layout change and is only useful for SetDisplayConfig

        var modes = new Dictionary<string, (int Width, int Height)>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        var monitors = reader.ReadArrayStart(DBusType.Struct);
        while (reader.HasNext(monitors))
        {
            reader.AlignStruct();

            var connector = reader.ReadString();
            _ = reader.ReadString(); // vendor
            _ = reader.ReadString(); // product
            _ = reader.ReadString(); // serial number

            var displayName = connector;

            var modeList = reader.ReadArrayStart(DBusType.Struct);
            while (reader.HasNext(modeList))
            {
                reader.AlignStruct();

                _ = reader.ReadString(); // mode id, such as "2560x1440@59.951"
                var width = reader.ReadInt32();
                var height = reader.ReadInt32();
                _ = reader.ReadDouble(); // refresh rate
                _ = reader.ReadDouble(); // preferred scale
                _ = reader.ReadArrayOfDouble(); // the scales this mode supports

                var modeProperties = new Dictionary<string, object>(StringComparer.Ordinal);
                var modeEntries = reader.ReadDictionaryStart();
                while (reader.HasNext(modeEntries))
                {
                    reader.AlignStruct();
                    modeProperties[reader.ReadString()] = Unpack(reader.ReadVariantValue());
                }

                if (modeProperties.TryGetValue("is-current", out var flag) && flag is true)
                {
                    modes[connector] = (width, height);
                }
            }

            var monitorProperties = new Dictionary<string, object>(StringComparer.Ordinal);
            var monitorEntries = reader.ReadDictionaryStart();
            while (reader.HasNext(monitorEntries))
            {
                reader.AlignStruct();
                monitorProperties[reader.ReadString()] = Unpack(reader.ReadVariantValue());
            }

            if (monitorProperties.TryGetValue("display-name", out var name) && name is string text && text.Length > 0)
            {
                displayName = text;
            }

            // A monitor with no mode marked current is one that is plugged in but not driving anything. It is left
            // out of the dictionary rather than recorded as 0x0, because the logical loop below uses its absence to
            // skip it — a screen of no size is not a screen anybody can be shown.
            names[connector] = displayName;
        }

        var result = new List<MutterMonitor>();

        var logical = reader.ReadArrayStart(DBusType.Struct);
        while (reader.HasNext(logical))
        {
            reader.AlignStruct();

            var x = reader.ReadInt32();
            var y = reader.ReadInt32();
            var scale = reader.ReadDouble();
            var transform = reader.ReadUInt32();
            var isPrimary = reader.ReadBool();

            // A logical monitor can cover several monitors that are being shown as one. The first is taken: it is the
            // one whose position and size the compositor uses for the whole group.
            string? connector = null;
            var covered = reader.ReadArrayStart(DBusType.Struct);
            while (reader.HasNext(covered))
            {
                reader.AlignStruct();

                // Every field is read even when it is not wanted. An element skipped early leaves the reader partway
                // through a struct, and everything after it is read from the wrong offset — which surfaces as
                // "unexpected end of data" somewhere unrelated, or, worse, as a layout that parses and is wrong.
                var name = reader.ReadString();
                _ = reader.ReadString();
                _ = reader.ReadString();
                _ = reader.ReadString();

                connector ??= name;
            }

            // Read to the end rather than skipped, for the same reason every unwanted field above is read: leaving
            // the reader partway through a value puts everything after it at the wrong offset.
            var logicalEntries = reader.ReadDictionaryStart();
            while (reader.HasNext(logicalEntries))
            {
                reader.AlignStruct();
                _ = reader.ReadString();
                _ = reader.ReadVariantValue();
            }

            if (connector is null || !modes.TryGetValue(connector, out var mode))
            {
                continue;
            }

            // Wayland has no rotation in the sense the transform values imply here, but a transformed monitor reports
            // its width and height the other way round from how it occupies the desktop. Swapping keeps the logical
            // rectangle matching what the compositor lays windows out in.
            var rotated = transform is 1 or 3 or 5 or 7;
            var geometry = new CompositorMonitor(
                x,
                y,
                scale <= 0 ? 1.0 : scale,
                rotated ? mode.Height : mode.Width,
                rotated ? mode.Width : mode.Height,
                isPrimary);

            result.Add(new MutterMonitor(connector, names.GetValueOrDefault(connector, connector), geometry));
        }

        return result;
    }

    /// <summary>Turns one variant into an ordinary value, while the message is still alive.</summary>
    /// <remarks>
    /// Takes the <c>VariantValue</c> rather than the reader, and that is the whole point: a variant carries its own
    /// bytes, so it is safe to hand to another method. A <c>Reader</c> is not — see the note on the class.
    /// </remarks>
    private static object Unpack(VariantValue value) => value.Type switch
    {
        VariantValueType.String => value.GetString(),
        VariantValueType.ObjectPath => value.GetObjectPathAsString(),
        VariantValueType.UInt32 => value.GetUInt32(),
        VariantValueType.Int32 => value.GetInt32(),
        VariantValueType.Bool => value.GetBool(),
        VariantValueType.Double => value.GetDouble(),

        // `supported-color-modes` and friends. Present, not needed, and reading them would mean walking a nested
        // array for nothing; the type name is enough to tell them apart in a log.
        _ => value.Type.ToString(),
    };
}
