using System.Text.RegularExpressions;

namespace RemoteDesktop.Architecture.UnitTests;

/// <summary>
/// Enforces the conventions that say where a thing lives and what it is called.
///
/// The layering rules next door hold because the compiler and a test both check them — which is why three platform
/// ports did not break them once. These conventions had no such guard: they were written down in CONTRIBUTING.md and
/// build/README.md, and from then on honoured only by whoever happened to read those files. Three rounds of directory
/// reviews kept turning up new violations, each one invisible until somebody looked. They are assertions now, so a
/// file that breaks one fails the build instead of waiting for the next review.
///
/// Exceptions are listed, never silent. Every list here is meant to shrink to nothing.
/// </summary>
public class RepositoryStructureTests
{
    private static readonly string Root = FindRepositoryRoot();

    private static readonly string[] Platforms = ["linux", "macos", "windows"];
    private static readonly string[] Roles = ["client", "host", "server"];
    private static readonly string[] PackagedTrees = ["installer", "service"];

    /// <summary>
    /// <c>&lt;区域&gt;.&lt;种类&gt;Tests</c>, with the kind from a closed set (“测试项目命名” in CONTRIBUTING.md).
    /// The rule exists so that filtering by kind — <c>**/*.UnitTests</c> — is a thing you can rely on; a project
    /// outside the set is missed by that filter, and missed silently, which is the whole reason it is checked.
    /// </summary>
    [Fact]
    public void Test_projects_are_named_for_their_area_and_kind()
    {
        var offenders = ProjectDirectories(Path.Combine(Root, "tests"))
            .Where(name => !Regex.IsMatch(name, @"^RemoteDesktop\..+\.(Unit|Integration)Tests$"))
            .ToArray();

        AssertNoOffenders(offenders, "tests/ projects outside the closed set of kinds (“测试项目命名” in CONTRIBUTING.md):");
    }

    /// <summary>
    /// A project directory carries the project's name, so that a path says which assembly it builds without the
    /// .csproj having to be opened. Namespaces follow the same rule and the compiler enforces that half.
    /// </summary>
    [Fact]
    public void Project_directories_are_named_after_their_project_file()
    {
        var mismatched = ProjectFiles()
            .Where(path => Path.GetFileNameWithoutExtension(path) != Path.GetFileName(Path.GetDirectoryName(path)!))
            .Select(Relative)
            .ToArray();

        AssertNoOffenders(mismatched, "project directories not named after their .csproj:");
    }

    /// <summary>
    /// A .cs file is named either after a type it declares, or after the concept it groups — in which case it must
    /// hold more than one type, because that is the only thing that makes a concept name honest. A file named after
    /// one type it does not contain is the failure this catches: it sends a reader looking for a type that is
    /// somewhere else, or nowhere.
    /// </summary>
    [Fact]
    public void Source_file_names_match_what_they_declare()
    {
        var offenders = SourceFiles()
            .Where(path =>
            {
                var stem = Path.GetFileNameWithoutExtension(path).Split('.')[0];
                if (stem == "Program")
                {
                    return false;   // top-level statements; there is no type to be named after
                }

                var declared = DeclaredTypeNames(File.ReadAllText(path));
                return declared.Count > 0 && !declared.Contains(stem) && declared.Count == 1;
            })
            .Select(Relative)
            .ToArray();

        AssertNoOffenders(offenders, ".cs files named after a type they do not declare, nor after a concept they group:");
    }

    /// <summary>Shell scripts under build/ and native/ are kebab-case (build/README.md).</summary>
    [Fact]
    public void Shell_scripts_are_kebab_case()
    {
        var offenders = FilesUnder("build", "*.sh")
            .Concat(FilesUnder("native", "*.sh"))
            .Where(path => !Regex.IsMatch(Path.GetFileName(path), @"^[a-z0-9]+(-[a-z0-9]+)*\.sh$"))
            .Select(Relative)
            .ToArray();

        AssertNoOffenders(offenders, "shell scripts that are not kebab-case:");
    }

