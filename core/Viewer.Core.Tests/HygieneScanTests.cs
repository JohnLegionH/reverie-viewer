// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Tracked files are written for the people who will read the code: comments, names, scripts, fixtures and docs say what
/// the code does and why, without internal tracking references. Two halves:
///   - generic shapes, kept here: internal session ids, tracker and decision ids (one capital letter, a hyphen, digits),
///     internal rule references, private IPv4 addresses, Windows drive paths, ISO dates in comments and docs, and
///     references to the assistant or its work orders;
///   - specific words (names of people, places and hosts that must not appear), read from an untracked list file named by
///     <see cref="WordsVariable"/>; that half is a real skip when the variable is unset or the file is absent.
/// File paths are scanned as well as contents. The allowed texts are the author credit and the adaptation line (<see cref="Credit"/>).
/// </summary>
public class HygieneScanTests
{
    /// <summary>The environment variable naming the specific-words list (absolute path).</summary>
    public const string WordsVariable = "NV_HYGIENE_WORDS";

    /// <summary>The project's author credit, with or without the year, and the line saying a file is adapted from
    /// another source, and the credit of the interface sounds made with ChatGPT (<c>UiSoundRecordings.ChatGptCredit</c>):
    /// allowed wherever they appear.</summary>
    private const string Credit = "Copyright (c) 2026 Legion Builds";
    private static readonly Regex CreditPattern = new(
        @"Copyright \(c\) (?:20\d\d )?Legion Builds|Adapted 20\d\d by Legion Builds|made with ChatGPT for Reverie by Legion Builds", RegexOptions.Compiled);

    private static readonly (string Kind, Regex Pattern)[] Generic =
    [
        ("session id", new(@"\b(?:NV|PHLOX|CORE|RP|JOLT)-\d+[a-z0-9]*\b|(?i:\bnv\d+[a-z]+\d*[a-z]?\b)|\bpart \d+[a-z]+\b", RegexOptions.Compiled)),
        ("tracker or decision id", new(@"(?<![\w-])[NFDQ]-\d+\b", RegexOptions.Compiled)),
        ("rule reference", new(@"WORKING-RULES|(?<![\w.])[A-O]\.\d{1,2}(?![\w.])", RegexOptions.Compiled)),
        ("private IPv4 address", new(@"(?<![\d.])(?:10\.\d{1,3}\.\d{1,3}\.\d{1,3}|192\.168\.\d{1,3}\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3})(?![\d.]*\d)", RegexOptions.Compiled)),
        ("Windows drive path", new(@"(?<![A-Za-z0-9])[A-Za-z]:[\\/][A-Za-z_]", RegexOptions.Compiled)),
        ("assistant or work order", new(@"\bClaude\b|\bCC\b(?![- ]BY\b)|(?i:\bwork orders?\b)", RegexOptions.Compiled)),
    ];

    private static readonly Regex IsoDate = new(@"\b20\d\d-[01]\d-[0-3]\d\b", RegexOptions.Compiled);

    /// <summary>Files that define or test the private address ranges they refuse (addresses only).</summary>
    private static readonly string[] PrivateRangeFiles =
        ["core/Viewer.Core/Net/OutboundPolicy.cs", "core/Viewer.Core.Tests/OutboundPolicyTests.cs", "core/Viewer.Core.Tests/TrackedContentScanTests.cs"];

    /// <summary>Files that test Windows path handling with synthetic paths (drive paths only).</summary>
    private static readonly string[] WindowsPathFiles =
        ["core/Viewer.Core.Tests/DataDirectoriesTests.cs", "core/Viewer.Core.Tests/OutboundPolicyTests.cs"];

    /// <summary>This file: its patterns and negative controls are the shapes it looks for.</summary>
    private const string Self = "core/Viewer.Core.Tests/HygieneScanTests.cs";

    private static readonly string[] BinaryExtensions = [".png", ".gz", ".llmesh", ".bin", ".j2c", ".j2k", ".dll", ".pck", ".exe", ".ico", ".ttf", ".otf"];
    private static readonly string[] DataExtensions = [".json", ".jsonl", ".csv", ".txt"];
    private static readonly string[] HashCommentExtensions = [".ps1", ".sh", ".py", ".gitignore", ".gitattributes", ".editorconfig", ".toml", ".yml", ".yaml"];

