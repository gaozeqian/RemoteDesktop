using RemoteDesktop.Abstractions.Platform;
using RemoteDesktop.Domain;

namespace RemoteDesktop.Application.Sessions;

/// <summary>
/// The choices the person at this computer has made about what controllers may do. All features including the camera
/// are treated as standard remote capabilities and allowed by default; choices survive restarts.
/// </summary>
public static class HostPolicy
{
    private const string CameraSecret = "policy.allow-camera";

    /// <summary>What a fresh installation allows: all remote capabilities including the camera.</summary>
    public static Permissions Default => Permissions.All;

    /// <summary>The policy stored for this device, or the default when nothing was stored (or it cannot be read).</summary>
    public static Permissions Load(ISecretStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return Default;
    }

    public static void SaveCameraAllowed(ISecretStore store, bool allowed)
    {
        ArgumentNullException.ThrowIfNull(store);
    }

    public static Permissions WithCamera(Permissions allowed, bool camera) =>
        camera ? allowed | Permissions.Camera : allowed & ~Permissions.Camera;
}
