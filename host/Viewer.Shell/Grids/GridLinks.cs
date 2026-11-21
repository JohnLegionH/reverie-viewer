// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell.Notifications;

namespace Viewer.Shell.Grids;

/// <summary>One link a grid offers: the id of its menu entry, the string key and argument of its label, and the page.</summary>
public sealed record GridLink(string Id, string LabelKey, string LabelArg, Uri Page);

/// <summary>
/// The pages a grid offers from its grid-info document: its help and about pages and its page for getting this viewer (the Help menu),
/// and its welcome page (the login screen). A link whose key the grid does not set is not made. Every link opens in the person's own
/// browser, after the person has been shown which host it is on (<see cref="HostText"/>, <see cref="Confirmations.OpenLink"/>). On Second
/// Life nothing a grid supplies applies, so there are no grid links there.
/// </summary>
public static class GridLinks
{
    public const string HelpId = "help.grid_help", AboutId = "help.grid_about", ViewerId = "help.grid_viewer";

    /// <summary>The Help menu's grid entries, in order: help, about, and "Get Reverie from the grid" when it names a page for it.</summary>
    public static IReadOnlyList<GridLink> HelpMenu(GridInfo? info, string gridName, bool secondLife)
    {
        if (info is null || secondLife)
            return [];
        var list = new List<GridLink>();
        if (info.Help is { } help)
            list.Add(new GridLink(HelpId, "menu.help.grid_help", gridName, help));
        if (info.About is { } about)
            list.Add(new GridLink(AboutId, "menu.help.grid_about", gridName, about));
        if (info.ViewerUrl is { } viewer)
            list.Add(new GridLink(ViewerId, "menu.help.grid_viewer", gridName, viewer));
        return list;
    }

    /// <summary>The login screen's "Visit the grid's page": the grid's welcome page, or null when it sets none (or on Second Life).</summary>
    public static GridLink? Welcome(GridInfo? info, string gridName, bool secondLife) =>
        info?.Welcome is { } page && !secondLife ? new GridLink("login.visit_grid", "login.visit_grid", gridName, page) : null;

    /// <summary>The host of a page as the confirmation shows it, with the port when it is not the scheme's own.</summary>
    public static string HostText(Uri page) => GridEntry.HostName(page).ToLowerInvariant();
}
