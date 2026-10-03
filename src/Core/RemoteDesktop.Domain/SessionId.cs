namespace RemoteDesktop.Domain;

public readonly record struct SessionId(Guid Value)
{
    public static SessionId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("N");
}
