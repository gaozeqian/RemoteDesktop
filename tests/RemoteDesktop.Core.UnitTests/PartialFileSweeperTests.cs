using Microsoft.Extensions.Logging.Abstractions;
using RemoteDesktop.Features.FileTransfer;

namespace RemoteDesktop.Core.UnitTests;

/// <summary>
/// The unfinished transfers nobody came back for. A controller can leave one of these behind on somebody else's
/// machine for every attempt it abandons — the file name carries the size and time it was aiming at, so each attempt
/// is a different name — and without this they stay in that folder for good.
/// </summary>
public sealed class PartialFileSweeperTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("rd-partial-").FullName;
    private readonly ManualTime _time = new();

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void A_record_the_ledger_writes_survives_a_new_ledger()
    {
        var partial = Path.Combine(_folder, "movie.mp4.123-abc.rdpart");
        Ledger().Record(partial);

        Assert.Equal([partial], Ledger().Entries());
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

    private PartialFileLedger Ledger(int maxEntries = PartialFileLedger.DefaultMaxEntries) =>
        new(new PartialFileLedgerOptions(Path.Combine(_folder, "partial-files.log"), maxEntries), NullLogger<PartialFileLedger>.Instance);

    private string WriteFile(string path, string contents)
    {
        System.IO.File.WriteAllText(path, contents);
        System.IO.File.SetLastWriteTimeUtc(path, _time.GetUtcNow().UtcDateTime);
        return path;
    }
}
