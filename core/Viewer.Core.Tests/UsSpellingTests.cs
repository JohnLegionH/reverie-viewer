// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.Json;
using System.Text.RegularExpressions;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The interface is written in US English: no string the viewer shows uses a British spelling. Reads the string table only.</summary>
public class UsSpellingTests
{
    private static readonly Regex British = new(
        @"\b\w*(colour|centre|centring|metre|licence|neighbour|behaviour|cancell(ed|ing)|travell(ed|ing|er)|favour|flavour|honour|armour|humour|catalogue|dialogue|labell(ed|ing)|modell(ed|ing)|judgement|defence|offence|practis(e|ed|es|ing)|programme|artefact|aluminium|whilst|amongst|learnt|spelt|minimis|maximis|organis|customis|recognis|initialis|synchronis|optimis|visualis|analys(e|ed|ing))\w*|\bgrey\w*",
        RegexOptions.IgnoreCase);

    private static IEnumerable<(string Key, string Text)> Table()
    {
        var assembly = typeof(StringTable).Assembly;
        string name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("en.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return doc.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).Select(p => (p.Name, p.Value.GetString()!)).ToList();
    }

    [Fact]
    public void The_refusal_of_a_bad_theme_file_uses_US_spelling()
    {
        var result = ThemePack.Parse("""{"format":1,"colors":{"panel":"nonsense"}}""");
        Assert.NotNull(result.Error);
        Assert.DoesNotMatch(British, result.Error!);
    }

    [Fact]
    public void No_interface_string_uses_a_British_spelling()
    {
        var table = Table().ToList();
        Assert.True(table.Count > 500, "the string table was not read");
        var found = table.Where(t => British.IsMatch(t.Text)).Select(t => t.Key + ": " + British.Match(t.Text).Value).ToList();
        Assert.True(found.Count == 0, found.Count + " strings: " + string.Join("; ", found));
    }
}
