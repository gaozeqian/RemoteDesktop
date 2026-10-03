namespace RemoteDesktop.Abstractions.Platform;

/// <summary>Blanks the physical monitor of the host and blocks local input while being controlled.</summary>
public interface IPrivacyScreen
{
    bool IsSupported { get; }

    void Enable();

    void Disable();
}
