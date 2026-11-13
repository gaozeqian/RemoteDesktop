using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Features.FileTransfer;
using RemoteDesktop.TestSupport;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The unfinished transfers nobody came back for. A controller can leave one of these behind on somebody else's
/// machine for every attempt it abandons — the file name carries the size and time it was aiming at, so each attempt
/// is a different name — and without this they stay in that folder for good.
/// </summary>
public sealed class PartialFileSweeperTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("rd-partial-").FullName;
    private readonly TestClock _time = new();

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void A_record_the_ledger_writes_survives_a_new_ledger()
    {
        var partial = Path.Combine(_folder, "movie.mp4.123-abc.rdpart");
        Ledger().Record(partial);

        Assert.Equal([partial], Ledger().Entries());
    }

    /// <summary>
    /// Unix paths are case-sensitive. An OrdinalIgnoreCase ledger treats these as one record: the second one is not
    /// recorded (so the sweeper never sees it) and forgetting one removes the other.
    /// </summary>
    [Fact]
    public void On_a_case_sensitive_filesystem_paths_that_differ_only_in_case_are_separate()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows paths are case-insensitive.");

        var lower = Path.Combine(_folder, "movie.1-a.rdpart");
        var upper = Path.Combine(_folder, "MOVIE.1-a.rdpart");
        var ledger = Ledger();
        ledger.Record(lower);
        ledger.Record(upper);

        var entries = Ledger().Entries();
        Assert.Equal(2, entries.Count);
        Assert.Contains(lower, entries);
        Assert.Contains(upper, entries);
    }

    [Fact]
    public void Forgetting_a_record_takes_it_out_of_the_file()
    {
        var ledger = Ledger();
        var partial = Path.Combine(_folder, "movie.mp4.123-abc.rdpart");
        ledger.Record(partial);
        ledger.Forget(partial);

        Assert.Empty(Ledger().Entries());
    }

    [Fact]
    public void An_old_unfinished_transfer_is_deleted_and_a_recent_one_is_left_alone()
    {
        var abandoned = WriteFile(Path.Combine(_folder, "old.iso.999-aaa.rdpart"), "half a file");
        var recent = WriteFile(Path.Combine(_folder, "new.iso.999-bbb.rdpart"), "half a file");
        var ledger = Ledger();
        ledger.Record(abandoned);
        ledger.Record(recent);

        // The one still being written has a current modification time, whatever the list says.
        File.SetLastWriteTimeUtc(abandoned, _time.GetUtcNow().UtcDateTime - PartialFileSweeper.Retention - TimeSpan.FromDays(1));
        File.SetLastWriteTimeUtc(recent, _time.GetUtcNow().UtcDateTime - TimeSpan.FromHours(2));

        var removed = new PartialFileSweeper(ledger, _time, NullLogger<PartialFileSweeper>.Instance).Sweep();

        Assert.Equal(1, removed);
        Assert.False(File.Exists(abandoned));
        Assert.True(File.Exists(recent));

        // What is left on the list is exactly what is still there to finish.
        Assert.Equal([recent], ledger.Entries());
    }

    [Fact]
    public void A_record_for_a_file_that_is_gone_is_dropped_quietly()
    {
        var ledger = Ledger();
        ledger.Record(Path.Combine(_folder, "already-finished.zip.1-a.rdpart"));

        var removed = new PartialFileSweeper(ledger, _time, NullLogger<PartialFileSweeper>.Instance).Sweep();

        Assert.Equal(0, removed);
        Assert.Empty(ledger.Entries());
    }

    /// <summary>
    /// A controller can abandon one attempt after another, each with a different size and time in its name. The list
    /// must not grow with them: past the cap the oldest record goes, because that is the one a resumed transfer is
    /// least likely to still want.
    /// </summary>
    [Fact]
    public void The_list_keeps_the_newest_records_when_it_is_full()
    {
        var ledger = Ledger(maxEntries: 3);
        foreach (var name in new[] { "a", "b", "c", "d" })
        {
            ledger.Record(Path.Combine(_folder, name + ".1-a.rdpart"));
        }

        Assert.Equal(
            [
                Path.Combine(_folder, "b.1-a.rdpart"),
                Path.Combine(_folder, "c.1-a.rdpart"),
                Path.Combine(_folder, "d.1-a.rdpart"),
            ],
            Ledger().Entries());
    }

    /// <summary>A list written before the cap existed — or by hand — is trimmed when it is read, not carried around.</summary>
    [Fact]
    public void A_list_longer_than_the_cap_is_trimmed_oldest_first_when_it_is_read()
    {
        var records = Enumerable.Range(0, 5).Select(i => Path.Combine(_folder, $"movie.mp4.{i}-a.rdpart")).ToArray();
        System.IO.File.WriteAllLines(Path.Combine(_folder, "partial-files.log"), records);

        Assert.Equal(records[^3..], Ledger(maxEntries: 3).Entries());
    }

    /// <summary>
    /// The service is what a host actually starts. Its contract is wider than <see cref="PartialFileSweeper.Sweep"/>:
    /// one pass on start, another every <see cref="PartialFileSweeper.Interval"/>, and a stop that ends the wait.
    /// </summary>
    [Fact]
    public async Task The_service_sweeps_on_start_without_waiting_for_the_interval()
    {
        var clock = new SteppedTime();
        var abandoned = WriteOldPartial(clock, "startup.iso.1-a.rdpart");
        var ledger = Ledger();
        ledger.Record(abandoned);

        var sweeper = new PartialFileSweeper(ledger, clock, NullLogger<PartialFileSweeper>.Instance);
        try
        {
            await sweeper.StartAsync(TestContext.Current.CancellationToken);

            Assert.True(
                await ViewerRig.WaitUntilAsync(() => !File.Exists(abandoned)),
                "the pass on start should delete the abandoned file");
            Assert.Empty(ledger.Entries());
        }
        finally
        {
            await sweeper.StopAsync(TestContext.Current.CancellationToken);
            sweeper.Dispose();
        }
    }

    /// <summary>
    /// A pass that throws is logged and the loop waits for the next interval. If it instead ended the loop, cleanup
    /// would stop for the life of the process after one unlucky file.
    /// </summary>
    [Fact]
    public async Task A_failed_pass_does_not_stop_the_next_interval()
    {
        var clock = new SteppedTime();
        var abandoned = WriteOldPartial(clock, "retry.iso.1-a.rdpart");
        var ledger = Ledger();
        ledger.Record(abandoned);

        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sweeper = new PartialFileSweeper(
            ledger,
            new FailOnceTime(clock),
            new SweepFailureLogger(failed));
        try
        {
            await sweeper.StartAsync(TestContext.Current.CancellationToken);
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(File.Exists(abandoned), "the failed pass must not have deleted the file");

            clock.Advance(PartialFileSweeper.Interval);

            Assert.True(
                await ViewerRig.WaitUntilAsync(() => !File.Exists(abandoned)),
                "the next interval should run another pass");
        }
        finally
        {
            await sweeper.StopAsync(TestContext.Current.CancellationToken);
            sweeper.Dispose();
        }
    }

    [Fact]
    public async Task Stopping_the_service_ends_its_wait()
    {
        var sweeper = new PartialFileSweeper(Ledger(), new SteppedTime(), NullLogger<PartialFileSweeper>.Instance);
        await sweeper.StartAsync(TestContext.Current.CancellationToken);
        await sweeper.StopAsync(TestContext.Current.CancellationToken).WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        sweeper.Dispose();
    }

    /// <summary>The transfer itself must not be able to notice the list: a file it cannot write is not its problem.</summary>
    [Fact]
    public void A_list_that_cannot_be_written_costs_the_cleanup_and_nothing_else()
    {
        // A path whose parent is a file: the directory cannot be created, so neither can the list.
        var blocked = WriteFile(Path.Combine(_folder, "not-a-directory"), "x");
        var unwritable = Path.Combine(blocked, "partial-files.log");
        var ledger = new PartialFileLedger(new PartialFileLedgerOptions(unwritable), NullLogger<PartialFileLedger>.Instance);

        ledger.Record(Path.Combine(_folder, "movie.mp4.123-abc.rdpart"));

        Assert.False(File.Exists(unwritable));
        Assert.Single(ledger.Entries()); // it is still tracked in memory, which is all this process can do
    }

    /// <summary>
    /// The list is a text file with one path per line. A path that itself contains a line break would be read back as
    /// several unrelated entries, so it is not recorded at all; the transfer it belongs to is unaffected.
    /// </summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    public void A_path_containing_a_line_break_is_not_recorded(string lineBreak)
    {
        var ledger = Ledger();
        var legitimate = Path.Combine(_folder, "movie.mp4.123-abc.rdpart");
        ledger.Record(legitimate);

        ledger.Record(Path.Combine(_folder, $"dir{lineBreak}sub", "movie.mp4.123-abc.rdpart"));

        Assert.Equal([legitimate], ledger.Entries());
        Assert.Equal([legitimate], Ledger().Entries());
        Assert.Equal([legitimate], File.ReadAllLines(Path.Combine(_folder, "partial-files.log")));
    }

    /// <summary>
    /// The sweeper deletes what the list names, so it only acts on what a transfer could have put there: an absolute
    /// path to a <c>.rdpart</c> file. A list edited by hand or damaged must not be able to point it at anything else.
    /// </summary>
    [Fact]
    public void Entries_that_are_not_absolute_partial_files_are_never_deleted()
    {
        var old = _time.GetUtcNow().UtcDateTime - PartialFileSweeper.Retention - TimeSpan.FromDays(1);
        var notPartial = WriteFile(Path.Combine(_folder, "precious.docx"), "keep me");
        File.SetLastWriteTimeUtc(notPartial, old);

        // Relative to the working directory of the process, which is not something a list can be trusted to mean.
        var relative = "rd-relative-" + Guid.NewGuid().ToString("N") + PartialFile.Extension;
        var relativeFull = Path.GetFullPath(relative);
        WriteFile(relativeFull, "keep me too");
        File.SetLastWriteTimeUtc(relativeFull, old);

        try
        {
            File.WriteAllLines(Path.Combine(_folder, "partial-files.log"), [notPartial, relative]);
            var ledger = Ledger();

            var removed = new PartialFileSweeper(ledger, _time, NullLogger<PartialFileSweeper>.Instance).Sweep();

            Assert.Equal(0, removed);
            Assert.True(File.Exists(notPartial));
            Assert.True(File.Exists(relativeFull));
        }
        finally
        {
            File.Delete(relativeFull);
        }
    }

    /// <summary>The cap that is in force when nobody says otherwise is the documented one, not just the one tests lower.</summary>
    [Fact]
    public void A_list_longer_than_the_default_cap_is_trimmed_to_it_keeping_the_newest()
    {
        Assert.Equal(1024, PartialFileLedger.DefaultMaxEntries);
        var records = Enumerable.Range(0, PartialFileLedger.DefaultMaxEntries + 2)
            .Select(i => Path.Combine(_folder, $"movie.mp4.{i}-a.rdpart"))
            .ToArray();
        System.IO.File.WriteAllLines(Path.Combine(_folder, "partial-files.log"), records);

        var entries = Ledger().Entries();

        Assert.Equal(PartialFileLedger.DefaultMaxEntries, entries.Count);
        Assert.Equal(records[2], entries[0]);
        Assert.Equal(records[^1], entries[^1]);
    }

    /// <summary>One slot is the smallest list that can exist; a cap of zero or less does not turn tracking into a crash.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_cap_below_one_still_keeps_the_newest_record(int maxEntries)
    {
        var ledger = Ledger(maxEntries);
        ledger.Record(Path.Combine(_folder, "a.1-a.rdpart"));
        ledger.Record(Path.Combine(_folder, "b.1-a.rdpart"));

        Assert.Equal([Path.Combine(_folder, "b.1-a.rdpart")], Ledger(maxEntries).Entries());
    }

    /// <summary>
    /// Tracking an attempt that is already tracked changes nothing - in particular it does not make the record newer.
    /// A resumed transfer that records its partial file again therefore does not push other records out.
    /// </summary>
    [Fact]
    public void Recording_a_path_twice_keeps_one_record_in_its_original_place()
    {
        var ledger = Ledger(maxEntries: 3);
        var a = Path.Combine(_folder, "a.1-a.rdpart");
        var b = Path.Combine(_folder, "b.1-a.rdpart");
        var c = Path.Combine(_folder, "c.1-a.rdpart");
        ledger.Record(a);
        ledger.Record(b);
        ledger.Record(a);
        ledger.Record(c);

        Assert.Equal([a, b, c], Ledger(maxEntries: 3).Entries());

        // Because "a" is still the oldest, the next new record is the one that pushes it out.
        ledger.Record(Path.Combine(_folder, "d.1-a.rdpart"));
        Assert.Equal([b, c, Path.Combine(_folder, "d.1-a.rdpart")], Ledger(maxEntries: 3).Entries());
    }

    /// <summary>Dropping a record from the middle leaves the others in the order they were made, also after a reload.</summary>
    [Fact]
    public void Forgetting_a_record_from_the_middle_keeps_the_order_of_the_rest_after_a_reload()
    {
        var ledger = Ledger();
        string[] letters = ["a", "b", "c", "d"];
        var names = letters.Select(n => Path.Combine(_folder, n + ".1-a.rdpart")).ToArray();
        foreach (var name in names)
        {
            ledger.Record(name);
        }

        ledger.Forget(names[1]);
        ledger.Forget(Path.Combine(_folder, "never-recorded.1-a.rdpart"));

        Assert.Equal([names[0], names[2], names[3]], Ledger().Entries());
        Assert.False(File.Exists(Path.Combine(_folder, "partial-files.log.tmp")));
    }

    /// <summary>A list edited by hand may have blank lines, padding and repeats; reading makes one clean list of it.</summary>
    [Fact]
    public void A_list_with_blank_lines_padding_and_repeats_is_read_as_distinct_records_in_first_seen_order()
    {
        var a = Path.Combine(_folder, "a.1-a.rdpart");
        var b = Path.Combine(_folder, "b.1-a.rdpart");
        System.IO.File.WriteAllLines(
            Path.Combine(_folder, "partial-files.log"),
            [string.Empty, "  " + a + "  ", b, "   ", a, b + "\t", string.Empty]);

        Assert.Equal([a, b], Ledger().Entries());
    }

    /// <summary>Evicting a record only stops tracking: the file it named is not the ledger's to delete.</summary>
    [Fact]
    public void An_evicted_record_does_not_delete_the_file_it_named()
    {
        var ledger = Ledger(maxEntries: 1);
        var first = WriteFile(Path.Combine(_folder, "first.iso.1-a.rdpart"), "half a file");
        ledger.Record(first);
        ledger.Record(Path.Combine(_folder, "second.iso.1-a.rdpart"));

        Assert.DoesNotContain(first, ledger.Entries());
        Assert.True(File.Exists(first));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_path_is_refused_by_record_and_forget(string path)
    {
        var ledger = Ledger();

        Assert.Throws<ArgumentException>(() => ledger.Record(path));
        Assert.Throws<ArgumentException>(() => ledger.Forget(path));
        Assert.Throws<ArgumentNullException>(() => ledger.Record(null!));
    }

    private PartialFileLedger Ledger(int maxEntries = PartialFileLedger.DefaultMaxEntries) =>
        new(new PartialFileLedgerOptions(Path.Combine(_folder, "partial-files.log"), maxEntries), NullLogger<PartialFileLedger>.Instance);

    private string WriteFile(string path, string contents)
    {
        System.IO.File.WriteAllText(path, contents);
        System.IO.File.SetLastWriteTimeUtc(path, _time.GetUtcNow().UtcDateTime);
        return path;
    }

    /// <summary>A partial file old enough for any pass to remove, measured against the clock the service uses.</summary>
    private string WriteOldPartial(TimeProvider clock, string name)
    {
        var path = Path.Combine(_folder, name);
        System.IO.File.WriteAllText(path, "half a file");
        System.IO.File.SetLastWriteTimeUtc(
            path,
            clock.GetUtcNow().UtcDateTime - PartialFileSweeper.Retention - TimeSpan.FromDays(1));
        return path;
    }

    /// <summary>The first wall-clock read fails, which is the first thing a pass does with a file on its list.</summary>
    private sealed class FailOnceTime(TimeProvider inner) : TimeProvider
    {
        private int _failuresLeft = 1;

        public override DateTimeOffset GetUtcNow() =>
            Interlocked.Decrement(ref _failuresLeft) >= 0
                ? throw new InvalidOperationException("The clock failed on purpose.")
                : inner.GetUtcNow();

        public override long GetTimestamp() => inner.GetTimestamp();

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            inner.CreateTimer(callback, state, dueTime, period);
    }

    private sealed class SweepFailureLogger(TaskCompletionSource failed) : ILogger<PartialFileSweeper>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                failed.TrySetResult();
            }
        }
    }
}
