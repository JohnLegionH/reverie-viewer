// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Chat;

/// <summary>
/// Which nearby chat lines show over the bottom-left of the view. The newest few show; with a time set, a line older than that
/// many seconds is no longer shown (0: it stays until newer lines push it out, as before the setting). The window of nearby chat
/// still holds every line.
/// </summary>
public static class NearbyOverlayRules
{
    /// <summary>A line to show and how opaque to draw it (1: whole).</summary>
    public readonly record struct ShownLine(ChatLine Line, float Opacity);

    /// <summary>The lines to show with their opacity.</summary>
    public static IReadOnlyList<ShownLine> Shown(IReadOnlyList<ChatLine> lines, DateTimeOffset now, int life, int fade, int shown)
    {
        var kept = new List<ShownLine>();
        foreach (var line in lines)
        {
            double age = (now - line.At).TotalSeconds;
            if (life <= 0 || age <= life)
                kept.Add(new ShownLine(line, 1f));
            else if (age <= life + fade)
                kept.Add(new ShownLine(line, FadingOpacity));
        }
        return kept.TakeLast(Math.Max(0, shown)).ToList();
    }

    /// <summary>The opacity of a line in its fading time: half of the whole (the official toast's fading transparency is half the inactive one).</summary>
    public const float FadingOpacity = 0.5f;

    /// <summary>The lines to show, oldest first, at most <paramref name="shown"/> of them.</summary>
    public static IReadOnlyList<ChatLine> Visible(IReadOnlyList<ChatLine> lines, DateTimeOffset now, int seconds, int shown)
    {
        var kept = seconds <= 0 ? lines : lines.Where(l => (now - l.At).TotalSeconds <= seconds);
        return kept.TakeLast(Math.Max(0, shown)).ToList();
    }
}
