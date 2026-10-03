namespace RemoteDesktop.Abstractions.Clipboard;

public abstract record ClipboardContent;

public sealed record TextClipboardContent(string Text) : ClipboardContent;

/// <param name="Png">PNG-encoded image.</param>
public sealed record ImageClipboardContent(ReadOnlyMemory<byte> Png) : ClipboardContent;

public interface IClipboard
{
    ClipboardContent? Get();

    void Set(ClipboardContent content);

    event EventHandler? Changed;
}
