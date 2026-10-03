namespace RemoteDesktop.Abstractions.Display;

/// <param name="Id">Stable identifier of the display for the lifetime of the session.</param>
/// <param name="Bounds">Position and size in virtual-desktop pixels.</param>
/// <param name="Scale">DPI scale factor (1.0 = 96 DPI on Windows).</param>
public sealed record DisplayInfo(string Id, string Name, PixelRect Bounds, double Scale, bool IsPrimary);

public readonly record struct PixelRect(int X, int Y, int Width, int Height);
