// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Xml;
using System.Xml.Linq;

namespace Viewer.Shell;

/// <summary>One item of a grid's news.</summary>
public sealed record NewsItem(string Title, string Text);

/// <summary>
/// How a grid presents itself on the login screen and in the menu bar, from what the grid itself publishes in its grid-info
/// document: its name, an optional tagline and accent colour, the addresses of its logo and splash picture, its register and
/// forgot-password links, and optional news. A grid that publishes none of it gets the viewer's own placeholder look.
/// Nothing here is fetched or drawn; every address is only checked to be an http or https address.
/// </summary>
public sealed record GridBranding(
    string? Name, string? Tagline, UiColour? Accent, Uri? Logo, Uri? Splash, Uri? Register, Uri? ForgotPassword, IReadOnlyList<NewsItem> News)
{
    /// <summary>The longest name, tagline or news text kept.</summary>
    public const int MaxText = 200;

    /// <summary>A grid with no branding: no name (the viewer's placeholder is shown), no accent, no links, no news.</summary>
    public static GridBranding None { get; } = new(null, null, null, null, null, null, null, []);

    /// <summary>The branding in a grid-info document, or <see cref="None"/> when it is not one.</summary>
    public static GridBranding FromGridInfo(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return None;
        XElement? root;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 256 * 1024 };
            // control characters are not valid in XML text; they are taken out before reading rather than refusing the whole document
            string readable = new(body.Where(c => !char.IsControl(c) || char.IsWhiteSpace(c)).ToArray());
            using var reader = XmlReader.Create(new StringReader(readable), settings);
            root = XDocument.Load(reader).Root;
        }
        catch (XmlException) { return None; }
        if (root is null || !root.Name.LocalName.Equals("gridinfo", StringComparison.OrdinalIgnoreCase))
            return None;

        XElement? Element(string name) => root.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
        string? Text(string name) => Clean(Element(name)?.Value);
        var news = Element("news")?.Elements().Where(e => e.Name.LocalName.Equals("item", StringComparison.OrdinalIgnoreCase))
            .Select(e => (Title: Clean((string?)e.Attribute("title")), Text: Clean(e.Value)))
            .Where(i => i.Text is not null)
            .Take(MaxNews)
            .Select(i => new NewsItem(i.Title ?? "", i.Text!))
            .ToList() ?? [];
        return new GridBranding(Text("gridname"), Text("tagline"), Text("accent") is { } a ? ThemePack.ParseColour(a) : null,
            Link(Text("logo")), Link(Text("splash")), Link(Text("register")), Link(Text("password")), news);
    }

    /// <summary>The most news items kept.</summary>
    public const int MaxNews = 10;

    /// <summary>The text without control characters, trimmed and cut to <see cref="MaxText"/>; null when nothing is left.</summary>
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
    private static Uri? Link(string? text) =>
        text is not null && Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Host.Length > 0 ? uri : null;
}
