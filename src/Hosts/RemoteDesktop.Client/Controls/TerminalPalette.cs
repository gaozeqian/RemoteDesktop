using Avalonia.Media;
using RemoteDesktop.Features.Terminal.Emulation;

namespace RemoteDesktop.Client.Controls;

/// <summary>The colours of the terminal: the 16 named ones (the Windows Terminal "Campbell" scheme), the 6x6x6 cube and the greys.</summary>
internal static class TerminalPalette
{
    public static readonly Color DefaultForeground = Color.FromRgb(0xCC, 0xCC, 0xCC);

    public static readonly Color DefaultBackground = Color.FromRgb(0x0C, 0x0C, 0x0C);

    private static readonly uint[] Named =
    [
        0x0C0C0C, 0xC50F1F, 0x13A10E, 0xC19C00, 0x0037DA, 0x881798, 0x3A96DD, 0xCCCCCC,
        0x767676, 0xE74856, 0x16C60C, 0xF9F1A5, 0x3B78FF, 0xB4009E, 0x61D6D6, 0xF2F2F2,
    ];

    private static readonly byte[] CubeLevels = [0, 95, 135, 175, 215, 255];

    /// <summary>The colour for a cell; <paramref name="defaultColor"/> stands in for "the terminal's default".</summary>
    public static Color Resolve(TerminalColor color, Color defaultColor)
    {
        if (color.IsRgb)
        {
            return Color.FromRgb(color.R, color.G, color.B);
        }

        if (!color.IsIndexed)
        {
            return defaultColor;
        }

        var index = color.Index;
        if (index < 16)
        {
            var rgb = Named[index];
            return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        if (index < 232)
        {
            var i = index - 16;
            return Color.FromRgb(CubeLevels[i / 36], CubeLevels[i / 6 % 6], CubeLevels[i % 6]);
        }

        var grey = (byte)(8 + ((index - 232) * 10));
        return Color.FromRgb(grey, grey, grey);
    }

    public static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)(from.R + ((to.R - from.R) * amount)),
        (byte)(from.G + ((to.G - from.G) * amount)),
        (byte)(from.B + ((to.B - from.B) * amount)));
}
