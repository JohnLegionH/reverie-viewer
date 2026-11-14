// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// A login name split into the first and last name a grid's login service takes. The one username field is read the way the
/// official Second Life viewer reads it (indra/newview/llpanellogin.cpp, LLPanelLogin::getFields): the text is trimmed, split at
/// the first space, dot or underscore, the part after it (trimmed) is the last name, and a single word gets the last name
/// "Resident". A last name that still holds a space cannot be a name, and neither can an empty first or last name.
/// </summary>
public sealed record LoginName(string First, string Last)
{
    /// <summary>The last name the official viewer gives a single-word name.</summary>
    public const string SingleNameLast = "Resident";

    private static readonly char[] Separators = [' ', '.', '_'];

    /// <summary>The first and last name for a typed name, or null when the text cannot be one.</summary>
    public static LoginName? Parse(string? typed)
    {
        string text = (typed ?? "").Trim();
        if (text.Length == 0)
            return null;
        int at = text.IndexOfAny(Separators);
        string first = at < 0 ? text : text[..at];
        string last = at < 0 ? SingleNameLast : text[(at + 1)..].Trim();
        if (first.Length == 0 || last.Length == 0 || last.Contains(' '))
            return null;
        return new LoginName(first, last);
    }

    /// <summary>The name as the login screen shows it again: "First Last", or just "First" when the last name is Resident (any case).</summary>
    public static string Display(string first, string last) =>
        string.IsNullOrEmpty(last) || last.Equals(SingleNameLast, StringComparison.OrdinalIgnoreCase) ? first : first + " " + last;
}
