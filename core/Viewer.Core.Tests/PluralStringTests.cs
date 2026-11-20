// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.Json;
using System.Text.RegularExpressions;
using Viewer.Shell;
using Viewer.Shell.Build;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Counts in the interface read as English needs them: "1 item", "2 items", "0 items". A count is passed to the string table as a
/// <see cref="Counted"/> argument, which picks the noun's ".one" entry for exactly 1 and its ".other" entry for every other count.
/// Pure and file reads; runs in parallel.
/// </summary>
public class PluralStringTests
{
    private static readonly StringTable S = StringTable.English();

    private static Counted C(string noun, double n) => new(noun, n);

    [Theory]
    [InlineData("count.items", 0, "0 items")]
    [InlineData("count.items", 1, "1 item")]
    [InlineData("count.items", 2, "2 items")]
    [InlineData("count.folders", 0, "0 folders")]
    [InlineData("count.folders", 1, "1 folder")]
    [InlineData("count.folders", 2, "2 folders")]
    [InlineData("count.matches", 1, "1 match")]
    [InlineData("count.matches", 2, "2 matches")]
    [InlineData("count.scripts", 1, "1 script")]
    [InlineData("count.scripts", 0, "0 scripts")]
    [InlineData("count.notices", 1, "1 notice")]
    [InlineData("count.notices", 3, "3 notices")]
    [InlineData("count.entries", 1, "1 entry")]
    [InlineData("count.entries", 2, "2 entries")]
    [InlineData("count.textures", 1, "1 texture")]
    [InlineData("count.textures", 0, "0 textures")]
    [InlineData("count.unread_notifications", 1, "1 unread notification")]
    [InlineData("count.unread_notifications", 5, "5 unread notifications")]
    [InlineData("count.seconds", 1, "1 second")]
    [InlineData("count.seconds", 2, "2 seconds")]
    public void A_counted_noun_takes_the_one_form_for_exactly_one_and_the_other_form_for_the_rest(string noun, int n, string expected) =>
        Assert.Equal(expected, S.Count(C(noun, n)));

    [Fact]
    public void A_fraction_is_not_one_and_shows_as_the_caller_wrote_it()
    {
        Assert.Equal("0.5 seconds", S.Count(new Counted("count.seconds", 0.5, "0.5")));
        Assert.Equal("1 second", S.Count(new Counted("count.seconds", 1, "1")));
    }

    [Theory]
    [InlineData(0, 0, "0 items and 0 folders")]
    [InlineData(1, 1, "1 item and 1 folder")]
    [InlineData(2, 1, "2 items and 1 folder")]
    [InlineData(1, 2, "1 item and 2 folders")]
    public void The_inventory_status_reads_in_the_right_form_for_each_count(int items, int folders, string counts)
    {
        Assert.Equal($"Fetching {counts}...", S.Get("inventory.status.fetching", C("count.items", items), C("count.folders", folders)));
        Assert.Equal($"{counts} so far", S.Get("inventory.status.partial", C("count.items", items), C("count.folders", folders)));
        Assert.Equal(counts, S.Get("inventory.status.complete", C("count.items", items), C("count.folders", folders)));
    }

    [Theory]
    [InlineData(0, "Searching... 0 folders still loading", "0 matches")]
    [InlineData(1, "Searching... 1 folder still loading", "1 match")]
    [InlineData(2, "Searching... 2 folders still loading", "2 matches")]
    public void The_inventory_search_line_reads_in_the_right_form(int n, string searching, string matches)
    {
        Assert.Equal(searching, S.Get("inventory.status.searching", C("count.folders", n)));
        Assert.Equal(matches, S.Get("inventory.status.matches", C("count.matches", n)));
    }

    [Theory]
    [InlineData(0, "0 items worn")]
    [InlineData(1, "1 item worn")]
    [InlineData(2, "2 items worn")]
    public void The_outfit_status_reads_in_the_right_form(int n, string expected) =>
        Assert.Equal(expected, S.Get("outfit.status.items", C("count.items", n)));

    [Theory]
    [InlineData(0, "Adding 0 items from the folder, one at a time.")]
    [InlineData(1, "Adding 1 item from the folder, one at a time.")]
    [InlineData(2, "Adding 2 items from the folder, one at a time.")]
    public void The_folder_drop_line_reads_in_the_right_form(int n, string expected) =>
        Assert.Equal(expected, S.Get("edit.content.folder_queued", C("count.items", n)));

