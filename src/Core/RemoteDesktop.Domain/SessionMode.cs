namespace RemoteDesktop.Domain;

public enum SessionMode
{
    /// <summary>Full remote control.</summary>
    Control,

    /// <summary>Watch mode: the controller only sees the screen and cannot affect the host.</summary>
    View,
}
