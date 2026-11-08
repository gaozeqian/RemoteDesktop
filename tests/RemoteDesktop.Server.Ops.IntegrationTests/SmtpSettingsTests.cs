using RemoteDesktop.Server.Ops;
using RemoteDesktop.Server.Ops.Core;

namespace RemoteDesktop.Server.Ops.IntegrationTests;

/// <summary>
/// A half-written mail configuration is refused at start-up rather than discovered when the first alert is the one that
/// nobody hears about. The messages name the key, because that is the only thing an operator can act on.
/// </summary>
public sealed class SmtpSettingsTests
{
    [Fact]
    public void An_empty_host_disables_mail_and_needs_nothing_else()
    {
        var options = Options(new SmtpSettings());

        options.Validate();

        var smtp = options.Notifications.Smtp;
        Assert.False(smtp.IsConfigured);
        Assert.Equal(string.Empty, smtp.Host);
        Assert.Equal(string.Empty, smtp.From);
        Assert.Equal(string.Empty, smtp.To);
        Assert.Equal(string.Empty, smtp.Username);
        Assert.Equal(string.Empty, smtp.Password);
        Assert.Equal(587, smtp.Port);
        Assert.True(smtp.UseStartTls);
        Assert.Empty(smtp.Recipients());
    }

    [Fact]
    public void Mail_settings_without_a_host_are_a_mistake_rather_than_a_silent_disable()
    {
        var options = Options(new SmtpSettings { To = "ops@example.com" });

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Ops:Notifications:Smtp:Host", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "ops@example.com")]           // no sender
    [InlineData("ops", "ops@example.com")]        // not an address
    [InlineData("ops@example.com", "")]           // no recipient
    [InlineData("ops@example.com", "ops")]        // recipient is not an address
    [InlineData("ops@example.com", "a@b, ops")]   // one bad recipient among good ones
    public void A_sender_and_every_recipient_have_to_be_addresses(string from, string to)
    {
        var options = Options(new SmtpSettings { Host = "smtp.example.com", From = from, To = to });

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Ops:Notifications:Smtp", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_password_without_a_user_name_is_refused()
    {
        var options = Options(new SmtpSettings
        {
            Host = "smtp.example.com",
            From = "ops@example.com",
            To = "ops@example.com",
            Password = "secret",
        });

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Username", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_complete_configuration_passes_and_splits_the_recipients()
    {
        var options = Options(new SmtpSettings
        {
            Host = "smtp.example.com",
            From = "ops@example.com",
            To = "a@example.com; b@example.com,",
            Username = "ops",
            Password = "secret",
        });

        options.Validate();

        Assert.Equal(["a@example.com", "b@example.com"], options.Notifications.Smtp.Recipients());
    }

    private static OpsOptions Options(SmtpSettings smtp) => new()
    {
        // The database has to be valid for the rest of Validate to be reached.
        Database = new OpsDatabaseSettings { Path = "ops.db" },
        Notifications = new NotificationSettings { Smtp = smtp },
    };
}