    /// <summary>
    /// PowerShell scripts are Verb-Noun with a verb from PowerShell's approved list, the opposite of the .sh rule
    /// and for the same reason: each language gets its own convention (build/README.md).
    ///
    /// This one had five grandfathered violations until the installer and service scripts were renamed: their names
    /// appeared in about eighty places, so they went last. There is no exemption list any more — every .ps1 under
    /// build/ is checked, which is the state the list existed to reach.
    /// </summary>
    [Fact]
    public void PowerShell_scripts_are_verb_noun()
    {
        var offenders = FilesUnder("build", "*.ps1")
            .Where(path =>
            {
                var match = Regex.Match(Path.GetFileName(path), @"^([A-Z][a-z]+)-([A-Za-z0-9]+)\.ps1$");
                return !match.Success || !ApprovedVerbs.Contains(match.Groups[1].Value);
            })
            .Select(Relative)
            .ToArray();

        AssertNoOffenders(offenders, "PowerShell scripts that are not Verb-Noun with an approved verb:");
    }

    /// <summary>
    /// build/ is laid out platform first, then role (build/README.md). Platform is the outer level because it
    /// answers the first question — can this script run on this machine — and the platform names are windows /
    /// macos / linux, not a distribution or a package format.
    ///
    /// Only the two axes are checked here. What sits at the shared level of installer/ is not: the rule there is
    /// "shared by two or more roles or platforms", which is a judgement about content rather than a name.
    /// </summary>
    [Fact]
    public void Build_packaging_is_laid_out_by_platform_then_role()
    {
        var wrongPlatform = PackagedTrees
            .SelectMany(kind => Directory.GetDirectories(Path.Combine(Root, "build", kind)))
            .Where(path => !Platforms.Contains(Path.GetFileName(path)))
            .Select(Relative);

        var wrongRole = Platforms
            .SelectMany(platform => Directory.GetDirectories(Path.Combine(Root, "build", "installer", platform)))
            .Where(path => !Roles.Contains(Path.GetFileName(path)))
            .Select(Relative);

        AssertNoOffenders(
            wrongPlatform.Concat(wrongRole).ToArray(),
            "build/ directories that are neither a platform (windows/macos/linux) nor a role (client/host/server):");
    }

