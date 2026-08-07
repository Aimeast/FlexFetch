using System.Text;

namespace FlexFetch.Tests;

/// <summary>
/// Enforces the project's coding conventions across every source file:
/// UTF-8 with BOM, CRLF line endings, trailing blank line, 4-space
/// indentation (no tabs), and using directives sorted (System first,
/// namespace alphabetical) placed before the namespace declaration.
/// Run as part of the full test suite before every commit.
/// </summary>
[TestClass]
public sealed class CodeConventionTests
{
    [TestMethod]
    public void AllSourceFiles_FollowCodingConventions()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var files = Directory.EnumerateFiles(repoRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildArtifact(f))
            .ToList();

        Assert.IsGreaterThan(0, files.Count, "No source files found under the repository root");

        var violations = new List<string>();
        foreach (var file in files)
        {
            CheckFile(file, repoRoot, violations);
        }

        Assert.IsEmpty(violations,
            "Coding convention violations:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    private static bool IsBuildArtifact(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}bin{sep}", StringComparison.Ordinal)
            || path.Contains($"{sep}obj{sep}", StringComparison.Ordinal);
    }

    private static void CheckFile(string file, string repoRoot, List<string> violations)
    {
        var rel = Path.GetRelativePath(repoRoot, file);
        var bytes = File.ReadAllBytes(file);

        // 1. UTF-8 with BOM.
        if (bytes.Length < 3 || bytes[0] != 0xEF || bytes[1] != 0xBB || bytes[2] != 0xBF)
        {
            violations.Add($"{rel}: missing UTF-8 BOM");
        }

        var text = Encoding.UTF8.GetString(bytes);

        // 2. CRLF line endings (no lone LF).
        var lineFeedCount = text.Count(c => c == '\n');
        var crlfCount = text.Split("\r\n", StringSplitOptions.None).Length - 1;
        if (lineFeedCount != crlfCount)
        {
            violations.Add($"{rel}: line endings are not all CRLF");
        }

        // 3. Trailing blank line: file must end with CRLF.
        if (!text.EndsWith("\r\n", StringComparison.Ordinal))
        {
            violations.Add($"{rel}: missing trailing newline");
        }

        // 4. No tab indentation.
        if (text.Split("\r\n").Any(line => line.StartsWith('\t')))
        {
            violations.Add($"{rel}: tab indentation found");
        }

        // 5. Using directives: before namespace, System first, alphabetical.
        CheckUsings(text, rel, violations);
    }

    private static void CheckUsings(string text, string rel, List<string> violations)
    {
        var lines = text.Split("\r\n");
        var namespaceLine = Array.FindIndex(lines, l => l.TrimStart().StartsWith("namespace ", StringComparison.Ordinal));

        var usings = new List<(int Line, string Name)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("using ", StringComparison.Ordinal) || !trimmed.EndsWith(';'))
            {
                continue;
            }

            var inner = trimmed["using ".Length..^1].Trim();
            if (inner.StartsWith("static ", StringComparison.Ordinal)
                || inner.StartsWith("global ", StringComparison.Ordinal)
                || inner.Contains('='))
            {
                continue; // static/global/alias usings are exempt from ordering
            }

            usings.Add((i, inner));
        }

        if (usings.Count == 0)
        {
            return;
        }

        if (namespaceLine >= 0 && usings.Any(u => u.Line > namespaceLine))
        {
            violations.Add($"{rel}: using directives must precede the namespace declaration");
            return;
        }

        // System usings first.
        var firstNonSystem = usings.FindIndex(u => !u.Name.StartsWith("System", StringComparison.Ordinal));
        var lastSystem = usings.FindLastIndex(u => u.Name.StartsWith("System", StringComparison.Ordinal));
        if (firstNonSystem >= 0 && lastSystem > firstNonSystem)
        {
            violations.Add($"{rel}: System usings must come before other namespaces");
            return;
        }

        // Alphabetical order (Ordinal) within the System and non-System groups.
        for (var i = 1; i < usings.Count; i++)
        {
            var sameGroup = usings[i - 1].Name.StartsWith("System", StringComparison.Ordinal)
                == usings[i].Name.StartsWith("System", StringComparison.Ordinal);
            if (sameGroup && string.CompareOrdinal(usings[i - 1].Name, usings[i].Name) > 0)
            {
                violations.Add($"{rel}: using '{usings[i - 1].Name}' out of order before '{usings[i].Name}'");
                return;
            }
        }
    }
}
