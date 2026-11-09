using RemoteDesktop.Protocol.Control;

namespace RemoteDesktop.Application.Sessions;

public enum SessionFailure
{
    BadPassword,
    TooManyAttempts,
    IncompatibleVersion,
    TooManySessions,
    Timeout,
    ProtocolError,
}

/// <summary>The connection was established but the session could not be.</summary>
public sealed class SessionException : Exception
{
    public SessionException()
    {
    }

    public SessionException(string message)
        : base(message)
    {
    }

    public SessionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SessionException(SessionFailure failure, string message)
        : base(message)
    {
        Failure = failure;
    }

    public SessionException(SessionFailure failure, string message, Exception innerException)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public SessionFailure Failure { get; } = SessionFailure.ProtocolError;

    internal static SessionException FromAuthFailure(AuthFailure failure) => failure switch
    {
        AuthFailure.BadPassword => new(SessionFailure.BadPassword, "Wrong password."),
        AuthFailure.TooManyAttempts => new(SessionFailure.TooManyAttempts, "Too many failed attempts; try again later."),
        AuthFailure.IncompatibleVersion => new(SessionFailure.IncompatibleVersion, "The remote device runs an incompatible version."),
        AuthFailure.TooManySessions => new(SessionFailure.TooManySessions, "The remote device is already serving as many sessions as it allows."),
        _ => new(SessionFailure.ProtocolError, "The remote device refused the session."),
    };
}
