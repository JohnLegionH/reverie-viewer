// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>Fits a text into a width by ending it with an ellipsis.</summary>
public static class Ellipsis
{
    public const string Mark = "…";

    /// <summary>
    /// The text itself when <paramref name="measure"/> (its width in the font that will draw it) says it fits in <paramref name="width"/>;
    /// otherwise the longest start of it that, with an ellipsis after it, still fits. A space or half a surrogate pair at the cut is
    /// dropped. Empty when not even the ellipsis fits.
    /// </summary>
    public static string Fit(string? text, float width, Func<string, float> measure)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        if (measure(text) <= width)
            return text;
        if (measure(Mark) > width)
            return "";
        int lo = 0, hi = text.Length - 1;   // the longest prefix length whose text + ellipsis fits
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (measure(text[..mid] + Mark) <= width)
                lo = mid;
            else
                hi = mid - 1;
        }
        string head = text[..lo];
        if (head.Length > 0 && char.IsHighSurrogate(head[^1]))
            head = head[..^1];
        return head.TrimEnd() + Mark;
    }
}
