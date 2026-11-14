// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using System.Numerics;

namespace Viewer.Core.Links;

public enum ViewerLinkKind { Invalid, Location, App }

/// <summary>How the link was written: secondlife://, the OpenSim hop:// form, or a maps.secondlife.com web address.</summary>
public enum ViewerLinkScheme { SecondLife, Hop, Maps }

/// <summary>
/// A location link or an app link, read the way the official viewer reads SLURLs (indra/newview/llslurl.cpp) and the way
/// Firestorm reads the OpenSim hop:// form (fsslurl.cpp), in our words. A location names a region and a place in it; an app
/// link names a command (agent, group, teleport, worldmap, inventory, ...) and its path.
/// </summary>
/// <param name="Raw">The link as written.</param>
/// <param name="Host">A hop:// link's grid host (lower case); null for the other forms.</param>
/// <param name="Port">A hop:// link's grid port, null when it gave none.</param>
/// <param name="Region">The region name, unescaped; empty for an app link that names none.</param>
/// <param name="Position">The place in the region, clamped; the region's middle when the link gave no coordinates.</param>
/// <param name="Coordinates">How many coordinates the link gave (0 to 3), for the label.</param>
/// <param name="Command">An app link's command, lower case ("agent", "teleport"); empty for a location.</param>
/// <param name="Path">An app link's path after the command, unescaped.</param>
public sealed record ViewerLink(string Raw, ViewerLinkKind Kind, ViewerLinkScheme Scheme, string? Host, int? Port, string Region,
    Vector3 Position, int Coordinates, string Command, IReadOnlyList<string> Path)
{
    /// <summary>The id an agent or group app link names (its first path part), or null.</summary>
    public Guid? Id => Kind == ViewerLinkKind.App && Path.Count > 0 && Guid.TryParse(Path[0], out var id) && id != Guid.Empty ? id : null;

    /// <summary>An agent or group app link's verb (about, inspect, displayname, completename), lower case; empty when none.</summary>
    public string Verb => Kind == ViewerLinkKind.App && Path.Count > 1 ? Path[1].ToLowerInvariant() : "";
}

/// <summary>Reads location and app links. Anything it does not know is <see cref="ViewerLinkKind.Invalid"/> or null.</summary>
public static class ViewerLinkParser
{
    /// <summary>The middle of a region, where a link without coordinates leads (the official viewer's default).</summary>
    public const float RegionMiddle = 128f;

    /// <summary>The highest a link's height may be; higher is clamped (the official world map's trackURL clamps to 0..4096).</summary>
    public const float MaxHeight = 4096f;

    /// <summary>The widest region a link's x or y may reach: OpenSim's large regions are up to this many metres on a side.</summary>
    public const float MaxRegionWidth = 8192f;

    /// <summary>Reads a link; null when the text is not a secondlife://, hop:// or maps.secondlife.com address.</summary>
    public static ViewerLink? Parse(string? text)
    {
        string raw = (text ?? "").Trim();
        int sep = raw.IndexOf("://", StringComparison.Ordinal);
        if (sep <= 0)
            return null;
        string scheme = raw[..sep].ToLowerInvariant();
        string rest = raw[(sep + 3)..];
        int cut = rest.IndexOfAny(['?', '#']);
        if (cut >= 0)
            rest = rest[..cut];
        string[] parts = rest.Split('/');
        string host = parts[0];
        // empty parts (a trailing slash, a doubled one) carry nothing, as in the official reader's path array
        var path = parts.Skip(1).Where(p => p.Length > 0).ToList();
        switch (scheme)
        {
            case "http" or "https":
                // http(s)://maps.secondlife.com/secondlife/Region/x/y/z
                if (!host.Equals("maps.secondlife.com", StringComparison.OrdinalIgnoreCase) || path.Count < 2
                    || !path[0].Equals("secondlife", StringComparison.OrdinalIgnoreCase))
                    return null;
                return Location(raw, ViewerLinkScheme.Maps, null, null, path[1], path.Skip(2).ToList());
            case "secondlife" or "hop":
            {
                var form = scheme == "hop" ? ViewerLinkScheme.Hop : ViewerLinkScheme.SecondLife;
                if (host.Length == 0)
                {
                    // secondlife:///app/..., secondlife:///secondlife/Region/...; hop:///app/... is Firestorm's app form
                    if (path.Count > 0 && path[0].Equals("app", StringComparison.OrdinalIgnoreCase))
                        return App(raw, form, path.Skip(1).ToList());
                    if (path.Count > 1 && path[0].Equals("secondlife", StringComparison.OrdinalIgnoreCase))
                        return Location(raw, form, null, null, path[1], path.Skip(2).ToList());
                    return Invalid(raw, form);
                }
                if (form == ViewerLinkScheme.SecondLife)
                {
                    if (host.Equals("app", StringComparison.OrdinalIgnoreCase))
                        return App(raw, form, path);
                    return Location(raw, form, null, null, host, path);   // secondlife://Region/x/y/z
                }
                // hop://host:port/Region/x/y/z
                string gridHost = host;
                int? port = null;
                int colon = host.LastIndexOf(':');
                if (colon >= 0)
                {
                    gridHost = host[..colon];
                    if (int.TryParse(host.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int p) && p is > 0 and <= 65535)
                        port = p;
                    else
                        return Invalid(raw, form);
                }
                if (gridHost.Length == 0 || path.Count == 0)
                    return Invalid(raw, form) with { Host = gridHost.Length == 0 ? null : gridHost.ToLowerInvariant(), Port = port };
                return Location(raw, form, gridHost.ToLowerInvariant(), port, path[0], path.Skip(1).ToList());
            }
            default:
                return null;
        }
    }