    [Theory]
    [InlineData(0, "Cache cleared: 0 entries removed.")]
    [InlineData(1, "Cache cleared: 1 entry removed.")]
    [InlineData(2, "Cache cleared: 2 entries removed.")]
    public void The_cache_line_reads_in_the_right_form(int n, string expected) =>
        Assert.Equal(expected, S.Get("prefs.cache.cleared", C("count.entries", n)));

    [Theory]
    [InlineData(0, "0 textures")]
    [InlineData(1, "1 texture")]
    [InlineData(2, "2 textures")]
    public void The_appearance_texture_count_reads_in_the_right_form(int n, string expected) =>
        Assert.Equal(expected, S.Get("appearance.texture.count", C("count.textures", n)));

    [Theory]
    [InlineData(1, "1 notice")]
    [InlineData(2, "2 notices")]
    public void The_dock_notices_count_reads_in_the_right_form(int n, string expected) =>
        Assert.Equal(expected, S.Get("dock.notices.count", C("count.notices", n)));

    [Theory]
    [InlineData(1, "1 unread notification")]
    [InlineData(2, "2 unread notifications")]
    public void The_toolbar_unread_line_reads_in_the_right_form(int n, string expected) =>
        Assert.Equal(expected, S.Get("toolbar.unread", C("count.unread_notifications", n)));

    [Theory]
    [InlineData(1, 1, "Starting recompile of 1 item.", "1 script done, 1 with errors.")]
    [InlineData(2, 0, "Starting recompile of 2 items.", "2 scripts done, 0 with errors.")]
    [InlineData(0, 2, "Starting recompile of 0 items.", "0 scripts done, 2 with errors.")]
    public void The_compile_queue_lines_read_in_the_right_form(int n, int failed, string starting, string summary)
    {
        Assert.Equal(starting, ScriptQueue.Format(new ScriptQueueLine("queue.starting", "queue.start.compile", n.ToString()), S));
        Assert.Equal(summary, ScriptQueue.Format(new ScriptQueueLine("queue.summary", n.ToString(), failed.ToString()), S));
    }

