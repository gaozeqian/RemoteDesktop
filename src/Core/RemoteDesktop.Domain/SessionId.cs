namespace RemoteDesktop.Domain;

public readonly record struct SessionId(Guid Value)
{
    public static SessionId New() => new(Guid.CreateVersion7());

    /// <summary>Parses the form produced by <see cref="ToString"/>.</summary>
    public static bool TryParse(string? text, out SessionId id)
    {
        var ok = Guid.TryParseExact(text, "N", out var guid);
        id = new SessionId(guid);
        return ok;
    }

    public override string ToString() => Value.ToString("N");
}