    private static ViewerLink Invalid(string raw, ViewerLinkScheme scheme) =>
        new(raw, ViewerLinkKind.Invalid, scheme, null, null, "", default, 0, "", []);

    private static ViewerLink App(string raw, ViewerLinkScheme scheme, List<string> path)
    {
        if (path.Count == 0)
            return Invalid(raw, scheme);
        string command = path[0].ToLowerInvariant();
        var args = path.Skip(1).Select(Unescape).ToList();
        // the region command is a location written as an app link; teleport and worldmap name a place too
        if (command == "region")
            return path.Count > 1 ? Location(raw, scheme, null, null, path[1], path.Skip(2).ToList()) : Invalid(raw, scheme);
        if (command is "teleport" or "worldmap" && path.Count > 1)
        {
            var place = Location(raw, scheme, null, null, path[1], path.Skip(2).ToList());
            if (place.Kind == ViewerLinkKind.Invalid)
                return place;
            return place with { Kind = ViewerLinkKind.App, Command = command, Path = args };
        }
        return new ViewerLink(raw, ViewerLinkKind.App, scheme, null, null, "", default, 0, command, args);
    }

    private static ViewerLink Location(string raw, ViewerLinkScheme scheme, string? host, int? port, string region, List<string> coords)
    {
        string name = new string(Unescape(region).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0)
            return Invalid(raw, scheme) with { Host = host, Port = port };
        int given = Math.Min(coords.Count, 3);
        float Coord(int i, float fallback) =>
            i < given && float.TryParse(coords[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && float.IsFinite(v) ? v : fallback;
        var position = new Vector3(
            Math.Clamp(Coord(0, RegionMiddle), 0f, MaxRegionWidth),
            Math.Clamp(Coord(1, RegionMiddle), 0f, MaxRegionWidth),
            Math.Clamp(Coord(2, 0f), 0f, MaxHeight));
        return new ViewerLink(raw, ViewerLinkKind.Location, scheme, host, port, name, position, given, "", []);
    }

    private static string Unescape(string s)
    {
        try
        {
            return Uri.UnescapeDataString(s);
        }
        catch (UriFormatException)
        {
            return s;
        }
    }

    /// <summary>Whether a web address is a maps.secondlife.com place (it reads as a location).</summary>
    public static bool IsMapsAddress(string text) => Parse(text) is { Scheme: ViewerLinkScheme.Maps };

    /// <summary>The label the official viewer shows for a location: "Region (x, y, z)", with as many coordinates as the link gave.</summary>
    public static string LocationLabel(ViewerLink link)
    {
        if (link.Coordinates == 0)
            return link.Region;
        static string R(float v) => ((int)MathF.Round(v, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
        var shown = new[] { R(link.Position.X), R(link.Position.Y), R(link.Position.Z) }.Take(link.Coordinates);
        return $"{link.Region} ({string.Join(", ", shown)})";
    }
}

/// <summary>Where a link came from, as the official dispatcher's navigation type: a click on it, or anything else (a page or a
/// script that tries to open it without the user).</summary>
public enum LinkNavigation { Clicked, Other }

/// <summary>The official dispatcher's access levels for untrusted sources (indra/newview/llcommandhandler.h).</summary>
public enum LinkAccess { Allow, Block, ClickOnly, Throttle }

public enum LinkVerdict { Run, Blocked, Throttled }

/// <summary>
/// The trust rules for links (indra/newview/llcommandhandler.cpp, in our words). Text from chat, notecards and objects is
/// untrusted: its links never run on their own, only on the user's click, and the throttled commands run at most once per
/// throttle period. Trusted text (the viewer's own) runs. One gate per session; main thread only.
/// </summary>
public sealed class LinkGate(Func<DateTimeOffset> clock)
{
    /// <summary>The official viewer's throttle period: seconds between two throttled commands.</summary>
    public static readonly TimeSpan ThrottlePeriod = TimeSpan.FromSeconds(5);

    private readonly Func<DateTimeOffset> _clock = clock;

    /// <summary>The access level of a link's command, as the official viewer registers it.</summary>
    // a location moves the map and offers a teleport, so it is click-only like the official teleport command
    public static LinkAccess AccessOf(ViewerLink link) => link.Kind switch
    {
        ViewerLinkKind.Location => LinkAccess.ClickOnly,
        ViewerLinkKind.App => link.Command switch
        {
            "teleport" => LinkAccess.ClickOnly,
            "agent" or "group" or "worldmap" or "inventory" => LinkAccess.Throttle,
            _ => LinkAccess.Block,
        },
        _ => LinkAccess.Block,
    };

    private DateTimeOffset? _lastThrottled;

    /// <summary>Whether a link may run now. Untrusted text never runs a link without a click (stricter than the official
    /// throttle level, which lets a few through without one); a throttled command, clicked, runs at most once per
    /// <see cref="ThrottlePeriod"/>, the one period shared by all of them as in the official dispatcher.</summary>
    public LinkVerdict Check(ViewerLink link, LinkNavigation navigation, bool trusted)
    {
        if (trusted)
            return LinkVerdict.Run;
        switch (AccessOf(link))
        {
            case LinkAccess.Allow:
                return LinkVerdict.Run;
            case LinkAccess.ClickOnly:
                return navigation == LinkNavigation.Clicked ? LinkVerdict.Run : LinkVerdict.Blocked;
            case LinkAccess.Throttle:
            {
                if (navigation != LinkNavigation.Clicked)
                    return LinkVerdict.Blocked;
                var now = _clock();
                if (_lastThrottled is { } last && now < last + ThrottlePeriod)
                    return LinkVerdict.Throttled;
                _lastThrottled = now;
                return LinkVerdict.Run;
            }
            default:
                return LinkVerdict.Blocked;
        }
    }
}
