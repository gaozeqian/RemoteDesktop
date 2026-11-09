namespace RemoteDesktop.Platform.MacOS.Input;

/// <summary>Keeps one remote session in control of the process-wide CGEvent pressed state.</summary>
internal sealed class InputSessionArbitrator
{
    private string? _activeSession;

    public bool TryInject(string sessionId)
    {
        _activeSession ??= sessionId;
        return _activeSession == sessionId;
    }

    public void Release(string sessionId)
    {
        if (_activeSession == sessionId)
        {
            _activeSession = null;
        }
    }
}
