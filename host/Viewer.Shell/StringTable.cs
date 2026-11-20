// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Viewer.Shell;

/// <summary>
/// A number with the noun it counts, passed as an argument to <see cref="StringTable.Get"/>: it is written as the count and the noun
/// in the form the current language needs ("1 item", "2 items", "0 items" in English). <paramref name="Noun"/> is a table key with
/// one entry per plural category the language uses (".zero", ".one", ".two", ".few", ".many", ".other"), each holding "{0}" for the
/// number; ".other" is required and is the form used when the category's own entry is missing. English has ".one" and ".other".
/// <paramref name="Shown"/> replaces the number's own text (for a fraction such as 0.5).
/// </summary>
public readonly record struct Counted(string Noun, double N, string? Shown = null);

/// <summary>The plural categories of the Unicode CLDR plural rules (the language plural rules in the CLDR specification).</summary>
public enum PluralCategory { Zero, One, Two, Few, Many, Other }

/// <summary>A language's plural rule: the category a number falls in.</summary>
public delegate PluralCategory PluralRule(double n);

/// <summary>The plural rules of the languages the viewer ships a string table for. A language with no entry here uses English's.</summary>
public static class PluralRules
{
    /// <summary>English (CLDR): "one" for exactly 1, "other" for every other number, 0 and fractions included.</summary>
    public static readonly PluralRule English = n => n == 1 ? PluralCategory.One : PluralCategory.Other;

    /// <summary>The rule for a locale id (a table id such as "en", or the pseudo table's "en-XA").</summary>
    public static PluralRule For(string locale) => English;
}

/// <summary>
/// The string table (ADR-020): every user-facing text comes from here by key, never from a literal in the host. The
/// English table is embedded in this assembly. <see cref="Pseudo"/> is the pseudo-localised table used to catch
/// truncation: every string about 40% longer, accented, and bracketed so a clipped end is visible.
/// </summary>
public sealed class StringTable
{
    private readonly IReadOnlyDictionary<string, string> _strings;

    private readonly PluralRule _plural;

    private StringTable(IReadOnlyDictionary<string, string> strings, string locale, PluralRule? plural = null)
    {
        _strings = strings;
        Locale = locale;
        _plural = plural ?? PluralRules.For(locale);
    }

    /// <summary>A table over the given strings, counting by <paramref name="plural"/> (the locale's own rule when null).</summary>
    public static StringTable Create(IReadOnlyDictionary<string, string> strings, string locale, PluralRule? plural = null) =>
        new(strings, locale, plural);

    public string Locale { get; }

    public IEnumerable<string> Keys => _strings.Keys;

    /// <summary>The embedded English table.</summary>
    public static StringTable English() => Load("en");

    /// <summary>The ids of the string tables a person may choose between.</summary>
    public static IReadOnlyList<string> Available { get; } = ["en"];

    /// <summary>The table for a saved language choice (default, a table id, anything else or nothing): English when no other table fits.</summary>
    public static StringTable ForSetting(string? choice) => choice is not null && Available.Contains(choice) ? Load(choice) : English();

    /// <summary>The English table pseudo-localised ("en-XA" by the usual convention).</summary>
    public static StringTable Pseudo()
    {
        var en = English();
        return new StringTable(en._strings.ToDictionary(kv => kv.Key, kv => Pseudolocalize(kv.Value)), "en-XA");
    }

    public static StringTable Load(string locale)
    {
        var asm = typeof(StringTable).Assembly;
        using var stream = asm.GetManifestResourceStream($"Viewer.Shell.Strings.{locale}.json")
            ?? throw new InvalidOperationException("no string table for " + locale);
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
        return new StringTable(map, locale);
    }

    public bool Has(string key) => _strings.ContainsKey(key) || _added.ContainsKey(key);

    // add-ons' own texts (menu entries, window titles): kept apart from the table, never formatted with arguments
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _added = new(StringComparer.Ordinal);

    /// <summary>Adds an add-on's own text under a key that starts with "addon.", so a menu entry or a window title made by an add-on is
    /// drawn like the viewer's own. Never replaces the viewer's text: false for any other key.</summary>
    public bool Define(string key, string text)
    {
        if (!key.StartsWith("addon.", StringComparison.Ordinal) || _strings.ContainsKey(key))
            return false;
        _added[key] = text;
        return true;
    }

    /// <summary>A count with its noun in the form English needs ("1 item", "2 items").</summary>
    public string Count(Counted counted)
    {
        string shown = counted.Shown ?? counted.N.ToString("0.##", CultureInfo.InvariantCulture);
        // the form the language's rule names for this number; a string without that form takes its ".other" form
        string form = _plural(counted.N) switch
        {
            PluralCategory.Zero => "zero",
            PluralCategory.One => "one",
            PluralCategory.Two => "two",
            PluralCategory.Few => "few",
            PluralCategory.Many => "many",
            _ => "other",
        };
        string key = counted.Noun + "." + form;
        if (!Has(key))
            key = counted.Noun + ".other";
        string text = Get(key, shown);
        // in the pseudo table the sentence that holds the count is bracketed: the count is not bracketed again inside it
        return Locale == "en-XA" && text.Length >= 2 && text[0] == '[' && text[^1] == ']' ? text[1..^1] : text;
    }

    /// <summary>The text for <paramref name="key"/>, formatted with <paramref name="args"/>. A missing key is shown as
    /// "[[key]]" so it is visible on screen and caught by tests rather than silently blank.</summary>
    public string Get(string key, params object?[] args)
    {
        if (_added.TryGetValue(key, out var own))
            return own;
        if (!_strings.TryGetValue(key, out var text))
            return "[[" + key + "]]";
        if (args.Length == 0)
            return text;
        // a count is written with its noun in the form English needs
        var shown = args.Select(a => a is Counted c ? Count(c) : a).ToArray();
        return string.Format(CultureInfo.InvariantCulture, text, shown);
    }

    private const string Plain = "aceinorsuyACEINORSUY";
    private const string Accented = "áçéîñöřšüýÁÇÉÎÑÖŘŠÜÝ";

    /// <summary>Accents letters, keeps {n} placeholders intact, pads to about 140% length and brackets the result.</summary>
    public static string Pseudolocalize(string text)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '{')
            {
                int end = text.IndexOf('}', i);
                if (end > i)
                {
                    sb.Append(text, i, end - i + 1);
                    i = end;
                    continue;
                }
            }
            int k = Plain.IndexOf(c);
            sb.Append(k >= 0 ? Accented[k] : c);
        }
        int pad = (int)Math.Ceiling(text.Length * 0.4);
        for (int i = 0; i < pad; i++)
            sb.Append(i % 2 == 0 ? '~' : '·');
        return sb.Append(']').ToString();
    }
}
