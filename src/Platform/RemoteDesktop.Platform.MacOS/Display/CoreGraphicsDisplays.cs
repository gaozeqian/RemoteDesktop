using System.Runtime.Versioning;
using RemoteDesktop.Abstractions.Display;
using RemoteDesktop.Platform.MacOS.Interop;

namespace RemoteDesktop.Platform.MacOS.Display;

/// <summary>
/// The displays CoreGraphics reports as online.
/// </summary>
/// <remarks>
/// Bounds are in the global display coordinate space: origin at the top-left of the main display, units of points,
/// which is the space <c>CGEvent</c> takes positions in. Windows uses pixels here; on a Retina Mac a point covers
/// several pixels, so <see cref="DisplayInfo.Scale"/> carries the backing scale and the capturer reports pixel
/// dimensions from the display mode. Pointer input is normalized (0..1) to a display precisely so this difference
/// never has to be reconciled downstream.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class CoreGraphicsDisplays : IDisplayEnumerator
{
    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var count = 0u;
        unsafe
        {
            if (CoreGraphics.CGGetOnlineDisplayList(0, null, &count) != CoreGraphics.Success || count == 0)
            {
                return [];
            }

            var ids = new uint[count];
            fixed (uint* p = ids)
            {
                if (CoreGraphics.CGGetOnlineDisplayList(count, p, &count) != CoreGraphics.Success)
                {
                    return [];
                }
            }

            var main = CoreGraphics.CGMainDisplayID();
            var displays = new List<DisplayInfo>((int)count);
            for (var i = 0; i < count; i++)
            {
                var id = ids[i];
                var bounds = CoreGraphics.CGDisplayBounds(id);
                displays.Add(new DisplayInfo(
                    Id: id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Name: Describe(id, i),
                    Bounds: new PixelRect((int)Math.Round(bounds.Origin.X), (int)Math.Round(bounds.Origin.Y), bounds.Width, bounds.Height),
                    Scale: BackingScale(id),
                    IsPrimary: id == main));
            }

            return displays;
        }
    }

    /// <summary>Resolves a display id back to the CoreGraphics id used by the capturer and the injector.</summary>
    public static bool TryParse(string displayId, out uint display)
    {
        if (uint.TryParse(displayId, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out display))
        {
            return true;
        }

        display = 0;
        return false;
    }

    /// <summary>Pixel width and height of the display's current mode, which is what a captured frame will be.</summary>
    public static (int Width, int Height) PixelSize(uint display)
    {
        var mode = CoreGraphics.CGDisplayCopyDisplayMode(display);
        if (mode == 0)
        {
            return ((int)CoreGraphics.CGDisplayPixelsWide(display), (int)CoreGraphics.CGDisplayPixelsHigh(display));
        }

        try
        {
            return ((int)CoreGraphics.CGDisplayModeGetPixelWidth(mode), (int)CoreGraphics.CGDisplayModeGetPixelHeight(mode));
        }
        finally
        {
            CoreGraphics.CGDisplayModeRelease(mode);
        }
    }

    private static double BackingScale(uint display)
    {
        var mode = CoreGraphics.CGDisplayCopyDisplayMode(display);
        if (mode == 0)
        {
            return 1.0;
        }

        try
        {
            var points = (double)CoreGraphics.CGDisplayModeGetWidth(mode);
            var pixels = (double)CoreGraphics.CGDisplayModeGetPixelWidth(mode);
            return points > 0 && pixels > 0 ? pixels / points : 1.0;
        }
        finally
        {
            CoreGraphics.CGDisplayModeRelease(mode);
        }
    }

    /// <summary>
    /// CoreGraphics has no display name, only the position in the list, so the label is "显示器 N" with the main
    /// display called out — the client shows this in the multi-monitor picker.
    /// </summary>
    private static string Describe(uint id, int index) =>
        id == CoreGraphics.CGMainDisplayID() ? $"显示器 {index + 1}（主）" : $"显示器 {index + 1}";
}
