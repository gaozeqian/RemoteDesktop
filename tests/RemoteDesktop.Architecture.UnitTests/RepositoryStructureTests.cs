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
    /// <c>&lt;区域&gt;.&lt;种类&gt;Tests</c>, with the kind from a closed set (CONTRIBUTING.md §测试项目命名).
    /// The rule exists so that filtering by kind — <c>**/*.UnitTests</c> — is a thing you can rely on; a project
    /// outside the set is missed by that filter, and missed silently, which is the whole reason it is checked.
    /// </summary>
    [Fact]
    public void Test_projects_are_named_for_their_area_and_kind()
    {
        var offenders = ProjectDirectories(Path.Combine(Root, "tests"))
            .Where(name => !Regex.IsMatch(name, @"^RemoteDesktop\..+\.(Unit|Integration)Tests$"))
            .ToArray();

        AssertNoOffenders(offenders, "tests/ projects outside the closed set of kinds (CONTRIBUTING.md §测试项目命名):");
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

    /// <summary>Shell scripts are kebab-case, as every one of the thirteen already is (build/README.md).</summary>
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
