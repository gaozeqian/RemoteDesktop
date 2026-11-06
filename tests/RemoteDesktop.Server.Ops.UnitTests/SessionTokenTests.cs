using RemoteDesktop.Server.Ops.Core;

namespace RemoteDesktop.Server.Ops.UnitTests;

/// <summary>The CSRF token is a pure function of the session token: stable for one session, different for any other.</summary>
public class SessionTokenTests
{
    [Fact]
    public void The_csrf_token_is_stable_for_a_session_and_differs_between_sessions()
    {
        var first = SessionToken.Create();
        var second = SessionToken.Create();

        Assert.Equal(SessionToken.CsrfFor(first), SessionToken.CsrfFor(first));
        Assert.NotEqual(SessionToken.CsrfFor(first), SessionToken.CsrfFor(second));
    }

    [Fact]
    public void The_csrf_token_is_not_the_session_token_nor_its_stored_hash_and_is_cookie_safe()
    {
        var session = SessionToken.Create();
        var csrf = SessionToken.CsrfFor(session);

        Assert.NotEqual(session, csrf);
        Assert.NotEqual(SessionToken.Hash(session), csrf);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", csrf);
    }
}
