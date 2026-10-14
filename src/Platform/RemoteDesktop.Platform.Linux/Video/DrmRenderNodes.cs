using System.Runtime.Versioning;

namespace RemoteDesktop.Platform.Linux.Video;

/// <summary>One GPU's render node, with what the kernel says it is.</summary>
/// <param name="Path">The device node to open, e.g. <c>/dev/dri/renderD128</c>.</param>
/// <param name="Vendor">The PCI vendor: 0x8086 Intel, 0x1002 AMD, 0x10de NVIDIA.</param>
/// <param name="Driver">The kernel driver in use, e.g. <c>amdgpu</c>, <c>i915</c>, <c>nvidia</c>.</param>
public readonly record struct DrmRenderNode(string Path, uint Vendor, string Driver)
{
    /// <summary>Whether the driver brings its own encoder, which is not reached through VA-API.</summary>
    public bool IsNvidia => Vendor == 0x10de;
}

/// <summary>
/// Finds the GPUs on this machine.
/// </summary>
/// <remarks>
/// <para>
/// The node numbers are not a way to tell the GPUs apart: <c>renderD128</c> is whichever one the kernel registered
/// first, which changes with a driver update, an added card or a different boot. Nothing here assumes anything from
/// an index — the vendor and driver are read out of sysfs and the capability is established by trying it.
/// </para>
/// <para>
/// With no GPU at all this returns nothing rather than failing, and the software encoder carries the session. That
/// is the normal case on a server, and it is why the caller must not treat an empty list as an error.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public static class DrmRenderNodes
{
    private const string SysfsDrm = "/sys/class/drm";
    private const string DeviceDirectory = "/dev/dri";

    /// <summary>Every render node the kernel exposes, in a stable order.</summary>
    public static IReadOnlyList<DrmRenderNode> Enumerate()
    {
        if (!Directory.Exists(SysfsDrm))
        {
            return [];
        }

        var nodes = new List<DrmRenderNode>();
        foreach (var entry in Directory.EnumerateDirectories(SysfsDrm, "renderD*").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(entry);
            var path = Path.Combine(DeviceDirectory, name);
            if (!File.Exists(path))
            {
                continue;
            }

            nodes.Add(new DrmRenderNode(path, ReadVendor(entry), ReadDriver(entry)));
        }

        return nodes;
    }

    private static uint ReadVendor(string sysfsDirectory)
    {
        // The file holds "0x1002" and a newline; an unreadable one means an unknown vendor rather than a failure,
        // since the capability probe does not depend on knowing who made the card.
        var text = ReadFirstLine(Path.Combine(sysfsDirectory, "device", "vendor"));
        return text is not null && text.StartsWith("0x", StringComparison.Ordinal)
            && uint.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var vendor)
            ? vendor
            : 0;
    }

    private static string ReadDriver(string sysfsDirectory)
    {
        // device/driver is a symlink into the bus's driver directory, e.g. ../../../bus/pci/drivers/amdgpu, so the
        // last segment of the target is the driver's name.
        var link = new DirectoryInfo(Path.Combine(sysfsDirectory, "device", "driver"));
        if (!link.Exists)
        {
            return "unknown";
        }

        return link.LinkTarget is { Length: > 0 } target
            ? Path.GetFileName(target.TrimEnd('/'))
            : link.Name;
    }

    private static string? ReadFirstLine(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllLines(path).FirstOrDefault()?.Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
