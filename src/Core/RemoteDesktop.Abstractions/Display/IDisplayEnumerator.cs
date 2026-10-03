namespace RemoteDesktop.Abstractions.Display;

public interface IDisplayEnumerator
{
    IReadOnlyList<DisplayInfo> GetDisplays();

    /// <summary>Raised when displays are added, removed or change resolution.</summary>
    event EventHandler? DisplaysChanged;
}