    [Theory]
    [InlineData(1, "Wait: 1 second")]
    [InlineData(2, "Wait: 2 seconds")]
    [InlineData(0.5, "Wait: 0.5 seconds")]
    public void A_gesture_wait_step_reads_in_the_right_form(double seconds, string expected) =>
        Assert.Equal(expected, S.Get("gesture.step.wait_time", new Counted("count.seconds", seconds, seconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))));

    [Fact]
    public void The_Build_window_summary_says_one_item_for_one_in_the_contents()
    {
        // BuildLayout draws "{n} items" for a count that is not 0; 1 must read "1 item"
        string src = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Shell", "Build", "BuildLayout.cs"));
        Assert.Contains("new Counted(\"count.items\", c)", src);
    }

    [Fact]
    public void Every_noun_has_a_one_and_an_other_form_with_the_number_in_it_and_the_pseudo_table_does_not_bracket_twice()
    {
        var nouns = S.Keys.Where(k => k.StartsWith("count.", StringComparison.Ordinal) && k.EndsWith(".one", StringComparison.Ordinal))
            .Select(k => k[..^4]).ToList();
        Assert.True(nouns.Count >= 9);
        foreach (var noun in nouns)
        {
            Assert.True(S.Has(noun + ".other"), noun + ".other is missing");
            Assert.Contains("{0}", S.Get(noun + ".one"));
            Assert.Contains("{0}", S.Get(noun + ".other"));
            Assert.NotEqual(S.Get(noun + ".one"), S.Get(noun + ".other"));
        }
        // in the pseudo table a counted noun keeps its number, is accented, and is not bracketed again inside its sentence
        var pseudo = StringTable.Pseudo();
        string line = pseudo.Get("inventory.status.complete", C("count.items", 2), C("count.folders", 1));
        Assert.StartsWith("[", line);
        Assert.EndsWith("]", line);
        Assert.DoesNotContain("[[", line);
        Assert.DoesNotContain("]]", line);
        Assert.Contains("2 ", line);
    }

    // An invented rule with the shape of the Russian one: one for 1, few for 2 to 4, many for 5 and up, other for fractions.
    private static PluralCategory InventedRule(double n) =>
        n != Math.Floor(n) ? PluralCategory.Other : n == 1 ? PluralCategory.One : n is >= 2 and <= 4 ? PluralCategory.Few : PluralCategory.Many;

    private static StringTable Invented(params (string Key, string Text)[] entries) =>
        StringTable.Create(entries.ToDictionary(e => e.Key, e => e.Text), "xx", InventedRule);

    [Fact]
    public void A_language_with_a_few_form_picks_it_for_the_numbers_its_rule_names()
    {
        var table = Invented(("n.one", "{0} kot"), ("n.few", "{0} kota"), ("n.many", "{0} kotov"), ("n.other", "{0} kota?"));
        Assert.Equal("1 kot", table.Count(C("n", 1)));
        Assert.Equal("2 kota", table.Count(C("n", 2)));
        Assert.Equal("4 kota", table.Count(C("n", 4)));
        Assert.Equal("5 kotov", table.Count(C("n", 5)));
        Assert.Equal("0 kotov", table.Count(C("n", 0)));
        Assert.Equal("1.5 kota?", table.Count(new Counted("n", 1.5, "1.5")));
    }

    [Fact]
    public void A_missing_form_falls_back_to_the_other_form()
    {
        var table = Invented(("n.one", "{0} kot"), ("n.other", "{0} kotov"));
        Assert.Equal("1 kot", table.Count(C("n", 1)));
        Assert.Equal("3 kotov", table.Count(C("n", 3)));
        Assert.Equal("7 kotov", table.Count(C("n", 7)));
        // a string with only the other form serves every count, 1 included, and is never shown as a missing key
        var only = Invented(("m.other", "{0} kotov"));
        Assert.Equal("1 kotov", only.Count(C("m", 1)));
        Assert.Equal("3 kotov", only.Count(C("m", 3)));
    }

    [Fact]
    public void The_counted_noun_reaches_the_rule_through_a_sentence()
    {
        var table = Invented(("s", "Found {0}."), ("n.few", "{0} kota"), ("n.other", "{0} kotov"));
        Assert.Equal("Found 2 kota.", table.Get("s", C("n", 2)));
        Assert.Equal("Found 9 kotov.", table.Get("s", C("n", 9)));
    }

    [Fact]
    public void English_picks_one_for_exactly_one_and_other_for_the_rest_and_is_the_default_rule()
    {
        Assert.Equal(PluralCategory.One, PluralRules.English(1));
        Assert.Equal(PluralCategory.Other, PluralRules.English(0));
        Assert.Equal(PluralCategory.Other, PluralRules.English(2));
        Assert.Equal(PluralCategory.Other, PluralRules.English(1.5));
        Assert.Same(PluralRules.English, PluralRules.For("en"));
        Assert.Same(PluralRules.English, PluralRules.For("en-XA"));
        Assert.Same(PluralRules.English, PluralRules.For("xx"));
        // a table made without a rule counts as English does
        var plain = StringTable.Create(new Dictionary<string, string> { ["n.one"] = "{0} cat", ["n.other"] = "{0} cats" }, "en");
        Assert.Equal("1 cat", plain.Count(C("n", 1)));
        Assert.Equal("2 cats", plain.Count(C("n", 2)));
    }

    // The strings that put a number before a noun (en.json, read as text). A string that is in this list may name a count that is
    // never 1 (the caller only builds it for 2 or more, or the caller already picks a separate "one" string); any other string with a
    // count before a plural noun must be built from a counted noun, so it cannot say "1 items".
    private static readonly HashSet<string> NeverOne = new(StringComparer.Ordinal)
    {
        "chat.typing.many",                  // three or more are typing: "{1} others" is 2 or more
        "edit.selected",                     // shown only for 2 or more objects
        "upload.notice.many.textures", "upload.notice.many.objects",   // the notice for one upload has its own string
        "edit.summary.faces", "edit.card.prims", "edit.card.faces",   // the callers use the "_one" strings for 1
        "texture.preview.size",              // "{0}" is "width x height"
        "notecard.count",                    // "{1}" is the fixed byte limit
        "appearance.group.more",             // "{0}" is a group's name, not a count
    };

    [Fact]
    public void No_other_string_puts_a_count_before_a_plural_noun_without_a_counted_noun()
    {
        string json = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Shell", "Strings", "en.json"));
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
        var countBeforeNoun = new Regex(@"\{\d\}\s+(?:[a-z]+\s+)?(items?|folders?|objects?|files?|faces?|prims?|matches|match|entries|entry|scripts?|bytes?|seconds?|minutes?|textures?|notices?|notifications?|settings|pixels?)\b");
        var offenders = map.Where(kv => countBeforeNoun.IsMatch(kv.Value) && !kv.Key.StartsWith("count.", StringComparison.Ordinal) && !NeverOne.Contains(kv.Key))
            .Select(kv => kv.Key + " = " + kv.Value).ToList();
        Assert.True(offenders.Count == 0, "counts written before a plural noun: " + string.Join("; ", offenders));
    }
}
