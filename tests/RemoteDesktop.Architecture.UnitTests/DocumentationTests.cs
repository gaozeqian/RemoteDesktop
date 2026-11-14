using System.Text;
using System.Text.RegularExpressions;

namespace RemoteDesktop.Architecture.UnitTests;

/// <summary>
/// Enforces docs/STYLE.md: which documents have to exist, where one is allowed to live, what it may be called, and
/// that every link in one points at something that is actually there.
///
/// The same argument as <see cref="RepositoryStructureTests"/> next door. These were conventions written down in
/// CONTRIBUTING.md and docs/README.md, and honoured only by whoever happened to read them: the index had already
/// silently dropped four documents, and docs/design/README.md had been linking at a path that never existed
/// since the day the port plans moved. Neither is visible in a diff, which is exactly why a test looks at it now.
///
/// Formatting (blank lines around headings and lists, code fences tagged with a language) is deliberately *not*
/// here — that is markdownlint's job in CI. This class checks meaning: does the file belong there, and does the
/// link go anywhere. The split is spelled out in the “靠测试，不靠自觉” section of docs/STYLE.md.
/// </summary>
public class DocumentationTests
{
    private static readonly string Root = FindRepositoryRoot();

    /// <summary>The entry points a reader is expected to be able to reach (“必备文件” in docs/STYLE.md).</summary>
    private static readonly string[] RequiredDocuments =
    [
        "README.md",
        "CONTRIBUTING.md",
        "SECURITY.md",
        "CHANGELOG.md",
        "LICENSE",
        "docs/README.md",
        "docs/STYLE.md",
        "docs/usage/README.md",
        "docs/design/README.md",
        "docs/ops/README.md",
        "docs/ports/README.md",
        ".github/PULL_REQUEST_TEMPLATE.md",
    ];

    /// <summary>
    /// The only names allowed at the repository root (“叫什么：文件名规范” in docs/STYLE.md): every one of them is a name GitHub or
    /// convention fixes, so a new one appearing here is a decision nobody made.
    /// CODE_OF_CONDUCT.md is on the list because it is the one that will be added if this ever takes outside
    /// contributors; it is not required.
    /// </summary>
    private static readonly string[] RootDocumentNames =
    [
        "README.md",
        "CONTRIBUTING.md",
        "SECURITY.md",
        "CHANGELOG.md",
        "THIRD-PARTY-NOTICES.md",
        "CODE_OF_CONDUCT.md",
        // The guidance file Claude Code reads at the start of every session. Its name is fixed by that tool, like
        // PULL_REQUEST_TEMPLATE.md is fixed by GitHub, so it cannot be kebab-cased into docs/.
        "CLAUDE.md",
    ];

    /// <summary>The index and the style guide, and nothing else (“放哪：按读者分目录” in docs/STYLE.md).</summary>
    private static readonly string[] DocsRootAllowance = ["docs/README.md", "docs/STYLE.md"];

    /// <summary>The three sections an outside reader looks for first (“怎么写：README 是落地页” in docs/STYLE.md).</summary>
    private static readonly string[] RequiredReadmeSections = ["构建与测试", "文档", "许可证"];

    /// <summary>
    /// Never documentation. artifacts/ and TestResults/ are build output, .workbuddy/ is the tool's own scratch
    /// space (gitignored), and bin/obj hold whatever an exception to ArtifactsPath left behind.
    /// </summary>
    private static readonly string[] ExcludedDirectories =
        ["artifacts", "TestResults", ".git", ".workbuddy", ".workbuddy-ai", "node_modules", "bin", "obj"];

