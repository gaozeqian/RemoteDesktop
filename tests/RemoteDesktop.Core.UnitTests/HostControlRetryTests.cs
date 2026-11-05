using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Application.Hosting;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The control endpoint is a convenience for the local UI. When it cannot be created or breaks while accepting, the
/// service has to carry on hosting sessions and try the endpoint again, not stop.
/// </summary>
public class HostControlRetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_failure_is_logged_as_an_error_and_the_endpoint_is_served_again_after_a_pause()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var logger = new RecordingLogger();
        var delays = new List<TimeSpan>();
        var attempts = 0;
        var served = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = HostControlRetry.RunAsync(
            async (accepted, token) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new IOException("The control-socket directory is a symbolic link.");
                }

                // The second attempt works: it accepts a connection and goes on waiting for the next one.
                accepted();
                served.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            },
            logger,
            "control",
            stop.Token,
            (time, _) =>
            {
                delays.Add(time);
                return Task.CompletedTask;
            });

        await served.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.False(run.IsCompleted); // still serving: the failure did not end it
        Assert.Equal([HostControlRetry.InitialDelay], delays);
        Assert.Single(logger.Errors);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Ct); // stopping is not a failure either
    }

    [Fact]
    public async Task The_pause_doubles_up_to_a_ceiling_and_starts_over_once_a_connection_was_accepted()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var delays = new List<TimeSpan>();
        var attempts = 0;

        await HostControlRetry.RunAsync(
            (accepted, _) =>
            {
                var attempt = ++attempts;
                if (attempt == 10)
                {
                    accepted(); // the endpoint worked this time, and then broke
                    throw new UnauthorizedAccessException();
                }

                if (attempt == 12)
                {
                    stop.Cancel();
                    return Task.CompletedTask;
                }

                throw attempt % 2 == 0 ? new SocketException((int)SocketError.AddressAlreadyInUse) : new IOException();
            },
            new RecordingLogger(),
            "control",
            stop.Token,
            (time, _) =>
            {
                delays.Add(time);
                return Task.CompletedTask;
            });

        var seconds = delays.Select(d => (int)d.TotalSeconds).ToArray();
        Assert.Equal([1, 2, 4, 8, 16, 30, 30, 30, 30, 1, 2], seconds);
    }

    [Fact]
    public async Task Cancellation_while_serving_ends_quietly_and_other_exceptions_are_not_hidden()
    {
        using var stop = new CancellationTokenSource();
        var logger = new RecordingLogger();
        await stop.CancelAsync();

        await HostControlRetry.RunAsync((_, token) => Task.FromCanceled(token), logger, "control", stop.Token);
        Assert.Empty(logger.Errors);

        await Assert.ThrowsAsync<InvalidOperationException>(() => HostControlRetry.RunAsync(
            (_, _) => throw new InvalidOperationException("a bug, not an endpoint failure"),
            logger,
            "control",
            Ct));
        Assert.Empty(logger.Errors);
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly List<string> _errors = [];

        public IReadOnlyList<string> Errors
        {
            get
            {
                lock (_errors)
                {
                    return [.. _errors];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                lock (_errors)
                {
                    _errors.Add(formatter(state, exception));
                }
            }
        }
    }
}
