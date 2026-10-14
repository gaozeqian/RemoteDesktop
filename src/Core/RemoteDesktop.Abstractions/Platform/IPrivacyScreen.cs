namespace RemoteDesktop.Abstractions.Platform;

/// <summary>Blanks the physical monitor of the host and blocks local input while being controlled.</summary>
/// <remarks>
/// <para>
/// <b>Planned port — not implemented on any platform yet.</b> Neither the Windows nor the macOS layer registers an
/// implementation, so <c>GetService&lt;IPrivacyScreen&gt;()</c> returns null everywhere and the client's self-check
/// reports it as "not registered" (a missing optional port, not a failure). Do not treat that line as a defect, and
/// do not write code that assumes a concrete platform behaviour before one exists.
/// </para>
/// <para>
/// The shape is settled (support probe plus enable/disable) because the two halves have different prices: blanking
/// the local monitor while somebody else drives is straightforward on both platforms, whereas blocking local input
/// needs a driver-level input filter on Windows and has no equivalent API at all on macOS — so this is one feature
/// that will not ship on both platforms at the same time.
/// </para>
/// </remarks>
public interface IPrivacyScreen
{
    bool IsSupported { get; }

    void Enable();

    void Disable();
}
