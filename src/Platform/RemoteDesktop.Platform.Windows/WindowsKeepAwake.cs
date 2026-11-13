using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using RemoteDesktop.Abstractions.Platform;

namespace RemoteDesktop.Platform.Windows;

/// <summary>
/// Keeps Windows awake while a controller is connected using <c>SetThreadExecutionState</c>.
/// </summary>
/// <remarks>
/// The Win32 call is thread-affine: <c>ES_CONTINUOUS</c> stays in effect for the thread that set it, and clearing it
/// from another thread clears that other thread's (unset) state instead. Session code calls <see cref="Acquire"/>
/// before an await and <see cref="Release"/> in a finally, which routinely means different thread-pool threads, so
/// calling the API directly left the first thread's request in place for the life of the process. Every call is
/// therefore marshalled to one long-lived background thread; the process can still exit normally because it is a
/// background thread.
/// </remarks>
public sealed partial class WindowsKeepAwake : ISystemKeepAwake, IDisposable
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsAwayModeRequired = 0x00000040;

    private readonly BlockingCollection<Action> _commands = new();
    private readonly Thread _worker;
    private readonly Lock _gate = new();
    private int _count;
    private bool _disposed;

    public WindowsKeepAwake()
    {
        _worker = new Thread(Consume)
        {
            IsBackground = true,
            Name = "RemoteDesktop.KeepAwake",
        };
        _worker.Start();
    }

    /// <summary>Replaces the Win32 call in tests, so they can observe the thread and flags it was given.</summary>
    internal Func<uint, uint>? ExecutionStateForTests { get; set; }

    public void Acquire()
    {
        var apply = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            apply = ++_count == 1;
        }

        if (apply)
        {
            OnWorker(() => SetExecutionState(EsContinuous | EsSystemRequired | EsAwayModeRequired));
        }
    }

    public void Release()
    {
        var clear = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (--_count <= 0)
            {
                _count = 0;
                clear = true;
            }
        }

        if (clear)
        {
            OnWorker(() => SetExecutionState(EsContinuous));
        }
    }

    private void Consume()
    {
        foreach (var command in _commands.GetConsumingEnumerable())
        {
            command();
        }
    }

    /// <summary>Runs one state change on the owning thread and waits for it, so Acquire means "applied".</summary>
    private void OnWorker(Action action)
    {
        if (Thread.CurrentThread == _worker)
        {
            action();
            return;
        }

        Exception? failure = null;
        using var done = new ManualResetEventSlim();
        try
        {
            _commands.Add(() =>
            {
                try
                {
                    action();
                }
#pragma warning disable CA1031 // The worker must stay alive; the failure is rethrown to the caller below.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    failure = ex;
                }
                finally
                {
                    done.Set();
                }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Disposed between the caller's check and this post; there is nothing left to keep awake.
            return;
        }

        done.Wait();
        if (failure is not null)
        {
            throw new InvalidOperationException("The keep-awake thread failed to change the execution state.", failure);
        }
    }

    private uint SetExecutionState(uint flags) =>
        ExecutionStateForTests?.Invoke(flags) ?? SetThreadExecutionState(flags);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _commands.CompleteAdding();
        _worker.Join(TimeSpan.FromSeconds(5));
        _commands.Dispose();
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint SetThreadExecutionState(uint esFlags);
}
