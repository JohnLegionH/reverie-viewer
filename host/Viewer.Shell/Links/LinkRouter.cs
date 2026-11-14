// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using System.Text;
using Viewer.Core.Actions;
using Viewer.Core.Inventory;
using Viewer.Core.Links;

namespace Viewer.Shell.Links;

public enum LinkActionKind { ShowOnMap, OpenInventory, OpenWeb, Note }

/// <summary>What a click on a link does: open the world map at a place (its Teleport button waits for the user), open the
/// inventory, open a web address through the action pipeline, or show a note (a string-table key and its argument).</summary>
public sealed record LinkAction(LinkActionKind Kind, string Region = "", Vector3 Position = default, string? NoteKey = null, string? NoteArg = null,
    ActionIntent? Intent = null);

/// <summary>
/// Turns a click on a link into what the viewer does, for every place that shows text (notecards, nearby chat, conversations,
/// group chat, object chat). Text from those places is untrusted (<see cref="LinkGate"/>). A location on the grid the user is on
/// opens the map there; a hop:// link to another grid and a maps.secondlife.com place off Second Life only say so. No engine types.
/// </summary>
/// <param name="loginUri">The login address of the grid the user is on (null before login).</param>
/// <param name="agentName">An agent's name when known, else null.</param>
/// <param name="groupName">A group's name when known, else null.</param>
public sealed class LinkRouter(LinkGate gate, Func<string?> loginUri, Func<Guid, string?>? agentName = null, Func<Guid, string?>? groupName = null)
{
    private readonly LinkGate _gate = gate;
    private readonly Func<string?> _loginUri = loginUri;
    private readonly Func<Guid, string?> _agentName = agentName ?? (_ => null);
    private readonly Func<Guid, string?> _groupName = groupName ?? (_ => null);

    /// <summary>The app commands this viewer handles; any other shows as plain text.</summary>
    public static readonly IReadOnlySet<string> BuiltCommands = new HashSet<string>(StringComparer.Ordinal) { "agent", "group", "teleport", "worldmap", "inventory" };

    /// <summary>The agent link verbs shown as the agent's name (the official display forms) and handled on a click.</summary>
    public static readonly IReadOnlySet<string> AgentVerbs = new HashSet<string>(StringComparer.Ordinal) { "about", "inspect", "displayname", "completename" };

    /// <summary>What a click on the link does; null for text that is not a link.</summary>
    public LinkAction? Click(string raw, LinkNavigation navigation = LinkNavigation.Clicked, bool trusted = false)
    {
        if (ViewerLinkParser.Parse(raw) is not { } link)
        {
            if (!IsWeb(raw))
                return null;
            string url = raw.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "http://" + raw : raw;
            return OpenUrlIntent.IsOpenable(url) ? new LinkAction(LinkActionKind.OpenWeb, Intent: new OpenUrlIntent(url)) : null;
        }
        if (link.Kind == ViewerLinkKind.Invalid)
            return null;
        if (!Clickable(link))
            return new LinkAction(LinkActionKind.Note, NoteKey: "link.not_built", NoteArg: raw);
        switch (_gate.Check(link, navigation, trusted))
        {
            case LinkVerdict.Blocked:
                return new LinkAction(LinkActionKind.Note, NoteKey: "link.blocked");
            case LinkVerdict.Throttled:
                return new LinkAction(LinkActionKind.Note, NoteKey: "link.throttled");
        }
        if (link.Kind == ViewerLinkKind.Location)
        {
            if (link.Scheme == ViewerLinkScheme.Hop && !SameGrid(link))
                return new LinkAction(LinkActionKind.Note, NoteKey: "link.other_grid", NoteArg: link.Host);
            if (link.Scheme == ViewerLinkScheme.Maps && !OnSecondLife())
                return new LinkAction(LinkActionKind.Note, NoteKey: "link.sl_place", NoteArg: link.Region);
            return new LinkAction(LinkActionKind.ShowOnMap, link.Region, link.Position);
        }
        return link.Command switch
        {
            "teleport" or "worldmap" => new LinkAction(LinkActionKind.ShowOnMap, link.Region, link.Position),
            "inventory" => new LinkAction(LinkActionKind.OpenInventory),
            // this viewer has no profile or group window yet: the name and a note say so
            "agent" => new LinkAction(LinkActionKind.Note, NoteKey: "link.profile_not_built", NoteArg: Label(link) ?? link.Id?.ToString("D")),
            _ => new LinkAction(LinkActionKind.Note, NoteKey: "link.group_not_built", NoteArg: Label(link) ?? link.Id?.ToString("D")),
        };
    }

