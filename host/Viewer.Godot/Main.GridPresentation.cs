// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Grids;
using Viewer.Shell.Notifications;

namespace Viewer.Godot;

/// <summary>
/// The grid the person is on, as the interface shows it: the grid's name in the top bar (or "Visiting name (host)" while the region
/// belongs to another grid), the currency symbol and the grid's name in the texts that name them, and the Help menu's grid pages. The
/// values come from <see cref="GridFeatures"/> (the region's own settings, then the login reply, then the grid's grid-info document).
/// On Second Life none of what a grid supplies applies.
/// </summary>
public partial class Main
{
    /// <summary>The grid entry the person logged in to (the home grid).</summary>
    private GridEntry? _homeGrid;

    private string? _presentedKey;
    private bool _presentedGrid;
    private string? _helpKey;
    private IReadOnlyList<GridLink> _helpLinks = [];

    /// <summary>What the session's grid-info values are: the grid's name, symbol, name of its money and the hosts that are the home grid's.</summary>
    private static GridLoginValues HomeGridValues(GridEntry? entry, GridInfo? info, string loginUri)
    {
        var hosts = new List<string>();
        if (Uri.TryCreate(loginUri, UriKind.Absolute, out var login))
            hosts.Add(login.Host);
        if (info?.Login is { } infoLogin)
            hosts.Add(infoLogin.Host);
        if (info?.Gatekeeper is { } gatekeeper)
            hosts.Add(gatekeeper);
        return new GridLoginValues(SearchUrl: info?.Search?.ToString(), CurrencyHelper: info?.Economy?.ToString(), GridName: info?.Name,
            InfoCurrency: info?.CurrencySymbol, CurrencyName: info?.CurrencyName, HomeHosts: hosts);
    }

    /// <summary>Brings the grid's name, symbol and Help pages up to date for the session (null: none, so the login screen's own show).</summary>
    private void UpdateGridPresentation(ViewerSession? s)
    {
        if (_shell is null || _strings is null)
            return;
        if (s is null)
        {
            if (!_presentedGrid)
                return;
            _presentedGrid = false;
            _presentedKey = null;
            _strings.Context.Currency = GridFeatures.SecondLifeSymbol;
            _strings.Context.Grid = "this grid";
            SetHelpLinks([]);
            if (Model is { } m)
                ApplyBranding(m.Branding);
            return;
        }

        bool secondLife = s.Grid == GridKind.SecondLife;
        var f = s.GridFeatures;
        var all = _gridList?.All ?? [];
        string home = _homeGrid is null ? GridLabels.SafeName(null, GridEntry.HostName(new Uri(s.LoginUri)), s.LoginUri) : GridLabels.Brand(_homeGrid, all);
        string name = secondLife ? home
            : f.GridName is { } own && !f.Visiting
                ? (_homeGrid is null ? GridLabels.SafeName(own, home, s.LoginUri) : GridLabels.Brand(_homeGrid with { Name = own }, all))
                : home;
        string? visiting = f.Visiting && !secondLife ? GridLabels.VisitingLine(_strings, f.GridName, f.VisitedHost ?? "") : null;
        string currency = f.PriceSymbol(secondLife);
        string placeName = visiting is not null ? GridLabels.SafeName(f.GridName, f.VisitedHost ?? "") : name;
        var links = secondLife ? [] : GridLinks.HelpMenu(_homeGrid?.Info, home, false);
        string key = string.Join("\u0001", visiting ?? name, currency, placeName, string.Join("\u0002", links.Select(l => l.Id + l.LabelArg + l.Page)));
        if (key == _presentedKey)
            return;
        _presentedKey = key;
        _presentedGrid = true;
        _strings.Context.Currency = currency;
        _strings.Context.Grid = placeName;
        _shell.SetBrand(visiting ?? name);
        _gridName = name;
        UpdateWindowTitle();
        SetHelpLinks(links);
    }

    /// <summary>Puts the grid's pages in the Help menu (and takes the earlier ones out); an empty list leaves none.</summary>
    private void SetHelpLinks(IReadOnlyList<GridLink> links)
    {
        if (_shell is null)
            return;
        string key = string.Join("\u0002", links.Select(l => l.Id + l.LabelArg + l.Page));
        if (key == _helpKey)
            return;
        _helpKey = key;
        var menus = _shell.Shell.Menus;
        foreach (string id in new[] { GridLinks.HelpId, GridLinks.AboutId, GridLinks.ViewerId })
            menus.RemoveEntry("help", id);
        _helpLinks = links;
        int order = 200;
        foreach (var link in links)
        {
            string command = "grid.link." + link.Id;
            string id = link.Id;
            if (!_shell.Shell.Commands.Has(command))
                _shell.Shell.Commands.Register(command, () => OpenGridLink(id));
            menus.AddEntry("help", new MenuEntry(link.Id, link.LabelKey, command, Order: order++, LabelArg: link.LabelArg));
        }
    }

    /// <summary>A grid page from the Help menu: the question that shows its host comes first, and only its first button opens the page in
    /// the person's own browser.</summary>
    private void OpenGridLink(string id)
    {
        if (_helpLinks.FirstOrDefault(l => l.Id == id) is not { } link)
            return;
        string page = link.Page.ToString();
        _notes?.Model.Post(Confirmations.OpenLink(link.Page, () => OS.ShellOpen(page)));
    }
}
