using Microsoft.Extensions.Logging;
using RemoteDesktop.Platform.Linux;
using Tmds.DBus.Protocol;

namespace RemoteDesktop.Platform.Linux.UnitTests;

/// <summary>
/// Keeping the machine awake through logind. The assertion is not on a flag this class keeps but on what logind
/// itself reports, because the whole mechanism is a file descriptor: asking for one takes the inhibitor, closing it
/// gives the inhibitor back, and a copy that quietly closed would look identical from the inside.
/// </summary>
public sealed class KeepAwakeTests
{
    private const string LinuxOnly = "logind is a Linux service.";
    private const string Who = "Remote Desktop";

    /// <summary>Waits for what logind reports to catch up, since the inhibitor is taken and dropped on another process.</summary>
    private static async Task<bool> IsHeldAsync(bool expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await Task.Run(() => HeldByLogind()) == expected)
            {
                return true;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return await Task.Run(() => HeldByLogind()) == expected;
    }

    /// <summary>
    /// Skips the test when the machine cannot take inhibitors at all, and lets it run — and possibly fail — otherwise.
    /// </summary>
    /// <remarks>
    /// A headless machine has no login session for the test process to belong to, and logind's polkit policy lets an
    /// active session take an inhibitor without further authentication but demands it of everything else. On a runner
    /// there is nobody to authenticate, so the inhibitor never appears — which looks exactly like the port being
    /// broken, and is the one thing these tests exist to catch. The class says which of the two happened, out loud, in
    /// its own warning, and that is what this reads: a failure the class did <em>not</em> explain is left to fail.
    /// </remarks>
    private static void SkipWhenLogindCannotInhibit(string? warning)
    {
        var environment = warning switch
        {
            null => null,
            _ when warning.Contains("There is no system bus", StringComparison.Ordinal)
                => "this machine has no system bus",
            _ when warning.Contains("AccessDenied", StringComparison.Ordinal)
                || warning.Contains("authentication", StringComparison.OrdinalIgnoreCase)
                => "logind refuses inhibitors for a process that is in no login session",
            _ => null,
        };

        if (environment is not null)
        {
            Assert.Skip($"{environment} ({warning})");
        }
    }

    /// <summary>Keeps the class's own warning, which is how "cannot inhibit here" is told from "the port is broken".</summary>
    private sealed class WarningLogger : ILogger<LogindKeepAwake>
    {
        public string? Warning { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Warning = formatter(state, exception);
            }
        }
    }

    private static bool HeldByLogind()
    {
        var address = DBusAddress.System;
        if (address is null)
        {
            return false;
        }

        var connection = new DBusConnection(address);
        try
        {
            connection.ConnectAsync().AsTask().GetAwaiter().GetResult();

            var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(
                "org.freedesktop.login1", "/org/freedesktop/login1", "org.freedesktop.login1.Manager", "ListInhibitors", null, MessageFlags.None);

            return connection.CallMethodAsync(writer.CreateMessage(), static (Message message, object? state) =>
            {
                var reader = message.GetBodyReader();
                var inhibitors = reader.ReadArrayStart(DBusType.Struct);
                while (reader.HasNext(inhibitors))
                {
                    var what = reader.ReadString();
                    var who = reader.ReadString();
                    reader.ReadString();  // why
                    reader.ReadString();  // mode
                    reader.ReadUInt32();  // uid
                    reader.ReadUInt32();  // pid

                    _ = what;
                    if (who == (string)state!)
                    {
                        return true;
                    }
                }

                return false;
            }, Who).GetAwaiter().GetResult();
        }
        finally
        {
            connection.Dispose();
        }
    }

    [Fact]
    public async Task An_inhibitor_is_held_while_a_controller_is_connected_and_given_back_afterwards()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var logger = new WarningLogger();
        using var keepAwake = new LogindKeepAwake(logger);

        Assert.True(await IsHeldAsync(expected: false), "An inhibitor was already held before the session started.");

        keepAwake.Acquire();
        var held = await IsHeldAsync(expected: true);
        SkipWhenLogindCannotInhibit(logger.Warning);
        Assert.True(held, "No inhibitor appeared after Acquire.");

        keepAwake.Release();
        Assert.True(await IsHeldAsync(expected: false), "The inhibitor outlived the session.");
    }

    [Fact]
    public async Task The_inhibitor_is_held_until_the_last_session_lets_go()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var logger = new WarningLogger();
        using var keepAwake = new LogindKeepAwake(logger);

        keepAwake.Acquire();
        keepAwake.Acquire();
        var held = await IsHeldAsync(expected: true);
        SkipWhenLogindCannotInhibit(logger.Warning);
        Assert.True(held, "No inhibitor appeared after two Acquisitions.");

        keepAwake.Release();
        // One session ending must not take the inhibitor away from the one still running.
        Assert.True(await IsHeldAsync(expected: true), "The inhibitor was dropped while a session was still open.");

        keepAwake.Release();
        Assert.True(await IsHeldAsync(expected: false), "The inhibitor outlived the last session.");
    }

    [Fact]
    public async Task Releasing_more_than_was_acquired_does_not_release_anything_else()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

        var logger = new WarningLogger();
        using var keepAwake = new LogindKeepAwake(logger);

        keepAwake.Acquire();
        var held = await IsHeldAsync(expected: true);
        SkipWhenLogindCannotInhibit(logger.Warning);
        Assert.True(held, "No inhibitor appeared after Acquire.");

        keepAwake.Release();
        keepAwake.Release(); // the count is already at zero; this must not underflow into a second inhibitor appearing
        Assert.True(await IsHeldAsync(expected: false), "The inhibitor outlived the session.");
    }
}