    private static bool IsWeb(string raw) =>
        raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || raw.StartsWith("www.", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a hop:// link names the grid the user is on (same host and port as the login address; a hop link
    /// without a port means port 80).</summary>
    public bool SameGrid(ViewerLink link)
    {
        if (link.Host is not { Length: > 0 } host || !Uri.TryCreate(_loginUri() ?? "", UriKind.Absolute, out var login))
            return false;
        return string.Equals(login.Host, host, StringComparison.OrdinalIgnoreCase) && login.Port == (link.Port ?? 80);
    }

    /// <summary>Whether the user is on Second Life (the login address is one of Linden Lab's).</summary>
    public bool OnSecondLife()
    {
        if (!Uri.TryCreate(_loginUri() ?? "", UriKind.Absolute, out var login))
            return false;
        string h = login.Host.ToLowerInvariant();
        return h == "lindenlab.com" || h.EndsWith(".lindenlab.com", StringComparison.Ordinal) || h == "secondlife.com" || h.EndsWith(".secondlife.com", StringComparison.Ordinal);
    }

    /// <summary>The text a link shows as: a location as "Region (x, y, z)", an agent or group as its name; null to show it as written.</summary>
    public string? Label(ViewerLink link)
    {
        if (link.Kind == ViewerLinkKind.Location || link is { Kind: ViewerLinkKind.App, Command: "teleport" or "worldmap", Region.Length: > 0 })
            return ViewerLinkParser.LocationLabel(link);
        if (!Clickable(link) || link.Id is not { } id)
            return null;
        string? name = link.Command switch
        {
            "agent" => _agentName(id),
            "group" => _groupName(id),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>Whether the link is one this viewer makes clickable (the rest stay plain text).</summary>
    public static bool Clickable(ViewerLink link) => link.Kind switch
    {
        ViewerLinkKind.Location => true,
        ViewerLinkKind.App => link.Command switch
        {
            "agent" => link.Id is not null && AgentVerbs.Contains(link.Verb),
            "group" => link.Id is not null && link.Verb == "about",
            "inventory" => link.Path is ["show", ..],
            "teleport" or "worldmap" => true,
            _ => false,
        },
        _ => false,
    };
}

/// <summary>
/// The one link renderer for rich text: cuts text into plain runs and links (web addresses, location and app links), escapes the
/// plain runs, and writes each clickable link as a rich-text link whose target is <see cref="MetaPrefix"/> and the escaped link.
/// Notecards and every chat place call it, so a link behaves the same wherever it shows.
/// </summary>
public static class LinkText
{
    public const string MetaPrefix = "link:";

    /// <summary>Text as rich text with its links clickable. <paramref name="escape"/> makes plain text literal.</summary>
    public static string Render(string text, Func<string, string> escape, LinkRouter? router)
    {
        text ??= "";
        if (router is null)
            return escape(text);
        var bb = new StringBuilder();
        foreach (var segment in NotecardLinks.Split(text))
            AppendSegment(bb, segment, escape, router);
        return bb.ToString();
    }

    /// <summary>One run of text: a link written as a rich-text link (its label escaped), anything else escaped.</summary>
    public static void AppendSegment(StringBuilder bb, NotecardSegment segment, Func<string, string> escape, LinkRouter router)
    {
        switch (segment.Kind)
        {
            case NotecardSegmentKind.WebLink when ViewerLinkParser.Parse(segment.Text) is null:
                Link(bb, segment.Text, escape(segment.Text));
                break;
            case NotecardSegmentKind.WebLink or NotecardSegmentKind.PlaceLink
                when ViewerLinkParser.Parse(segment.Text) is { } link && LinkRouter.Clickable(link):
                Link(bb, segment.Text, escape(router.Label(link) ?? segment.Text));
                break;
            default:
                bb.Append(escape(segment.Text));
                break;
        }
    }

    // the target is the link percent-escaped, so it holds no bracket that could end the tag
    private static void Link(StringBuilder bb, string raw, string label) =>
        bb.Append("[url=").Append(MetaPrefix).Append(Uri.EscapeDataString(raw)).Append(']').Append(label).Append("[/url]");

    /// <summary>The link a rich-text target names, or null when it is not one of ours.</summary>
    public static string? LinkOf(string meta)
    {
        if (meta is null || !meta.StartsWith(MetaPrefix, StringComparison.Ordinal))
            return null;
        try
        {
            string raw = Uri.UnescapeDataString(meta[MetaPrefix.Length..]);
            return raw.Length > 0 ? raw : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }
}
