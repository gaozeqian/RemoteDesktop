namespace RemoteDesktop.Abstractions.Platform;

/// <summary>
/// Registers the application to launch when the user logs in, for an unattended controlled-side host.
/// </summary>
/// <remarks>
/// <para>
/// The macOS implementation is <c>SMAppService.mainAppService</c>, which is keyed to the signed bundle — so it is only
/// effective for a properly signed <c>.app</c> placed in <c>/Applications</c>, and an ad-hoc signature is reported as
/// a fresh, unregistered identity on every build.
/// </para>
/// <para>
/// The port itself carries no platform annotation, like every other one in this folder: the operating system is a
/// property of the implementation (<c>MacLoginItem</c>), and keeping the annotation there leaves the concept open for
/// a Windows startup task or a Linux unit to implement later without re-annotating the interface.
/// </para>
/// </remarks>
public interface ILoginItem
{
    /// <summary>Whether the application is currently registered to launch at login.</summary>
    bool IsRegistered { get; }

    /// <summary>Registers the application to launch at login. Throws if the system refuses (not a signed bundle in
    /// <c>/Applications</c>, or macOS before 13).</summary>
    void Register();

    /// <summary>Unregisters the application from launching at login.</summary>
    void Unregister();
}
