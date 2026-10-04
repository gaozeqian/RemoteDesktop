using RemoteDesktop.Features.Terminal.Emulation;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Features.Terminal;

/// <summary>
/// One remote terminal as the controller sees it: the host's shell output is fed into a <see cref="TerminalEmulator"/>
/// whose screen the UI draws, and keys, pastes and mouse events go back to the shell in the form the running program
/// asked for.
/// </summary>
public sealed class TerminalSession : IAsyncDisposable
{
    private readonly TerminalClient _client;
    private int _exitCode = int.MinValue;

    private TerminalSession(TerminalClient client, TerminalEmulator emulator)
    {
        _client = client;
        Emulator = emulator;
        client.Output += (_, data) => emulator.Feed(data.Span);
        client.Exited += (_, code) => Volatile.Write(ref _exitCode, code);
        client.Closed += (_, _) => Ended?.Invoke(this, EventArgs.Empty);
        emulator.Reply += (_, data) => client.Send(data.Span);
        client.Start();
    }

    public TerminalEmulator Emulator { get; }

    /// <summary>The shell the host started.</summary>
    public string Shell => _client.Shell;

    /// <summary>The shell's exit code once it has ended; <c>null</c> while it runs or when the connection was lost instead.</summary>
    public int? ExitCode => Volatile.Read(ref _exitCode) is var code && code != int.MinValue ? code : null;

    /// <summary>The terminal is over: the shell exited or the connection was lost. Raised on a background thread.</summary>
    public event EventHandler? Ended;

    /// <exception cref="TerminalException">The host refused or could not start the shell.</exception>
    public static async Task<TerminalSession> StartAsync(
        IPeerConnection connection,
        string? shell,
        int columns,
        int rows,
        CancellationToken cancellationToken,
        int scrollbackLines = TerminalEmulator.DefaultScrollback)
    {
        var client = await TerminalClient.OpenAsync(connection, shell, columns, rows, cancellationToken).ConfigureAwait(false);
        return new TerminalSession(client, new TerminalEmulator(columns, rows, scrollbackLines));
    }

    public void SendText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _client.Send(TerminalInput.EncodeText(text));
    }

    public void SendKey(TerminalKey key, TerminalModifiers modifiers = TerminalModifiers.None) =>
        _client.Send(TerminalInput.EncodeKey(key, modifiers, Emulator.ApplicationCursorKeys));

    /// <returns><c>false</c> when the combination has no terminal meaning and nothing was sent.</returns>
    public bool SendControl(char character, TerminalModifiers modifiers)
    {
        var bytes = TerminalInput.EncodeControl(character, modifiers);
        if (bytes is null)
        {
            return false;
        }

        _client.Send(bytes);
        return true;
    }

    public void Paste(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _client.Send(TerminalInput.EncodePaste(text, Emulator.BracketedPaste));
    }

    /// <summary>Reports a mouse event to the program, if it asked for mouse events of this kind. Returns whether it was sent.</summary>
    public bool SendMouse(MouseAction action, int button, int column, int row, TerminalModifiers modifiers)
    {
        var wanted = Emulator.MouseTracking switch
        {
            MouseTracking.None => false,
            MouseTracking.Buttons => action != MouseAction.Move,
            MouseTracking.Drag => action != MouseAction.Move || button >= 0,
            _ => true,
        };
        if (!wanted)
        {
            return false;
        }

        var bytes = TerminalInput.EncodeMouse(action, button, column, row, modifiers, Emulator.MouseSgr);
        if (bytes is null)
        {
            return false;
        }

        _client.Send(bytes);
        return true;
    }

    public void SendFocus(bool focused)
    {
        if (Emulator.FocusReporting)
        {
            _client.Send(TerminalInput.EncodeText(focused ? "\u001b[I" : "\u001b[O"));
        }
    }

    public void Resize(int columns, int rows)
    {
        Emulator.Resize(columns, rows);
        _client.Resize(columns, rows);
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
