using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Domain;

namespace RemoteDesktop.Application.Sessions;

/// <summary>
/// The choices the person at this computer has made about what controllers may do. Everything is allowed by default
/// except the camera, which is only ever used after an explicit yes; the choice survives restarts.
/// </summary>
public static class HostPolicy
{
    private const string CameraSecret = "policy.allow-camera";

    /// <summary>What a fresh installation allows: everything but the camera.</summary>
    public static Permissions Default => Permissions.All & ~Permissions.Camera;

    /// <summary>The policy stored for this device, or the default when nothing was stored (or it cannot be read).</summary>
    public static Permissions Load(ISecretStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            return store.Load(CameraSecret) is { Length: 1 } stored && stored[0] == 1 ? Permissions.All : Default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return Default;
        }
    }

    public static void SaveCameraAllowed(ISecretStore store, bool allowed)
    {
        ArgumentNullException.ThrowIfNull(store);
        store.Save(CameraSecret, [allowed ? (byte)1 : (byte)0]);
    }

    public static Permissions WithCamera(Permissions allowed, bool camera) =>
        camera ? allowed | Permissions.Camera : allowed & ~Permissions.Camera;
}