    /// <summary>
    /// The Linux package has to let the console pin the certificate its own node serves.
    /// </summary>
    /// <remarks>
    /// The node is registered as <c>https://127.0.0.1:5080</c>, while a certificate generated on this machine does
    /// not necessarily name that address: Debian and Ubuntu resolve the host name to 127.0.1.1 in /etc/hosts. Without
    /// the pin, every scrape and the device page fail with a name mismatch — which is what a freshly installed Linux
    /// server showed. The Windows and macOS installers have always passed this argument; this is the one that did
    /// not, and nothing else in the build reads these scripts.
    /// </remarks>
    [Fact]
    public void The_linux_package_lets_the_console_pin_its_own_certificate()
    {
        var postinst = File.ReadAllText(Path.Combine(Root, "build", "installer", "linux", "server", "postinst"));

        Assert.Contains("--ops-node-certificate-path", postinst, StringComparison.Ordinal);
        Assert.Contains("tls/signaling.crt", postinst, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Linux and macOS package scripts must not put secrets on the command line of the settings step.
    /// </summary>
    /// <remarks>
    /// An argument is readable by every local account (<c>ps</c>, <c>/proc/&lt;pid&gt;/cmdline</c>) while the step runs.
    /// <c>--configure-system</c> reads the same values from <c>RD_RELAY_KEY</c>, <c>RD_POSTGRES</c> and
    /// <c>RD_TLS_PASSWORD</c>, which is what the Windows installer already uses. configure-server.sh only forwards
    /// what the operator typed and is deliberately not covered.
    /// </remarks>
    [Theory]
    [InlineData("linux", "postinst")]
    [InlineData("macos", "scripts/postinstall")]
    public void The_server_package_scripts_pass_secrets_through_the_environment(string platform, string script)
    {
        var path = Path.Combine(new[] { Root, "build", "installer", platform, "server" }.Concat(script.Split('/')).ToArray());
        var text = File.ReadAllText(path);

        foreach (var option in new[] { "--relay-key", "--postgres", "--tls-password" })
        {
            Assert.DoesNotContain(option, text, StringComparison.Ordinal);
        }

        Assert.Contains("RD_POSTGRES", text, StringComparison.Ordinal);
        Assert.Contains("RD_TLS_PASSWORD", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The macOS pre-install script reads the pane's answers file from the world-writable <c>/var/tmp</c> as root, and
    /// puts the signaling address into a PlistBuddy command.
    /// </summary>
    /// <remarks>
    /// A symbolic link at that path would make root copy whatever it points at, and an address check without an end
    /// anchor lets <c>ws://h/ "x</c> through. The scripts need macOS to run; the behaviour was exercised in a Linux
    /// container with the macOS tools stubbed, and this keeps the two guards from being edited away.
    /// </remarks>
    [Fact]
    public void The_macos_answers_script_refuses_a_symbolic_link_and_anchors_the_address_check()
    {
        var script = File.ReadAllText(Path.Combine(Root, "build", "installer", "macos", "client", "scripts", "apply-answers.sh"));

        Assert.Contains("[ -L \"$plugin\" ]", script, StringComparison.Ordinal);

        var grepLine = script.Split('\n').Single(line => line.Contains("grep -Eq '^wss?://", StringComparison.Ordinal));
        Assert.Contains(")?$'", grepLine, StringComparison.Ordinal);

        // grep judges one line at a time, so the whole-string character check has to be there as well.
        Assert.Contains("*[[:space:]]*|*[[:cntrl:]]*|*'\"'*|*'\\'*", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Linux server package's config script must only treat a leftover connection string as an answer when the
    /// existing settings use PostgreSQL.
    /// </summary>
    /// <remarks>
    /// The server removes both connection-string keys when it writes sqlite or none, so a string beside another
    /// provider is a hand edit; counting it skipped the second question and the server then reused it silently.
    /// </remarks>
    [Fact]
    public void The_linux_config_script_reads_the_provider_before_trusting_a_connection_string()
    {
        var script = File.ReadAllText(Path.Combine(Root, "build", "installer", "linux", "server", "config"));

        var provider = script.IndexOf("existing_provider=$value", StringComparison.Ordinal);
        var decision = script.IndexOf("case \"$existing_provider\" in", StringComparison.Ordinal);
        var connection = script.IndexOf("\"PostgresConnectionString\"[[:space:]]", StringComparison.Ordinal);

        Assert.True(provider >= 0, "The script does not remember the provider the existing settings name.");
        Assert.True(provider < decision, "The provider must be read before it is used.");
        Assert.True(decision < connection, "A connection string must only be looked for inside the provider decision.");
    }

    /// <summary>
    /// In POSIX sh a single-quoted <c>'\\'</c> is two backslashes, so a <c>case</c> pattern written that way never
    /// matches the one backslash the check is meant to refuse.
    /// </summary>
    [Fact]
    public void The_linux_common_script_refuses_a_single_backslash_in_the_signaling_address()
    {
        var script = File.ReadAllText(Path.Combine(Root, "build", "installer", "linux", "common.sh"));

        Assert.DoesNotContain("*'\\\\'*", script, StringComparison.Ordinal);
        Assert.Contains("*'\\'*", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The release workflow's job-level <c>env</c> is visible to every step, restore, build and the tests included, so
    /// it may only hold the booleans the steps' <c>if:</c> conditions read — never a certificate, password or key.
    /// </summary>
    /// <remarks>
    /// A Secret belongs in the <c>env</c> of the step that uses it (ten spaces deep), which this does not restrict.
    /// </remarks>
    [Fact]
    public void The_release_workflow_keeps_secrets_out_of_job_level_environment()
    {
        var lines = File.ReadAllLines(Path.Combine(Root, ".github", "workflows", "release.yml"));

        var offenders = lines
            .Select((line, index) => (line, number: index + 1))
            .Where(entry => System.Text.RegularExpressions.Regex.IsMatch(entry.line, @"^      [A-Za-z_]+: \$\{\{ .*secrets\."))
            .Where(entry => !entry.line.Contains("!= ''", StringComparison.Ordinal))
            .Select(entry => $"release.yml:{entry.number}: {entry.line.Trim()}")
            .ToArray();

        AssertNoOffenders(offenders, "Job-level env entries that carry a Secret rather than a boolean computed from it:");
    }

    /// <summary>
    /// The manual uninstaller removes a directory recursively, so it must take the path literally and refuse a
    /// dangerous one before it stops the service.
    /// </summary>
    [Fact]
    public void The_host_uninstaller_validates_its_install_directory_before_stopping_anything()
    {
        var script = File.ReadAllText(Path.Combine(Root, "build", "service", "windows", "Uninstall-HostService.ps1"));

        Assert.DoesNotContain("Remove-Item -Recurse", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Test-Path $", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath", script, StringComparison.Ordinal);

        var refusal = script.IndexOf("Refusing to remove", StringComparison.Ordinal);
        var stop = script.IndexOf("Stop-Service", StringComparison.Ordinal);
        Assert.True(refusal >= 0, "The uninstaller has no refusal for dangerous directories.");
        Assert.True(refusal < stop, "The directory must be validated before the service is stopped.");
    }

    /// <summary>
    /// Both installers generate WiX source from the publish folders. A folder name goes into an XML attribute, so it has
    /// to be escaped; the server script once wrote it raw while the client script escaped it.
    /// </summary>
    [Theory]
    [InlineData("client", "Build-ClientInstaller.ps1")]
    [InlineData("server", "Build-ServerInstaller.ps1")]
    public void The_installer_file_list_escapes_folder_names_written_into_wix_source(string role, string script)
    {
        var text = File.ReadAllText(Path.Combine(Root, "build", "installer", "windows", role, script));

        Assert.Contains("SecurityElement]::Escape($directory.Name)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Name=\"\"$($directory.Name)\"\"", text, StringComparison.Ordinal);
    }

    private static readonly string[] SharedInstallerFunctions =
    [
        "Find-SigningCertificate", "Get-SigningCertificate", "Invoke-Signing",
        "Read-PfxCertificate", "Remove-GeneratedDirectory", "Resolve-SigningTool",
    ];

    /// <summary>
    /// The signing and cleanup functions both Windows packaging scripts need are defined once, in
    /// build/installer/windows/Import-SigningHelpers.ps1, and dot-sourced. They used to be pasted into each script, and a
    /// fix made to one copy never reached the other.
    /// </summary>
    [Theory]
    [InlineData("client", "Build-ClientInstaller.ps1")]
    [InlineData("server", "Build-ServerInstaller.ps1")]
    public void The_windows_packaging_scripts_share_their_signing_functions_instead_of_copying_them(string role, string script)
    {
        var text = File.ReadAllText(Path.Combine(Root, "build", "installer", "windows", role, script));

        var redefined = SharedInstallerFunctions
            .Where(name => Regex.IsMatch(text, $@"^\s*function\s+{Regex.Escape(name)}\b", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            .ToArray();
        AssertNoOffenders(redefined, $"{script} defines functions that Import-SigningHelpers.ps1 already provides:");

        Assert.Contains(@". (Join-Path $PSScriptRoot '..\Import-SigningHelpers.ps1')", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shared file has to define every function the two scripts stopped carrying, and be UTF-8 with a byte order
    /// mark like the scripts that load it: Windows PowerShell 5.1 reads a file without one in the machine's code page.
    /// </summary>
    [Fact]
    public void The_shared_signing_helpers_define_every_function_the_packaging_scripts_dropped()
    {
        var path = Path.Combine(Root, "build", "installer", "windows", "Import-SigningHelpers.ps1");
        Assert.True(File.Exists(path), "build/installer/windows/Import-SigningHelpers.ps1 is missing.");

        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "Import-SigningHelpers.ps1 must be UTF-8 with a byte order mark.");

        var text = File.ReadAllText(path);
        var missing = SharedInstallerFunctions
            .Where(name => !Regex.IsMatch(text, $@"^function\s+{Regex.Escape(name)}\b", RegexOptions.Multiline))
            .ToArray();
        AssertNoOffenders(missing, "functions missing from Import-SigningHelpers.ps1:");
    }

    /// <summary>Fails with every offending path listed; xunit's own formatting truncates a long collection.</summary>
    private static void AssertNoOffenders(string[] offenders, string rule)
    {
        Assert.True(offenders.Length == 0, $"{rule}\n  {string.Join("\n  ", offenders)}");
    }

    /// <summary>The type names declared at the file's outermost indentation — nested types do not count.</summary>
    private static HashSet<string> DeclaredTypeNames(string source)
    {
        var declarations = Regex
            .Matches(source, @"^([ \t]*)(?:(?:public|internal|private|protected|file)\s+)?(?:(?:static|sealed|abstract|partial|readonly|unsafe|ref)\s+)*(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+([A-Za-z_]\w*)", RegexOptions.Multiline)
            .Select(match => (Indent: match.Groups[1].Value.Length, Name: match.Groups[2].Value))
            .ToArray();

        if (declarations.Length == 0)
        {
            return [];
        }

        var outermost = declarations.Min(declaration => declaration.Indent);
        return declarations
            .Where(declaration => declaration.Indent == outermost)
            .Select(declaration => declaration.Name)
            .ToHashSet();
    }

    private static IEnumerable<string> ProjectFiles() =>
        Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, "tests"), "*.csproj", SearchOption.AllDirectories));

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, "tests"), "*.cs", SearchOption.AllDirectories));

    private static IEnumerable<string> ProjectDirectories(string parent) =>
        Directory.GetDirectories(parent).Select(Path.GetFileName)!;

    private static IEnumerable<string> FilesUnder(string tree, string pattern) =>
        Directory.EnumerateFiles(Path.Combine(Root, tree), pattern, SearchOption.AllDirectories);

    private static string Relative(string absolutePath) =>
        Path.GetRelativePath(Root, absolutePath).Replace('\\', '/');

    /// <summary>Walks up from the test binary until the solution file, so the layout can move without breaking this.</summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RemoteDesktop.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException($"No RemoteDesktop.slnx above {AppContext.BaseDirectory}.");
    }

    /// <summary>PowerShell's approved verbs (Get-Verb), which the naming rule defers to.</summary>
    private static readonly HashSet<string> ApprovedVerbs = new(
        (
            "Add,Clear,Close,Copy,Enter,Exit,Find,Format,Get,Hide,Join,Lock,Move,New,Open,Optimize,Pop,Push,Redo," +
            "Remove,Rename,Reset,Resize,Search,Select,Set,Show,Skip,Split,Step,Switch,Undo,Unlock,Watch," +
            "Backup,Checkpoint,Compare,Compress,Convert,ConvertFrom,ConvertTo,Dismount,Edit,Expand,Export,Group," +
            "Import,Initialize,Limit,Merge,Mount,Out,Publish,Restore,Save,Sync,Unpublish,Update," +
            "Approve,Assert,Build,Complete,Confirm,Deny,Deploy,Disable,Enable,Install,Invoke,Register,Request," +
            "Restart,Resume,Start,Stop,Submit,Suspend,Test,Uninstall,Unregister,Wait"
        ).Split(','),
        StringComparer.Ordinal);
}
