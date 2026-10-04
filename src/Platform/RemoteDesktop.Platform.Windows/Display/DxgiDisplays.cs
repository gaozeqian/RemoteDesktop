using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Display;
using Vortice.DXGI;

namespace RemoteDesktop.Platform.Windows.Display;

/// <summary>
/// Displays as DXGI outputs attached to the desktop. The id is the GDI device name (e.g. <c>\\.\DISPLAY1</c>);
/// bounds are physical pixels in virtual-desktop coordinates, which is what SendInput expects from a
/// per-monitor DPI aware process.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DxgiDisplayEnumerator : IDisplayEnumerator
{
    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var displays = new List<DisplayInfo>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var description = output.Description;
                        if (!description.AttachedToDesktop)
                        {
                            continue;
                        }

                        var rect = description.DesktopCoordinates;
                        var bounds = new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                        displays.Add(new DisplayInfo(
                            description.DeviceName,
                            description.DeviceName.TrimStart('\\', '.'),
                            bounds,
                            Scale: 1.0,
                            IsPrimary: rect.Left == 0 && rect.Top == 0));
                    }
                }
            }
        }

        return displays;
    }

    /// <summary>Finds the adapter and output for a display; the caller disposes both.</summary>
    internal static (IDXGIAdapter1 Adapter, IDXGIOutput Output)? Find(string deviceName)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                if (string.Equals(output.Description.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    return (adapter, output);
                }

                output.Dispose();
            }

            adapter.Dispose();
        }

        return null;
    }
}
