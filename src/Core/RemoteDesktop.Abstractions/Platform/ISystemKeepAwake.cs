namespace RemoteDesktop.Abstractions.Platform;

/// <summary>Prevents the host computer from falling asleep while a controller is connected.</summary>
public interface ISystemKeepAwake
{
    /// <summary>Requests that the system stay awake.</summary>
    void Acquire();

    /// <summary>Releases a stay-awake request.</summary>
    void Release();
}
