// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Linq;
using Viewer.Core.Links;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Links;

namespace Viewer.Godot;

/// <summary>
/// The one link router every text place uses (notecards, nearby chat, conversations, group and object chat), and what a click
/// does. The places render through <see cref="LinkText"/> with <see cref="Router"/> and hand a click to <see cref="Click"/>, which
/// answers the line to show under the text (a note), or null. Main thread only.
/// </summary>
internal static class HostLinks
{
    private static readonly LinkGate Gate = new(() => DateTimeOffset.Now);

    public static Func<string?> LoginUri = () => null;
    public static Func<string?> CurrentGrid = () => null;
    public static Func<Guid, string?> AgentName = _ => null;
    public static Func<Guid, string?> GroupName = _ => null;
    public static Func<LinkAction, string?> Perform = _ => null;

    public static readonly LinkRouter Router = new(Gate, () => LoginUri(), id => AgentName(id), id => GroupName(id), currentGrid: () => CurrentGrid());

    /// <summary>The last click's action, for the development automation.</summary>
    public static LinkAction? LastAction { get; private set; }

    /// <summary>The link targets in a rich text, in order (what a click on each hands to <see cref="Click"/>).</summary>
    public static string[] Targets(string bbcode) =>
        System.Text.RegularExpressions.Regex.Matches(bbcode ?? "", @"\[url=(link:[^\]]*)\]").Select(m => m.Groups[1].Value).ToArray();

    /// <summary>A click on a link in untrusted text: what it does, and the note to show (null for none).</summary>
    public static string? Click(string raw)
    {
        LastAction = Router.Click(raw, LinkNavigation.Clicked, trusted: false);
        return LastAction is { } a ? Perform(a) : null;
    }
}

public partial class Main
{
    /// <summary>The router follows the session: its grid (for hop:// links) and the names it knows.</summary>
    private void AttachLinks(ViewerSession s)
    {
        HostLinks.LoginUri = () => s.IsEnded ? null : s.LoginUri;
        HostLinks.CurrentGrid = () => s.IsEnded ? null : s.VisitedGrid;   // while visiting, links name places by the visited grid; at home, by the login address as before
        HostLinks.AgentName = id => s.Names.Get(id);
        HostLinks.GroupName = id => _comms?.Groups.Groups.FirstOrDefault(g => g.Id == id)?.Name;
        HostLinks.Perform = PerformLink;
    }

    /// <summary>Does what a link asks: the world map at a place (its Teleport button waits for the user), the inventory, a web
    /// address through the action pipeline, or a note.</summary>
    private string? PerformLink(LinkAction action)
    {
        var strings = _strings ?? StringTable.English();
        switch (action.Kind)
        {
            case LinkActionKind.ShowOnMap:
                if (_travel is null)
                    return strings.Get("link.no_session");
                _shell?.Shell.Floaters.Open(TravelFeature.MapId);
                if (action.Region.Length > 0)
                    _travel.WorldMap.ShowDestination(action.Region, action.Position);
                return null;
            case LinkActionKind.ConfirmTeleport:
                // a place on another grid: asked first in the shared dialog, naming the place and its grid's host; only Teleport goes
                if (_travel is null || action.Travel is not { } place)
                    return strings.Get("link.no_session");
                _notes?.Model.Post(Viewer.Shell.Notifications.Confirmations.TeleportToPlace(place.Display, () =>
                {
                    string grid = _travelFor?.CurrentGrid ?? "";
                    _travel?.Teleport.ToAddress(place, grid.Length > 0 ? grid : null);
                }));
                return null;
            case LinkActionKind.OpenInventory:
                _shell?.Shell.Floaters.Open(InventoryFeature.FloaterId);
                return null;
            case LinkActionKind.OpenWeb:
                // through the session's pipeline, as before; with no session nothing opens
                if (action.Intent is not null && _inventoryFor is { IsEnded: false } s)
                    s.Actions.Submit(action.Intent);
                return null;
            default:
                return action.NoteKey is null ? null
                    : action.NoteArg is null ? strings.Get(action.NoteKey) : strings.Get(action.NoteKey, action.NoteArg);
        }
    }
}
