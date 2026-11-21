// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Viewer.Core.Links;

/// <summary>Why an address could not be read; each has a string "travel.error.&lt;key&gt;".</summary>
public enum TravelAddressError { None, Empty, TooLong, BadPort, NoRegion, BadRegion, NotAPlace }

/// <summary>
/// A place a user can travel to: a region on the grid they are on (<see cref="Host"/> null), or a region on another grid named by
/// that grid's address (a Hypergrid address). Every form a user meets reads to one of these:
/// <c>hop://host:port/Region/x/y/z</c> (Firestorm's Hypergrid link, fsslurl.cpp), <c>host:port:Region</c> and <c>host:port Region</c>
/// (the forms OpenSim's map search reads, OpenSim.Framework RegionURI), <c>http(s)://host:port/ Region</c> or
/// <c>http(s)://host:port/Region/x/y/z</c> (a grid's web address with a region), <c>secondlife://Region/x/y/z</c> (the official
/// viewer's location link, llslurl.cpp) and a plain region name, which means the grid the user is on.
/// </summary>
/// <param name="Host">The grid's host, lower case; null for a region on the grid the user is on.</param>
/// <param name="Port">The grid's port; null when the address gave none (port 80 then, as OpenSim reads it).</param>
/// <param name="Region">The region's name, unescaped.</param>
/// <param name="Position">The place in the region; its middle at ground level when the address gave none.</param>
/// <param name="Coordinates">How many coordinates the address gave (0 to 3).</param>
public sealed record TravelAddress(string? Host, int? Port, string Region, Vector3 Position, int Coordinates)
{
    /// <summary>The longest address read (OpenSim's map search name field is a 255-byte string).</summary>
    public const int MaxLength = 255;

    /// <summary>The longest region name read (OpenSim and the official viewer allow far fewer; this only bounds what is sent).</summary>
    public const int MaxRegionChars = 100;

    /// <summary>Whether the address names another grid (a Hypergrid address).</summary>
    public bool IsHypergrid => Host is not null;

    /// <summary>The grid's port, 80 when the address gave none.</summary>
    public int EffectivePort => Port ?? 80;

    /// <summary>The grid as "host:port" (the key Reverie keeps regions by); null for the grid the user is on.</summary>
    public string? Grid => Host is null ? null : GridKeys.Of(Host, EffectivePort);

    /// <summary>The name the region's map is asked for: the region name on the grid the user is on, "host:port:Region" for another grid
    /// (the form OpenSim's map search links to, HypergridLinker via RegionURI).</summary>
    public string LookupName => Host is null ? Region : $"{Host}:{EffectivePort.ToString(CultureInfo.InvariantCulture)}:{Region}";

    /// <summary>What the user is shown for the place: "Region" or "Region (host:port)".</summary>
    public string Display => Host is null ? Region : $"{Region} ({Host}:{EffectivePort.ToString(CultureInfo.InvariantCulture)})";

    /// <summary>The address as a hop:// link (the form teleport history keeps, so the place can be gone back to); a region on the user's
    /// own grid is written for <paramref name="currentGrid"/> ("host:port"), or as secondlife:// when that is not known.</summary>
    public string ToHop(string? currentGrid = null)
    {
        string esc = Uri.EscapeDataString(Region);
        string coords = string.Create(CultureInfo.InvariantCulture, $"{(int)MathF.Round(Position.X)}/{(int)MathF.Round(Position.Y)}/{(int)MathF.Round(Position.Z)}");
        string? grid = Grid ?? currentGrid;
        return grid is null ? $"secondlife://{esc}/{coords}" : $"hop://{grid}/{esc}/{coords}";
    }

    /// <summary>The same place, as a region on the grid the user is on when the address names that grid (<paramref name="currentGrid"/>,
    /// "host:port"); otherwise unchanged.</summary>
    public TravelAddress OnGrid(string? currentGrid) =>
        Grid is { } g && currentGrid is not null && string.Equals(g, currentGrid, StringComparison.OrdinalIgnoreCase) ? this with { Host = null, Port = null } : this;
}

