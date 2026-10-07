using System.Text;
using RemoteDesktop.Abstractions.Terminal;
using RemoteDesktop.Features.Terminal.Emulation;
using RemoteDesktop.Platform.Windows.Terminal;

namespace RemoteDesktop.Transport.IntegrationTests;

/// <summary>
/// Real programs in a real pseudoconsole, drawn by the emulator: the proof that the terminal copes with what people
/// actually run (PowerShell with its line editor, full-screen editors and pagers).
/// </summary>
public sealed class RealProgramTerminalTests : IDisposable
{
    private const string GitBin = @"C:\Program Files\Git\usr\bin";

    private readonly string _folder = Directory.CreateTempSubdirectory("rd-tui-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task PowerShell_runs_commands_edits_the_line_and_recalls_history()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ConPTY is Windows only.");
        await using var shell = await Shell.StartAsync("powershell", 100, 30);
        await shell.WaitForAsync("PS ");

        // Accented Latin and CJK: multi-byte UTF-8 and cells that take two columns. No dingbat here on purpose: a
        // character the host console cannot represent (U+2713 in a Western code page) comes back as a space, which
        // says nothing about this side. The emulator's own handling of one is pinned in TerminalEmulatorTests.
        shell.Type("Write-Output 'héllo 你好'");
        shell.Key(TerminalKey.Enter);
        await shell.WaitForAsync("héllo 你好", occurrences: 2); // the command line and its output

        shell.Type("1..3 | ForEach-Object { \"row$_\" }");
        shell.Key(TerminalKey.Enter);
        await shell.WaitForAsync("row3");

        // The line editor: type, change your mind with Backspace, recall history with the arrow key.
        shell.Type("echo wrongx");
        shell.Key(TerminalKey.Backspace);
        shell.Type("!");
        await shell.WaitForAsync("echo wrong!");
        shell.Key(TerminalKey.Enter);
        shell.Key(TerminalKey.Up);
        await shell.WaitForAsync("PS ", predicate: screen => screen.Split('\n').Any(l => l.TrimEnd().EndsWith("echo wrong!", StringComparison.Ordinal) && l.Contains("PS ", StringComparison.Ordinal)));

