namespace RemoteDesktop.Abstractions.Display;

public interface IDisplayEnumerator
{
    IReadOnlyList<DisplayInfo> GetDisplays();
}