/// <summary>Grid keys: "host:port", lower case, the one spelling used to tell grids apart.</summary>
public static class GridKeys
{
    public static string Of(string host, int port) => $"{host.Trim().TrimEnd('.').ToLowerInvariant()}:{port.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The key of a grid's web address (login address, gatekeeper or home URI), or null when it is not one.</summary>
    public static string? OfUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return null;
        string text = uri.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "http://" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out var u) && u.Host.Length > 0 && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? Of(u.Host, u.Port)
            : null;
    }

    /// <summary>The host of a key ("host:port" without the port).</summary>
    public static string HostOf(string key)
    {
        int colon = key.LastIndexOf(':');
        return colon > 0 ? key[..colon] : key;
    }
}

/// <summary>Reads a travel address (see <see cref="TravelAddress"/>).</summary>
public static partial class TravelAddressParser
{
    // host:port:Region, host:port Region, host:Region: a host with a dot (OpenSim's RegionURI needs one) or an IPv6 literal in brackets
    [GeneratedRegex(@"^(?<host>\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)+)(?::(?<port>[0-9]{1,6}))?(?:(?<sep>[: +])(?<rest>.*))?$")]
    private static partial Regex HostForm();

    public static TravelAddress? Parse(string? text) => Parse(text, out _);

    public static TravelAddress? Parse(string? text, out TravelAddressError error)
    {
        string raw = (text ?? "").Trim();
        error = TravelAddressError.None;
        if (raw.Length == 0)
            return Fail(TravelAddressError.Empty, out error);
        if (raw.Length > TravelAddress.MaxLength)
            return Fail(TravelAddressError.TooLong, out error);
        if (raw.Any(char.IsControl))
            return Fail(TravelAddressError.BadRegion, out error);

        int sep = raw.IndexOf("://", StringComparison.Ordinal);
        if (sep > 0)
        {
            string scheme = raw[..sep].ToLowerInvariant();
            if (scheme is "http" or "https")
            {
                if (ViewerLinkParser.IsMapsAddress(raw))
                    return FromLink(ViewerLinkParser.Parse(raw)!, out error);
                return FromWeb(raw, scheme == "https" ? 443 : 80, out error);
            }
            if (scheme is "hop" or "secondlife")
            {
                var link = ViewerLinkParser.Parse(raw);
                if (link is null || link.Kind == ViewerLinkKind.Invalid)
                {
                    // the link parser leaves the reason out; a hop:// with a host but no region, or a bad port, is said as such
                    string afterScheme = raw[(sep + 3)..];
                    string hostPart = afterScheme.Split('/')[0];
                    int c = hostPart.LastIndexOf(':');
                    if (scheme == "hop" && c >= 0 && !ValidPort(hostPart[(c + 1)..], out _))
                        return Fail(TravelAddressError.BadPort, out error);
                    return Fail(TravelAddressError.NoRegion, out error);
                }
                // an app link that names a place (secondlife:///app/teleport/Region/x/y/z) travels there too
                if (link.Kind == ViewerLinkKind.App && link is not { Command: "teleport" or "worldmap" or "region" })
                    return Fail(TravelAddressError.NotAPlace, out error);
                return FromLink(link, out error);
            }
            return Fail(TravelAddressError.NotAPlace, out error);
        }

        var m = HostForm().Match(raw);
        // a host form needs a port, or a colon before the region: "Mr.Smith Island" stays a plain region name
        if (m.Success && (m.Groups["port"].Success || m.Groups["sep"].Value == ":"))
        {
            int? port = null;
            if (m.Groups["port"].Success)
            {
                if (!ValidPort(m.Groups["port"].Value, out int p))
                    return Fail(TravelAddressError.BadPort, out error);
                port = p;
            }
            string rest = m.Groups["rest"].Success ? m.Groups["rest"].Value : "";
            return Build(m.Groups["host"].Value, port, rest, out error);
        }
        // a plain region name, optionally with /x/y/z: the grid the user is on
        return Build(null, null, raw, out error);
    }

