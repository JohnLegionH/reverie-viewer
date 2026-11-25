// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text;

namespace Viewer.Shell.Chat;

// Behaviour follows the official viewer's chat input, written in our own words (no code copied): line recall as
// llchatentry.cpp has it, growth to five lines (floater_im_session.xml, expand_lines_count), and gesture completion for a
// line that starts with "/" (llfloaterimnearbychat.cpp onChatBoxKeystroke, llgesturemgr.cpp matchPrefix).

/// <summary>The lines a chat input has sent, for recall: <see cref="Previous"/> walks back, <see cref="Next"/> comes forward and
/// ends at the text that was being typed. A line that repeats the one before it is kept once. Main thread only.</summary>
public sealed class ChatLineHistory
{
    private const int Most = 200;
    private readonly List<string> _lines = new();
    private int _at;
    private string _unsent = "";

    /// <summary>Keeps a sent line and starts recall again from the newest.</summary>
    public void Remember(string line)
    {
        if (!string.IsNullOrWhiteSpace(line) && (_lines.Count == 0 || _lines[^1] != line))
        {
            _lines.Add(line);
            if (_lines.Count > Most)
                _lines.RemoveAt(0);
        }
        _at = _lines.Count;
        _unsent = "";
    }

    /// <summary>The line before the one shown (<paramref name="current"/> is kept when recall starts); null at the oldest.</summary>
    public string? Previous(string current)
    {
        if (_at == 0)
            return null;
        if (_at == _lines.Count)
            _unsent = current;
        return _lines[--_at];
    }

    /// <summary>The line after the one shown, then the unsent text; null when not recalling.</summary>
    public string? Next(string current)
    {
        if (_at >= _lines.Count)
            return null;
        _at++;
        return _at == _lines.Count ? _unsent : _lines[_at];
    }

    public void Clear()
    {
        _lines.Clear();
        _at = 0;
        _unsent = "";
    }
}

/// <summary>The chat box's text and size rules. The box shows one logical line that wraps.</summary>
public static class ChatBoxRules
{
    /// <summary>The most characters the box holds.</summary>
    public const int MaxCharacters = 1023;

    /// <summary>The box grows to this many wrapped lines, then scrolls.</summary>
    public const int MaxVisibleLines = 5;

    public static int VisibleLines(int wrappedLines) => Math.Clamp(wrappedLines, 1, MaxVisibleLines);

    /// <summary>
    /// The text the box keeps after a change from <paramref name="before"/> to <paramref name="after"/>: line breaks and other control
    /// characters are left out (a say holds none), and text past the limit is cut from the end of what was put in, so what was there
    /// stays. <paramref name="caret"/> and the caret returned count characters (code points), as the engine does.
    /// </summary>
    public static (string Text, int Caret) Clean(string before, string after, int caret)
    {
        var kept = new List<Rune>();
        int at = 0, seen = 0;
        foreach (var r in after.EnumerateRunes())
        {
            bool keep = !Rune.IsControl(r);
            if (keep)
                kept.Add(r);
            if (keep && seen < caret)
                at++;
            seen++;
        }
        int excess = kept.Count - MaxCharacters;
        if (excess > 0)
        {
            var old = before.EnumerateRunes().ToArray();
            int head = 0;
            while (head < old.Length && head < kept.Count && old[head] == kept[head])
                head++;
            int tail = 0;
            while (tail < old.Length - head && tail < kept.Count - head && old[^(tail + 1)] == kept[^(tail + 1)])
                tail++;
            int end = kept.Count - tail;   // one past what was put in
            int from = Math.Max(head, end - excess);
            int cut = end - from;
            kept.RemoveRange(from, cut);
            if (at > from)
                at = Math.Max(from, at - cut);
            if (kept.Count > MaxCharacters)
                kept.RemoveRange(MaxCharacters, kept.Count - MaxCharacters);
        }
        var text = new StringBuilder();
        foreach (var r in kept)
            text.Append(r.ToString());
        return (text.ToString(), Math.Min(at, kept.Count));
    }
}

/// <summary>Completing a gesture's trigger as it is typed.</summary>
public static class GestureCompletion
{
    /// <summary>The most triggers offered at once.</summary>
    public const int MaxShown = 8;

    /// <summary>The triggers that begin with the line typed and add something to it, when the line starts with "/" and has at least one
    /// character more; case is ignored.</summary>
    public static IReadOnlyList<string> Matches(string text, IEnumerable<string> triggers)
    {
        string head = text.TrimStart();
        if (head.Length < 2 || head[0] != '/')
            return [];
        return triggers.Where(t => t.Length > head.Length && t.StartsWith(head, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Take(MaxShown)
            .ToArray();
    }

    /// <summary>The line with the trigger completed: what was typed stays as typed, the rest of the trigger is added.</summary>
    public static string Complete(string text, string trigger)
    {
        int typed = text.TrimStart().Length;
        return typed >= trigger.Length ? text : text + trigger[typed..];
    }
}
