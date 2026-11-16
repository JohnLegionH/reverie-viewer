// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Xml;
using System.Xml.Linq;

namespace Viewer.Shell.Grids;

/// <summary>
/// What a grid publishes about itself in its grid-info document (OpenSim's get_grid_info): its name and short name, its login
/// address, its welcome, sign-up and forgot-password pages, its Hypergrid gatekeeper, its currency helper, and its other pages.
/// The fields read are the ones Firestorm's grid manager reads (fsgridhandler.cpp, gridInfoResponderCB): login, gridname,
/// gridnick, gatekeeper (kept as its host and port), welcome, register, password, help, about, search, web_profile_url or
/// profileuri, platform, message, and helperuri or economy (two names for the same address).
/// </summary>
public sealed record GridInfo(
    string? Name, string? Nick, Uri? Login, Uri? Welcome, Uri? Register, Uri? Password, string? Gatekeeper, Uri? Economy,
    Uri? Help, Uri? About, Uri? Search, Uri? Profile, string? Platform, string? Message)
{
    /// <summary>The longest text kept from any one field.</summary>
    public const int MaxText = 200;

    /// <summary>Whether the grid names a Hypergrid gatekeeper.</summary>
    public bool Hypergrid => Gatekeeper is not null;

    /// <summary>Whether the grid names a currency helper (its economy address).</summary>
    public bool Currency => Economy is not null;

    /// <summary>The grid-info document's fields, or null when the text is not a grid-info document.</summary>
    public static GridInfo? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        XElement? root;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 256 * 1024 };
            string readable = new(body.Where(c => !char.IsControl(c) || char.IsWhiteSpace(c)).ToArray());
            using var reader = XmlReader.Create(new StringReader(readable), settings);
            root = XDocument.Load(reader).Root;
        }
        catch (XmlException) { return null; }
        if (root is null || !root.Name.LocalName.Equals("gridinfo", StringComparison.OrdinalIgnoreCase))
            return null;

        string? Text(string name) => Clean(root.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value);
        Uri? Link(params string[] names) => names.Select(n => WebAddress(Text(n))).FirstOrDefault(u => u is not null);
        string? gatekeeper = WebAddress(Text("gatekeeper")) is { } gk ? (gk.IsDefaultPort ? gk.Host : gk.Host + ":" + gk.Port) : null;
        return new GridInfo(Text("gridname"), Text("gridnick"), Link("login"), Link("welcome"), Link("register"), Link("password"), gatekeeper,
            Link("economy", "helperuri"), Link("help"), Link("about"), Link("search"), Link("web_profile_url", "profileuri"), Text("platform"), Text("message"));
    }

    private static string? Clean(string? text)
    {
        if (text is null)
            return null;
        var kept = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (kept.Length > MaxText)
            kept = kept[..MaxText];
        return kept.Length == 0 ? null : kept;
    }

    /// <summary>An absolute http or https address with a host and no user name or password in it; else null.</summary>
    internal static Uri? WebAddress(string? text) =>
        text is not null && Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Host.Length > 0 ? uri : null;
}
