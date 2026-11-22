// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Inventory;

namespace Viewer.Shell.Inventory;

/// <summary>The kinds of item the Recent tab's filter can show, as the official viewer's filter window lists them.</summary>
public enum RecentKind { Animation, CallingCard, Clothing, Gesture, Landmark, Material, Notecard, Object, Script, Sound, Texture, Settings }

/// <summary>
/// The Recent tab's filter, with the official viewer's options (its inventory filter window, floater_inventory_view_finder): the kinds of
/// item shown, "Since Logoff", or items newer or older than a number of hours and days ago. The date rule is the official viewer's
/// (llinventoryfilter.cpp, the FILTERTYPE_DATE test in checkAgainstFilterType, and setDateRangeLastLogoff): with Since Logoff the cut-off
/// is the last logout, or an earlier time when hours or days ago reach further back; with neither, every date passes. Written from the
/// behaviour, not copied. The filter only reads the items already held: it never asks the grid for folders.
/// </summary>
/// <param name="SinceLogoff">Items made since the account last logged out (the official default for the Recent tab).</param>
/// <param name="Hours">Hours ago (0 to 240,000), added to <paramref name="Days"/>.</param>
/// <param name="Days">Days ago (0 to 10,000).</param>
/// <param name="Older">Older than the time (else newer than it); only with hours or days and Since Logoff off.</param>
/// <param name="Hidden">The kinds left out (none by default).</param>
public sealed record RecentFilter(bool SinceLogoff = true, int Hours = 0, int Days = 0, bool Older = false, IReadOnlySet<RecentKind>? Hidden = null)
{
    public const int MaxHours = 240000, MaxDays = 10000;

    /// <summary>The Recent tab's filter as it opens: Since Logoff, every kind.</summary>
    public static RecentFilter Default { get; } = new();

    /// <summary>Hours and days together, as the official viewer adds them.</summary>
    public long HoursAgo => (long)Days * 24 + Hours;

    /// <summary>Whether an item made at <paramref name="created"/> (null: not known, as the official viewer's 0) of <paramref name="kind"/>
    /// shows, at <paramref name="now"/>, with the account's last logout <paramref name="lastLogout"/> (null: none kept yet).</summary>
    public bool Passes(DateTimeOffset? created, ItemKind kind, DateTimeOffset now, DateTimeOffset? lastLogout)
    {
        if (KindOf(kind) is { } k && Hidden is { } hidden && hidden.Contains(k))
            return false;
        if (!SinceLogoff && HoursAgo == 0)
            return true;   // no date limit
        var at = created ?? DateTimeOffset.UnixEpoch;   // an unknown date is the official viewer's 0
        var earliest = now.AddHours(-HoursAgo);
        // Since Logoff's lower limit wins when it reaches further back; with no logout kept it is 0, which counts as no limit. An item made in
        // the logout's own second (the time is kept in whole seconds) belongs to the session that ended, so it is left out.
        if (SinceLogoff && lastLogout is { } logout && logout > DateTimeOffset.UnixEpoch && logout < earliest)
            return at > logout;
        if (HoursAgo == 0)
            earliest = DateTimeOffset.UnixEpoch;
        return Older && !SinceLogoff ? at <= earliest : at >= earliest;
    }

    /// <summary>The filter after Since Logoff is ticked or cleared: ticking it sets hours and days back to 0, as the official window does.</summary>
    public RecentFilter WithSinceLogoff(bool on) => on ? this with { SinceLogoff = true, Hours = 0, Days = 0, Older = false } : this with { SinceLogoff = false };

    /// <summary>The filter after hours or days are changed: any time ago clears Since Logoff, as the official window does.</summary>
    public RecentFilter WithTimeAgo(int hours, int days)
    {
        int h = Math.Clamp(hours, 0, MaxHours), d = Math.Clamp(days, 0, MaxDays);
        return this with { Hours = h, Days = d, SinceLogoff = h == 0 && d == 0 && SinceLogoff };
    }

    /// <summary>The filter with one kind shown or hidden.</summary>
    public RecentFilter WithKind(RecentKind kind, bool shown)
    {
        var set = new HashSet<RecentKind>(Hidden ?? new HashSet<RecentKind>());
        if (shown) set.Remove(kind); else set.Add(kind);
        return this with { Hidden = set };
    }

    /// <summary>Whether a kind is shown.</summary>
    public bool Shows(RecentKind kind) => Hidden is null || !Hidden.Contains(kind);

    /// <summary>Whether anything differs from <see cref="Default"/> (the tab then says a filter is on).</summary>
    public bool IsDefault => SinceLogoff && Hours == 0 && Days == 0 && !Older && (Hidden is null || Hidden.Count == 0);

    /// <summary>The filter window's kind for an inventory item (clothing and body parts are both Clothing; a mesh is an Object).</summary>
    public static RecentKind? KindOf(ItemKind kind) => kind switch
    {
        ItemKind.Animation => RecentKind.Animation,
        ItemKind.CallingCard => RecentKind.CallingCard,
        ItemKind.Clothing or ItemKind.Bodypart => RecentKind.Clothing,
        ItemKind.Gesture => RecentKind.Gesture,
        ItemKind.Landmark => RecentKind.Landmark,
        ItemKind.Material => RecentKind.Material,
        ItemKind.Notecard => RecentKind.Notecard,
        ItemKind.Object or ItemKind.Mesh => RecentKind.Object,
        ItemKind.Script => RecentKind.Script,
        ItemKind.Sound => RecentKind.Sound,
        ItemKind.Texture => RecentKind.Texture,
        ItemKind.Settings => RecentKind.Settings,
        _ => null,   // links and anything else always show, as the kinds do not name them
    };
}