    [Fact]
    public void Required_documents_exist()
    {
        var missing = RequiredDocuments
            .Where(relative => !File.Exists(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar))))
            .ToArray();

        AssertNoOffenders(missing, "documents the “必备文件” section of docs/STYLE.md requires but which do not exist:");
    }

    /// <summary>
    /// One H1 per file, and it is the first line. The first line because a reader who opens the raw file, and every
    /// tool that renders a title (GitHub, an editor preview, a PDF export), takes it from there.
    /// </summary>
    [Fact]
    public void Every_document_has_exactly_one_title()
    {
        var offenders = MarkdownDocuments()
            .Where(path => !Relative(path).StartsWith(".github/", StringComparison.Ordinal))
            .Where(path =>
            {
                var body = WithoutFencedBlocks(File.ReadAllText(path));
                var titles = Regex.Matches(body, @"^#[ \t]+\S.*$", RegexOptions.Multiline);
                if (titles.Count != 1)
                {
                    return true;
                }

                var firstLine = body.Split('\n').FirstOrDefault(line => line.Trim().Length > 0);
                return firstLine is null || !Regex.IsMatch(firstLine.Trim(), @"^#[ \t]+\S");
            })
            .Select(Relative)
            .ToArray();

        AssertNoOffenders(offenders, "documents that do not open with exactly one H1:");
    }

    /// <summary>
    /// Every document under docs/ is reachable from the index. A document nobody links to is a document nobody
    /// reads: the four that had quietly gone missing from this index were each written by somebody who meant them
    /// to be found. A directory's own README is not an entry — it is linked from the index as a section, or not
    /// needed at all.
    /// </summary>
    [Fact]
    public void Every_document_under_docs_is_reachable_from_the_index()
    {
        var index = Path.Combine(Root, "docs", "README.md");
        var linked = Links(File.ReadAllText(index))
            .Select(target => ResolveTarget(index, target))
            .Where(resolved => resolved is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unindexed = MarkdownDocuments()
            .Where(path => IsUnder(path, Path.Combine(Root, "docs")))
            .Where(path => Path.GetFileName(path) != "README.md")
            .Where(path => !linked.Contains(path))
            .Select(Relative)
            .ToArray();

        AssertNoOffenders(unindexed, "documents under docs/ that nothing links to from docs/README.md:");
    }

    /// <summary>
    /// Every relative link and image resolves, and an anchor names a heading that exists. This is the check that
    /// would have caught the design document linking to the macOS port plan by a bare file name, when the two had
    /// never been in the same directory. Anchors are checked because GitHub's Chinese heading anchors are pasted
    /// by hand and rot silently.
    /// </summary>
    [Fact]
    public void Every_relative_link_resolves()
    {
        var broken = new List<string>();

        foreach (var document in MarkdownDocuments())
        {
            foreach (var target in Links(File.ReadAllText(document)))
            {
                if (IsExternal(target))
                {
                    continue;
                }

                var resolved = ResolveTarget(document, target);
                if (resolved is null || (!File.Exists(resolved) && !Directory.Exists(resolved)))
                {
                    broken.Add($"{Relative(document)} -> {target}");
                    continue;
                }

                var anchor = AnchorOf(target);
                if (anchor.Length == 0 || !resolved.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!File.Exists(resolved))
                {
                    broken.Add($"{Relative(document)} -> {target} (anchored link to a directory)");
                    continue;
                }

                if (!Slugs(File.ReadAllText(resolved)).Contains(anchor))
                {
                    broken.Add($"{Relative(document)} -> {target} (no heading with that anchor)");
                }
            }
        }

        AssertNoOffenders([.. broken], "relative links that do not resolve:");
    }

    /// <summary>
    /// The naming rules in docs/STYLE.md (“叫什么：文件名规范”). docs/ and the script trees are lower-case kebab-case;
    /// the two places where the name is fixed by GitHub rather than by us.
    /// </summary>
    [Fact]
    public void Document_file_names_follow_the_conventions()
    {
        var offenders = MarkdownDocuments()
            .Select(Relative)
            .Where(relative => !FollowsNamingRule(relative))
            .ToArray();

        AssertNoOffenders(offenders, "documents whose name does not follow the “叫什么：文件名规范” section of docs/STYLE.md:");
    }

    /// <summary>
    /// docs/STYLE.md 的“放哪：按读者分目录”一节. The root of docs/ is the index and the rules, nothing else; the moment a third file lands
    /// there, "everything is in docs/" stops being true for the reader who skims.
    /// </summary>
    [Fact]
    public void Docs_root_holds_only_the_index_and_the_style_guide()
    {
        var extras = Directory
            .EnumerateFiles(Path.Combine(Root, "docs"), "*.md")
            .Select(Relative)
            .Where(relative => !DocsRootAllowance.Contains(relative))
            .ToArray();

        AssertNoOffenders(extras, "files at the root of docs/ that belong in a subdirectory:");
    }

    /// <summary>docs/STYLE.md 的“怎么写：README 是落地页”一节 — README is a landing page; these are the things people come to it for.</summary>
    [Fact]
    public void Readme_carries_the_required_sections()
    {
        var readme = Path.Combine(Root, "README.md");
        var sections = Regex
            .Matches(WithoutFencedBlocks(File.ReadAllText(readme)), @"^##[ \t]+(.+)$", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value.Trim())
            .ToArray();

        var missing = RequiredReadmeSections
            .Where(required => !sections.Any(section => section.Contains(required, StringComparison.Ordinal)))
            .ToArray();

        AssertNoOffenders(missing, "H2 sections README.md is missing:");
    }

    private static bool FollowsNamingRule(string relative)
    {
        var name = Path.GetFileName(relative);

        // The two names that are a convention rather than a decision: a directory's own README, and the style guide
        // every contributor is told to read. Everything else under docs/ is kebab-case.
        if (name is "README.md" or "STYLE.md")
        {
            return true;
        }

        // GitHub renders these: PULL_REQUEST_TEMPLATE.md and the issue forms get their names from GitHub, not us.
        if (relative.StartsWith(".github/", StringComparison.Ordinal))
        {
            return true;
        }

        if (!relative.Contains('/'))
        {
            return RootDocumentNames.Contains(name);
        }

        return Regex.IsMatch(name, @"^[a-z0-9]+(-[a-z0-9]+)*\.md$");
    }

    /// <summary>
    /// Link and image targets, minus the ones that are not paths. Titles (<c>"path" "title"</c>) are dropped, and so
    /// is inline code: an example written as <c>`[标题](相对路径.md)`</c> is a reader's template, not a link, and the
    /// style guide is full of those.
    /// </summary>
    private static string[] Links(string markdown) =>
        Regex
            .Matches(WithoutInlineCode(WithoutFencedBlocks(markdown)), @"\]\(\s*<?([^)\s>]+)>?")
            .Select(match => match.Groups[1].Value)
            .ToArray();

    /// <summary>Single-backtick spans, blanked. Fences were already removed by the caller.</summary>
    private static string WithoutInlineCode(string markdown) =>
        Regex.Replace(markdown, "`[^`\n]*`", string.Empty);

    private static bool IsExternal(string target) =>
        target.StartsWith('#')
        || target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
        || target.StartsWith("//", StringComparison.Ordinal);

    /// <summary>Absolute path the link points at, or null for a bare anchor. Anchors are not part of a path.</summary>
    private static string? ResolveTarget(string document, string target)
    {
        var path = target;
        var hash = path.IndexOf('#');
        if (hash >= 0)
        {
            path = path[..hash];
        }

        if (path.Length == 0)
        {
            return null;
        }

        path = Uri.UnescapeDataString(path).Replace('\\', '/');
        var baseDirectory = path.StartsWith('/')
            ? Root
            : Path.GetDirectoryName(document)!;

        return Path.GetFullPath(Path.Combine(baseDirectory, path.TrimStart('/')));
    }

    private static string AnchorOf(string target)
    {
        var hash = target.IndexOf('#');
        return hash < 0 ? string.Empty : target[(hash + 1)..];
    }

    /// <summary>
    /// GitHub's heading anchors. Letters and digits survive — Chinese headings keep theirs, which is why they mostly
    /// work — spaces become hyphens, and everything else is dropped. Underscores and hyphens stay.
    /// </summary>
    private static HashSet<string> Slugs(string markdown)
    {
        var slugs = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match heading in Regex.Matches(WithoutFencedBlocks(markdown), @"^#{1,6}[ \t]+(.+?)[ \t]*#*$", RegexOptions.Multiline))
        {
            var text = Regex.Replace(heading.Groups[1].Value, @"\[([^\]]*)\]\([^)]*\)", "$1");
            var slug = new StringBuilder();
            foreach (var character in text.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(character) || character is '_' or '-')
                {
                    slug.Append(character);
                }
                else if (character == ' ')
                {
                    slug.Append('-');
                }
            }

            slugs.Add(slug.ToString());
        }

        return slugs;
    }

    /// <summary>
    /// Line count is preserved so that "is the H1 the first line" stays answerable. An unclosed fence swallows the
    /// rest of the file, which is the right reading: a reader sees code there too.
    /// </summary>
    private static string WithoutFencedBlocks(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var inside = false;
        var fence = string.Empty;

        for (var index = 0; index < lines.Length; index++)
        {
            var trimmed = lines[index].TrimStart();
            if (!inside && (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)))
            {
                inside = true;
                fence = trimmed[..3];
                lines[index] = string.Empty;
            }
            else if (inside && trimmed.StartsWith(fence, StringComparison.Ordinal))
            {
                inside = false;
                lines[index] = string.Empty;
            }
            else if (inside)
            {
                lines[index] = string.Empty;
            }
        }

        return string.Join('\n', lines);
    }

    private static IEnumerable<string> MarkdownDocuments() =>
        Directory
            .EnumerateFiles(Root, "*.md", SearchOption.AllDirectories)
            .Where(path => !Path
                .GetRelativePath(Root, path)
                .Replace('\\', '/')
                .Split('/')
                .SkipLast(1)
                .Any(ExcludedDirectories.Contains));

    private static bool IsUnder(string path, string directory) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string Relative(string absolutePath) =>
        Path.GetRelativePath(Root, absolutePath).Replace('\\', '/');

    private static void AssertNoOffenders(string[] offenders, string rule)
    {
        Assert.True(offenders.Length == 0, $"{rule}\n  {string.Join("\n  ", offenders)}");
    }

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
}