        shell.Control('c');
        await Task.Delay(200, Ct);
        shell.Type("exit");
        shell.Key(TerminalKey.Enter);
        Assert.Equal(0, await shell.ExitCodeAsync());
    }

    [Fact]
    public async Task Colours_from_a_real_shell_reach_the_cells()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ConPTY is Windows only.");
        await using var shell = await Shell.StartAsync("powershell", 100, 30);
        await shell.WaitForAsync("PS ");

        shell.Type("Write-Host 'redtext' -ForegroundColor Red");
        shell.Key(TerminalKey.Enter);
        await shell.WaitForAsync("redtext", occurrences: 2);

        var coloured = shell.Cells().Where(c => c.Character == 'r' && !c.Foreground.IsDefault).ToList();
        Assert.NotEmpty(coloured);
    }

    [Fact]
    public async Task Vim_uses_the_alternate_screen_edits_a_file_and_hands_the_screen_back()
    {
        Assert.SkipUnless(File.Exists(Path.Combine(GitBin, "vim.exe")), "vim from Git for Windows is not installed.");
        var file = Path.Combine(_folder, "note.txt");
        await using var shell = await Shell.StartAsync("cmd", 100, 30);
        await shell.WaitForAsync(">");

        shell.Type("echo before vim");
        shell.Key(TerminalKey.Enter);
        await shell.WaitForAsync("before vim", occurrences: 2);
        shell.Type($"\"{Path.Combine(GitBin, "vim.exe")}\" \"{file}\"");
        shell.Key(TerminalKey.Enter);

        await shell.WaitUntilAsync(s => s.IsAlternateScreen(), "the alternate screen");
        await shell.WaitForAsync("note.txt");
        await shell.WaitUntilAsync(s => s.Screen().Split('\n').Count(l => l == "~") >= 20, "the empty-buffer markers down the left edge");

        shell.Type("ihello from vim 你好");
        await shell.WaitForAsync("-- INSERT --");
        shell.Key(TerminalKey.Escape);
        shell.Type(":wq");
        shell.Key(TerminalKey.Enter);

        await shell.WaitUntilAsync(s => !s.IsAlternateScreen(), "the normal screen again");
        await shell.WaitForAsync("before vim");
        Assert.Equal("hello from vim 你好", (await File.ReadAllTextAsync(file, Ct)).Trim());
    }

    [Fact]
    public async Task Vim_scrolls_with_page_keys_and_control_keys_and_moves_the_cursor()
    {
        Assert.SkipUnless(File.Exists(Path.Combine(GitBin, "vim.exe")), "vim from Git for Windows is not installed.");
        var file = Path.Combine(_folder, "lines.txt");
        await File.WriteAllLinesAsync(file, Enumerable.Range(1, 200).Select(i => $"line number {i}"), Ct);
        await using var shell = await Shell.StartAsync("cmd", 100, 30);
        await shell.WaitForAsync(">");
        shell.Type($"\"{Path.Combine(GitBin, "vim.exe")}\" \"{file}\"");
        shell.Key(TerminalKey.Enter);
        await shell.WaitForAsync("line number 1");

        shell.Key(TerminalKey.PageDown);
        await shell.WaitForAsync("line number 40");
        shell.Control('f'); // scroll forward a page
        await shell.WaitUntilAsync(s => s.Screen().Split('\n')[0] == "line number 53", "the second page");
        var rowBefore = shell.CursorRow();
        shell.Key(TerminalKey.Down);
        await shell.WaitUntilAsync(s => s.CursorRow() == rowBefore + 1, "the cursor moving down");


        shell.Type(":q!");
        shell.Key(TerminalKey.Enter);
        await shell.WaitUntilAsync(s => !s.IsAlternateScreen(), "leaving vim");
    }

    [Fact]
    public async Task A_pager_shows_a_page_scrolls_with_keys_and_quits()
    {
        Assert.SkipUnless(File.Exists(Path.Combine(GitBin, "less.exe")), "less from Git for Windows is not installed.");
        var file = Path.Combine(_folder, "long.txt");
        await File.WriteAllLinesAsync(file, Enumerable.Range(1, 300).Select(i => $"entry {i:000}"), Ct);
        await using var shell = await Shell.StartAsync("cmd", 100, 30);
        await shell.WaitForAsync(">");
        shell.Type($"\"{Path.Combine(GitBin, "less.exe")}\" \"{file}\"");
        shell.Key(TerminalKey.Enter);

        await shell.WaitUntilAsync(s => s.IsAlternateScreen(), "the pager's screen");
        await shell.WaitForAsync("entry 001");
        shell.Key(TerminalKey.PageDown);
        await shell.WaitForAsync("entry 030");
        shell.Type("G");
        await shell.WaitForAsync("entry 300");
        shell.Type("q");
        await shell.WaitUntilAsync(s => !s.IsAlternateScreen(), "leaving the pager");
    }

    [Fact]
    public async Task Nano_shows_its_menus_saves_and_exits_with_control_keys()
    {
        Assert.SkipUnless(File.Exists(Path.Combine(GitBin, "nano.exe")), "nano from Git for Windows is not installed.");
        var file = Path.Combine(_folder, "nano.txt");
        await using var shell = await Shell.StartAsync("cmd", 100, 30);
        await shell.WaitForAsync(">");
        shell.Type($"\"{Path.Combine(GitBin, "nano.exe")}\" \"{file}\"");
        shell.Key(TerminalKey.Enter);

        await shell.WaitForAsync("GNU nano");
        await shell.WaitForAsync("^X");
        shell.Type("typed in nano");
        await shell.WaitForAsync("typed in nano");
        shell.Control('o'); // write out
        await shell.WaitForAsync("Write to File");
        shell.Key(TerminalKey.Enter);
        await shell.WaitUntilAsync(_ =>
        {
            try
            {
                return File.Exists(file) && File.ReadAllText(file).Contains("typed in nano", StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }, "the file being written");
        shell.Control('x');
        await shell.WaitUntilAsync(s => !s.IsAlternateScreen(), "leaving nano");
    }

    /// <summary>A shell in a pseudoconsole whose output is drawn by an emulator, and whose input is encoded the way the UI does.</summary>
    private sealed class Shell : IAsyncDisposable
    {
        private readonly IPtySession _pty;
        private readonly Task _reader;
        private Exception? _readerFailure;

        private Shell(IPtySession pty, TerminalEmulator emulator)
        {
            _pty = pty;
            Emulator = emulator;
            emulator.Reply += (_, data) => Write(data.Span);
            _reader = Task.Run(async () =>
            {
                var buffer = new byte[16 * 1024];
                int read;
                try
                {
                    while ((read = await pty.Output.ReadAsync(buffer)) > 0)
                    {
                        emulator.Feed(buffer.AsSpan(0, read));
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // Disposal ends the loop this way and is expected. Anything else means the screen is frozen
                    // from here on, which is worth saying out loud: without this a stalled reader is
                    // indistinguishable from a program that simply never did what the test was waiting for.
                    _readerFailure = ex;
                }
            });
        }

        public TerminalEmulator Emulator { get; }

        public static Task<Shell> StartAsync(string shell, int columns, int rows)
        {
            var pty = new ConPtyHost().Start(new PtyStartOptions(shell, [], null, columns, rows));
            return Task.FromResult(new Shell(pty, new TerminalEmulator(columns, rows)));
        }

        public string Screen() => Emulator.ScreenText();

        public List<TerminalCell> Cells()
        {
            lock (Emulator.SyncRoot)
            {
                var cells = new List<TerminalCell>();
                for (var y = 0; y < Emulator.Rows; y++)
                {
                    cells.AddRange(Emulator.GetLine(Emulator.ScrollbackCount + y).Cells);
                }

                return cells;
            }
        }

        public void Type(string text) => Write(TerminalInput.EncodeText(text));

        public void Key(TerminalKey key, TerminalModifiers modifiers = TerminalModifiers.None) => Write(TerminalInput.EncodeKey(key, modifiers, ApplicationCursorKeys()));

        public void Control(char character) => Write(TerminalInput.EncodeControl(character, TerminalModifiers.Control)!);

        public void Resize(int columns, int rows)
        {
            Emulator.Resize(columns, rows);
            _pty.Resize(columns, rows);
        }

        public async Task WaitForAsync(string expected, int occurrences = 1, Func<string, bool>? predicate = null) =>
            await WaitUntilAsync(
                s =>
                {
                    var screen = s.Screen();
                    return predicate is null
                        ? Count(screen, expected) >= occurrences
                        : screen.Contains(expected, StringComparison.Ordinal) && predicate(screen);
                },
                $"'{expected}'");

        public async Task WaitUntilAsync(Func<Shell, bool> condition, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (!condition(this))
            {
                Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.{Why()} The screen shows:\n{Screen()}");
                await Task.Delay(50, Ct);
            }
        }

        /// <summary>The cursor row, read the way <see cref="TerminalEmulator"/> requires: under its lock.</summary>
        public int CursorRow()
        {
            lock (Emulator.SyncRoot)
            {
                return Emulator.CursorRow;
            }
        }

        public bool IsAlternateScreen()
        {
            lock (Emulator.SyncRoot)
            {
                return Emulator.IsAlternateScreen;
            }
        }

        public bool ApplicationCursorKeys()
        {
            lock (Emulator.SyncRoot)
            {
                return Emulator.ApplicationCursorKeys;
            }
        }

        /// <summary>
        /// What the emulator was doing when a wait ran out. A key the emulator encodes from its application cursor
        /// key state ("vim ignored the arrow key") and output that stopped arriving ("vim's screen froze") look
        /// identical in a screen dump, so the two have to be told apart here.
        /// </summary>
        private string Why() =>
            _reader.IsCompleted
                ? _readerFailure is null
                    ? " The pseudoconsole had already stopped producing output, so the screen could not change again."
                    : $" The pseudoconsole output reader stopped: {_readerFailure.Message}"
                : $" It is still producing output (arrow keys are sent in {(ApplicationCursorKeys() ? "application" : "normal")} mode).";

        public async Task<int> ExitCodeAsync() => await _pty.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(20), Ct);

        public async ValueTask DisposeAsync()
        {
            await _pty.DisposeAsync();
            await _reader.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ContinueWith(static _ => { }, TaskScheduler.Default);
        }

        private static int Count(string text, string value)
        {
            var count = 0;
            for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }

        private void Write(ReadOnlySpan<byte> data)
        {
            _pty.Input.Write(data);
            _pty.Input.Flush();
        }
    }
}