    private static TravelAddress? FromLink(ViewerLink link, out TravelAddressError error)
    {
        error = TravelAddressError.None;
        if (link.Region.Length == 0)
            return Fail(TravelAddressError.NoRegion, out error);
        if (!ValidRegion(link.Region))
            return Fail(TravelAddressError.BadRegion, out error);
        // a hop:// link names its grid; secondlife:// and maps.secondlife.com places are on the grid the user is on
        string? host = link.Scheme == ViewerLinkScheme.Hop ? link.Host : null;
        return new TravelAddress(host, host is null ? null : link.Port, link.Region, Clamp(link.Position, link.Coordinates), link.Coordinates);
    }

    // http(s)://host:port/ Region  or  http(s)://host:port/Region/x/y/z  (the region may also follow a space after the address)
    private static TravelAddress? FromWeb(string raw, int defaultPort, out TravelAddressError error)
    {
        error = TravelAddressError.None;
        int sep = raw.IndexOf("://", StringComparison.Ordinal);
        string rest = raw[(sep + 3)..];
        int end = rest.IndexOfAny(['/', ' ']);
        string authority = end < 0 ? rest : rest[..end];
        string tail = end < 0 ? "" : rest[end..].TrimStart('/', ' ');
        int colon = authority.LastIndexOf(':');
        string host = authority;
        int port = defaultPort;
        if (colon >= 0 && !authority.EndsWith(']'))
        {
            host = authority[..colon];
            if (!ValidPort(authority[(colon + 1)..], out port))
                return Fail(TravelAddressError.BadPort, out error);
        }
        if (host.Length == 0 || Uri.CheckHostName(host.Trim('[', ']')) == UriHostNameType.Unknown)
            return Fail(TravelAddressError.NotAPlace, out error);
        return Build(host, port, tail, out error);
    }

    private static TravelAddress? Build(string? host, int? port, string rest, out TravelAddressError error)
    {
        error = TravelAddressError.None;
        rest = rest.Trim();
        // the region name runs to the first '/', and the coordinates follow (as RegionURI reads them)
        string[] parts = rest.Split('/');
        string region;
        try
        {
            region = Uri.UnescapeDataString(parts[0].Replace('+', ' ')).Trim();
        }
        catch (UriFormatException)
        {
            region = parts[0].Trim();
        }
        if (region.Length == 0)
            return Fail(TravelAddressError.NoRegion, out error);
        if (!ValidRegion(region))
            return Fail(TravelAddressError.BadRegion, out error);
        var coords = parts.Skip(1).Where(p => p.Length > 0).Take(3).ToList();
        int given = 0;
        var v = new float[] { ViewerLinkParser.RegionMiddle, ViewerLinkParser.RegionMiddle, 0f };
        foreach (var c in coords)
        {
            if (!float.TryParse(c, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) || !float.IsFinite(f))
                break;
            v[given++] = f;
        }
        string? h = host?.ToLowerInvariant();
        return new TravelAddress(h, h is null ? null : port, region, Clamp(new Vector3(v[0], v[1], v[2]), given), given);
    }

    private static Vector3 Clamp(Vector3 p, int given) => new(
        Math.Clamp(given > 0 ? p.X : ViewerLinkParser.RegionMiddle, 0f, ViewerLinkParser.MaxRegionWidth),
        Math.Clamp(given > 1 ? p.Y : ViewerLinkParser.RegionMiddle, 0f, ViewerLinkParser.MaxRegionWidth),
        Math.Clamp(given > 2 ? p.Z : 0f, 0f, ViewerLinkParser.MaxHeight));

    private static bool ValidPort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;

    // OpenSim refuses a region name holding any of . , : ; \ / (RegionURI forbidonname); control characters never pass
    private static bool ValidRegion(string region) =>
        region.Length <= TravelAddress.MaxRegionChars && !region.Any(c => char.IsControl(c) || c is ':' or ';' or '\\' or '/');

    private static TravelAddress? Fail(TravelAddressError why, out TravelAddressError error)
    {
        error = why;
        return null;
    }
}
