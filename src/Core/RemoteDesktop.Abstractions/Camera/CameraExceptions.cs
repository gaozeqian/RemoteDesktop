namespace RemoteDesktop.Abstractions.Camera;

/// <summary>The camera cannot deliver pictures: it is in use elsewhere, unplugged, or the operating system's privacy settings block access.</summary>
public sealed class CameraUnavailableException : Exception
{
    public CameraUnavailableException()
    {
    }

    public CameraUnavailableException(string message)
        : base(message)
    {
    }

    public CameraUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