    /// <summary>The comment part of a line, where dates are looked for; the whole line in docs and markup; none in data.</summary>
    internal static string CommentPart(string path, string line)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".md" or ".csproj" or ".props" or ".targets" or ".xml" or ".sln")
            return line;
        if (DataExtensions.Contains(ext))
            return "";
        if (HashCommentExtensions.Contains(ext) || Path.GetFileName(path).StartsWith('.'))
        {
            int h = line.IndexOf('#');
            return h < 0 ? "" : line[h..];
        }
        if (ext is ".godot" or ".cfg" or ".import" or ".ini")
        {
            int s = line.IndexOf(';');
            return s < 0 ? "" : line[s..];
        }
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith('*') || trimmed.StartsWith("/*"))
            return line;
        for (int i = line.IndexOf("//", StringComparison.Ordinal); i >= 0; i = line.IndexOf("//", i + 2, StringComparison.Ordinal))
            if (i == 0 || line[i - 1] != ':')
                return line[i..];
        return "";
    }

    internal static List<string> ScanGeneric(string path, string text)
    {
        var found = new List<string>();
        foreach (var (kind, pattern) in Generic)
            if (kind != "private IPv4 address" && pattern.IsMatch(path))
                found.Add($"{path}: {kind} in the file's path");
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = CreditPattern.Replace(lines[i], "");
            foreach (var (kind, pattern) in Generic)
            {
                if (kind == "private IPv4 address" && PrivateRangeFiles.Contains(path))
                    continue;
                if (kind == "Windows drive path" && WindowsPathFiles.Contains(path))
                    continue;
                foreach (Match m in pattern.Matches(line))
                    found.Add($"{path}:{i + 1} {kind}: {m.Value}");
            }
            foreach (Match m in IsoDate.Matches(CommentPart(path, line)))
                found.Add($"{path}:{i + 1} date in a comment: {m.Value}");
        }
        return found;
    }

    /// <summary>The list file: one entry per line, matched case-insensitively at word boundaries; an entry ending in
    /// <c>*</c> matches as a prefix; <c>allow: text</c> lines name texts removed before matching; <c>#</c> lines are notes.</summary>
    internal static (Regex? Words, string[] Allowed) ParseWords(IEnumerable<string> lines)
    {
        var words = new List<string>();
        var allowed = new List<string>();
        foreach (var raw in lines)
        {
            var l = raw.Trim();
            if (l.Length == 0 || l.StartsWith('#')) continue;
            if (l.StartsWith("allow:", StringComparison.OrdinalIgnoreCase)) { allowed.Add(l["allow:".Length..].Trim()); continue; }
            words.Add(l.EndsWith('*') ? @"\b" + Regex.Escape(l[..^1]) : @"\b" + Regex.Escape(l) + @"\b");
        }
        return (words.Count == 0 ? null : new Regex(string.Join("|", words), RegexOptions.Compiled | RegexOptions.IgnoreCase), allowed.ToArray());
    }

    internal static List<string> ScanWords(string path, string text, Regex words, string[] allowed)
    {
        var found = new List<string>();
        if (words.IsMatch(path))
            found.Add($"{path}: a listed word in the file's path");
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = CreditPattern.Replace(lines[i], "");
            foreach (var a in allowed)
                line = line.Replace(a, "");
            foreach (Match m in words.Matches(line))
                found.Add($"{path}:{i + 1} listed word ({m.Value.Length} chars)");
        }
        return found;
    }

    private static List<(string Path, string Text)> TrackedTextFiles()
    {
        var (exit, listing) = ChildProcess.Run("git", ["-C", TestPaths.RepoRoot, "ls-files"], TimeSpan.FromSeconds(30));
        Assert.Equal(0, exit);
        var files = new List<(string, string)>();
        foreach (var f in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (f == Self || BinaryExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
            var full = Path.Combine(TestPaths.RepoRoot, f);
            if (!File.Exists(full)) continue;
            var bytes = File.ReadAllBytes(full);
            if (Array.IndexOf(bytes, (byte)0) >= 0) continue;   // binary by content
            files.Add((f.Replace('\\', '/'), Encoding.UTF8.GetString(bytes)));
        }
        Assert.True(files.Count > 100, $"only {files.Count} tracked text files listed");
        return files;
    }

    private static void AssertClean(List<string> findings)
    {
        const int shown = 200;
        Assert.True(findings.Count == 0,
            $"{findings.Count} findings" + (findings.Count > shown ? $" (first {shown})" : "") + ":\n" + string.Join("\n", findings.Take(shown)));
    }

    [Fact]
    public void Tracked_files_carry_no_internal_ids_private_addresses_local_paths_or_dated_comments()
    {
        var findings = new List<string>();
        foreach (var (path, text) in TrackedTextFiles())
            findings.AddRange(ScanGeneric(path, text));
        AssertClean(findings);
    }

    [HygieneWordsFact]
    public void Tracked_files_carry_no_listed_word()
    {
        var (words, allowed) = ParseWords(File.ReadAllLines(Environment.GetEnvironmentVariable(WordsVariable)!));
        Assert.True(words is not null, "the list file names no words");
        var findings = new List<string>();
        foreach (var (path, text) in TrackedTextFiles())
            findings.AddRange(ScanWords(path, text, words!, allowed));
        AssertClean(findings);
    }

    [Fact]
    public void Negative_control_each_shape_is_flagged_and_plain_text_is_not()
    {
        Assert.NotEmpty(ScanGeneric("x.cs", "// " + "NV-" + "8c part 3e: a note"));
        Assert.NotEmpty(ScanGeneric("x.cs", "// see " + "N-" + "57"));
        Assert.NotEmpty(ScanGeneric("x.cs", "// a finding, " + "F-" + "301"));
        Assert.NotEmpty(ScanGeneric("x.ps1", "# rule " + "B." + "16"));
        Assert.NotEmpty(ScanGeneric("x.cs", "var host = \"192." + "168.1.20\";"));
        Assert.NotEmpty(ScanGeneric("x.ps1", "$p = \"" + "D:" + "\\work\\x\""));
        Assert.NotEmpty(ScanGeneric("x.cs", "// measured on 20" + "26-01-15"));
        Assert.NotEmpty(ScanGeneric("x.md", "Status: v1, 20" + "26-01-15"));
        Assert.NotEmpty(ScanGeneric("x.cs", "// written by " + "Clau" + "de"));
        Assert.NotEmpty(ScanGeneric("core/" + "NV4b" + "Tests.cs", ""));
        Assert.Empty(ScanGeneric("x.cs", "// " + Credit));
        Assert.Empty(ScanGeneric("x.cs", "// Copyright (c) Legion Builds"));
        Assert.Empty(ScanGeneric("x.cs", "var stamp = \"2026-01-15T10:00:00Z\";   // a parsed log line"));
        Assert.Empty(ScanGeneric("x.json", "{\"at\": \"2026-01-15\"}"));
        Assert.Empty(ScanGeneric("x.cs", "// UTF-8, SHA-256, ADR-011, version 1.2.3, https://example.org/a"));
        Assert.Empty(ScanGeneric("x.cs", "var ok = 10.5 + x.Y;"));
        Assert.Empty(ScanGeneric("x.md", "licensed CC-BY-4.0"));
        Assert.Empty(ScanGeneric("x.cs", "// CC BY 3.0 (Holger Dammertz)"));   // a licence name written with a space, as the credit in the official shader is
        Assert.NotEmpty(ScanGeneric("x.cs", "// the CC will decide"));

        var (words, allowed) = ParseWords(["# names", "Somebody", "Placename*", "allow: Somebody Corp"]);
        Assert.NotNull(words);
        Assert.NotEmpty(ScanWords("x.cs", "// asked by somebody", words!, allowed));
        Assert.NotEmpty(ScanWords("x.cs", "// the PlacenameNorth region", words!, allowed));
        Assert.NotEmpty(ScanWords("somebody.cs", "", words!, allowed));
        Assert.Empty(ScanWords("x.cs", "// " + Credit + ", (c) Somebody Corp", words!, allowed));
        Assert.Empty(ScanWords("x.cs", "// nobody here", words!, allowed));
        // the sounds' credit is allowed as a whole; the name alone elsewhere is still found
        var (credit, none) = ParseWords(["Legion*"]);
        Assert.Empty(ScanWords("x.md", "- `ui-x.wav`: made with ChatGPT for Reverie by Legion Builds.", credit!, none));
        Assert.NotEmpty(ScanWords("x.md", "- made by Legion Builds on its own grid", credit!, none));
    }
}

/// <summary>A real skip, with its reason, when the specific-words list is not configured or absent.</summary>
public sealed class HygieneWordsFactAttribute : FactAttribute
{
    public HygieneWordsFactAttribute()
    {
        var path = Environment.GetEnvironmentVariable(HygieneScanTests.WordsVariable);
        if (string.IsNullOrWhiteSpace(path))
            Skip = $"{HygieneScanTests.WordsVariable} is unset (the specific-words list lives outside the repository)";
        else if (!File.Exists(path))
            Skip = $"{HygieneScanTests.WordsVariable} names a file that is absent";
    }
}
