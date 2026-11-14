// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The repository carries its licence and every notice it needs: the LGPL 2.1 text at the top, an SPDX line in every
/// source file, and each ported file's upstream notice word for word with the licence that notice gives. The ported
/// files, their notices and their licences are listed in <c>ThirdPartyLicenses/PORTED-FILES.tsv</c> (one row per
/// notice: path, SPDX expression, a line of the upstream notice); a file whose header names a copyright holder other
/// than this project must be listed there.
/// </summary>
public class LicenceNoticeTests
{
    private const string Manifest = "ThirdPartyLicenses/PORTED-FILES.tsv";
    private const string OwnLicence = "LGPL-2.1-or-later";
    private static readonly string[] SourceExtensions = [".cs", ".ps1", ".sh", ".js", ".mjs"];
    private static readonly Regex Spdx = new(@"SPDX-License-Identifier:\s*(?<expr>[A-Za-z0-9.+\- ()]+?)\s*(?:\*/)?\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
    /// <summary>The project's own credit line, any year.</summary>
    private static readonly Regex OwnCredit = new(Regex.Escape("Copyright (c) 2026 Legion Builds").Replace("2026", @"\d{4}"), RegexOptions.Compiled);
    private static readonly Regex HolderLine = new(@"Copyright\s+\((?:c|C)\)\s+(?<rest>.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    /// <summary>The file's leading comment block: every line from the top that is blank, a line comment (// or #), part of
    /// a block comment (/* ... */ or &lt;# ... #&gt;) or a shebang, up to the first line of code.</summary>
    private static string Head(string text)
    {
        var kept = new List<string>();
        bool inBlock = false;
        string? close = null;
        foreach (var raw in text.TrimStart('﻿').Replace("\r", "").Split('\n'))
        {
            var l = raw.Trim();
            if (inBlock)
            {
                kept.Add(raw);
                if (l.Contains(close!, StringComparison.Ordinal)) inBlock = false;
                continue;
            }
            if (l.Length == 0 || l.StartsWith("//", StringComparison.Ordinal) || l.StartsWith("#!", StringComparison.Ordinal)
                || (l.StartsWith('#') && !l.StartsWith("#>", StringComparison.Ordinal)))
            {
                kept.Add(raw);
                continue;
            }
            if (l.StartsWith("/*", StringComparison.Ordinal) || l.StartsWith("<#", StringComparison.Ordinal))
            {
                close = l.StartsWith("/*", StringComparison.Ordinal) ? "*/" : "#>";
                kept.Add(raw);
                inBlock = !l[2..].Contains(close, StringComparison.Ordinal);
                continue;
            }
            break;
        }
        return string.Join("\n", kept);
    }

    private static List<string> TrackedSources()
    {
        var (exit, listing) = ChildProcess.Run("git", ["-C", TestPaths.RepoRoot, "ls-files"], TimeSpan.FromSeconds(30));
        Assert.Equal(0, exit);
        var files = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(f => SourceExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Where(f => File.Exists(Path.Combine(TestPaths.RepoRoot, f)))
            .Select(f => f.Replace('\\', '/'))
            .ToList();
        Assert.True(files.Count > 1000, $"only {files.Count} tracked source files listed");
        return files;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, relative), Encoding.UTF8);

    /// <summary>The manifest's rows; an empty list (and a failed assertion in the caller) when it is absent.</summary>
    private static List<(string Path, string Spdx, string Notice)> ManifestRows()
    {
        var full = Path.Combine(TestPaths.RepoRoot, Manifest);
        if (!File.Exists(full))
            return [];
        return File.ReadAllLines(full, Encoding.UTF8)
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Skip(1)   // the column names
            .Select(l => l.Split('\t'))
            .Where(c => c.Length == 3)
            .Select(c => (c[0], c[1], c[2]))
            .ToList();
    }

    [Fact]
    public void The_licence_file_is_the_LGPL_2_1_text()
    {
        var path = Path.Combine(TestPaths.RepoRoot, "LICENSE");
        Assert.True(File.Exists(path), "no LICENSE file at the top of the repository");
        var text = File.ReadAllText(path);
        Assert.Contains("GNU LESSER GENERAL PUBLIC LICENSE", text);
        Assert.Contains("Version 2.1, February 1999", text);
        Assert.Contains("END OF TERMS AND CONDITIONS", text);
    }

    [Fact]
    public void Every_source_file_carries_an_SPDX_line()
    {
        var missing = TrackedSources().Where(f => !Spdx.IsMatch(Head(Read(f)))).ToList();
        Assert.True(missing.Count == 0, $"{missing.Count} source files without an SPDX-License-Identifier line (first 40):\n" + string.Join("\n", missing.Take(40)));
    }

    [Fact]
    public void The_manifest_lists_ported_files()
    {
        Assert.True(File.Exists(Path.Combine(TestPaths.RepoRoot, Manifest)), $"no {Manifest}");
        Assert.True(ManifestRows().Count > 50, $"{Manifest} lists {ManifestRows().Count} notices");
    }

    [Fact]
    public void Ported_files_keep_their_upstream_notice_and_its_licence()
    {
        var rows = ManifestRows();
        Assert.NotEmpty(rows);
        var problems = new List<string>();
        foreach (var (path, spdx, notice) in rows)
        {
            var full = Path.Combine(TestPaths.RepoRoot, path);
            if (!File.Exists(full)) { problems.Add($"{path}: listed but missing"); continue; }
            var head = Head(File.ReadAllText(full, Encoding.UTF8));
            if (!head.Contains(notice, StringComparison.Ordinal)) problems.Add($"{path}: upstream notice line not found: {notice}");
            var m = Spdx.Match(head);
            if (!m.Success) problems.Add($"{path}: no SPDX line");
            else if (m.Groups["expr"].Value.Trim() != spdx) problems.Add($"{path}: SPDX is '{m.Groups["expr"].Value.Trim()}', the notice gives '{spdx}'");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    // the sample add-on is a template to copy: 0BSD, with its own licence file, so a copy carries no conditions
    private const string SampleFolder = "samples/HelloAddon/", SampleLicence = "0BSD";

    [Fact]
    public void The_sample_add_on_is_0BSD_with_its_licence_text_beside_it()
    {
        var sources = TrackedSources().Where(f => f.StartsWith(SampleFolder, StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(sources);
        foreach (var f in sources)
            Assert.Equal(SampleLicence, Spdx.Match(Head(Read(f))).Groups["expr"].Value.Trim());
        string licence = Read(SampleFolder + "LICENSE");
        Assert.Contains("Permission to use, copy, modify, and/or distribute this software for any", licence, StringComparison.Ordinal);
        Assert.Contains("purpose with or without fee is hereby granted.", licence, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_file_naming_another_holder_is_listed_and_every_other_file_is_LGPL_2_1_or_later()
    {
        var listed = ManifestRows().Select(r => r.Path).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var f in TrackedSources())
        {
            var head = Head(Read(f));
            bool other = HolderLine.Matches(head).Any(m => !OwnCredit.IsMatch(m.Value));
            if (other && !listed.Contains(f)) { problems.Add($"{f}: names another copyright holder but is not in {Manifest}"); continue; }
            if (listed.Contains(f)) continue;
            var m = Spdx.Match(head);
            string expected = f.StartsWith(SampleFolder, StringComparison.Ordinal) ? SampleLicence : OwnLicence;
            if (m.Success && m.Groups["expr"].Value.Trim() != expected) problems.Add($"{f}: own file with SPDX '{m.Groups["expr"].Value.Trim()}', not {expected}");
        }
        Assert.True(problems.Count == 0, $"{problems.Count} problems (first 40):\n" + string.Join("\n", problems.Take(40)));
    }

    [Theory]
    [InlineData("Godot Engine")]
    [InlineData("LibreMetaverse")]
    [InlineData("CoreJ2K")]
    [InlineData("JoltPhysicsSharp")]
    [InlineData("joltc")]
    [InlineData("Jolt Physics")]
    [InlineData("Khronos")]
    [InlineData("Second Life viewer")]
    [InlineData("Firestorm")]
    [InlineData("Avatar data folder")]
    [InlineData("Creative Commons Attribution-Share Alike 3.0")]
    public void The_third_party_notices_name_each_component(string component)
    {
        var text = Read("THIRD-PARTY-NOTICES.md");
        Assert.Contains(component, text, StringComparison.Ordinal);
    }

    /// <summary>The bundled packages that ship no licence file of their own: the notices carry each project's licence text in full,
    /// under its own heading, with the copyright line the project's licence file gives (the text is checked by its first and last words).</summary>
    [Theory]
    [InlineData("MessagePack and MessagePack.Annotations", "Copyright (c) 2017 Yoshifumi Kawai and contributors", "Permission is hereby granted", "limitations under the License.")]
    [InlineData("ZLogger", "Copyright (c) 2020 Cysharp, Inc.", "Permission is hereby granted", "OTHER DEALINGS IN THE\nSOFTWARE.")]
    [InlineData("Utf8StringInterpolation", "Copyright (c) 2023 Cysharp, Inc.", "Permission is hereby granted", "OTHER DEALINGS IN THE\nSOFTWARE.")]
    [InlineData("OggVorbisEncoder", "Copyright (c) 2016 Steve Lillis", "Permission is hereby granted", "OTHER DEALINGS IN THE\nSOFTWARE.")]
    [InlineData("Pfim", "MIT License", "Permission is hereby granted", "SOFTWARE.")]
    [InlineData("zlib.net-mutliplatform", "Copyright (c) 2006-2007, ComponentAce", "Redistribution and use in source and binary forms", "POSSIBILITY OF SUCH DAMAGE.")]
    public void A_package_without_a_licence_file_has_its_licence_text_in_the_notices(string heading, string copyright, string first, string last)
    {
        var text = Read("THIRD-PARTY-NOTICES.md").Replace("\r\n", "\n");
        int at = text.IndexOf("\n#### " + heading + "\n", StringComparison.Ordinal);
        Assert.True(at >= 0, $"no licence text heading for {heading}");
        int open = text.IndexOf("\n```\n", at, StringComparison.Ordinal);
        int close = open < 0 ? -1 : text.IndexOf("\n```\n", open + 5, StringComparison.Ordinal);
        Assert.True(open >= 0 && close > open, $"no licence text block for {heading}");
        string block = text[(open + 5)..close];
        Assert.Contains(copyright, block, StringComparison.Ordinal);
        Assert.Contains(first, block, StringComparison.Ordinal);
        Assert.EndsWith(last, block, StringComparison.Ordinal);
    }

    [Fact]
    public void The_readme_has_a_licence_section()
    {
        var path = Path.Combine(TestPaths.RepoRoot, "README.md");
        Assert.True(File.Exists(path), "no README.md at the top of the repository");
        var text = File.ReadAllText(path);
        Assert.Contains("## Licence", text);
        Assert.Contains("LGPL-2.1-or-later", text);
        Assert.Contains("THIRD-PARTY-NOTICES.md", text);
    }
}
