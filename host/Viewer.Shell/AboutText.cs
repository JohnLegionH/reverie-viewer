// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Viewer.Core.Identity;

namespace Viewer.Shell;

/// <summary>One document of the About window's Licences entry.</summary>
public sealed record LicenceDocument(string Title, string Text);

/// <summary>
/// What the About window and the login screen say about the viewer. The release name is the string table's brand entry,
/// which a build's own branding replaces, so no viewer name is written in code; the version is the build's own.
/// </summary>
public static class AboutText
{
    /// <summary>The release name and version number, for the About window.</summary>
    public static string Release(StringTable strings, ViewerIdentity identity) =>
        strings.Get("about.release", strings.Get("brand.default"), identity.Version);

    /// <summary>The product's name, its version and what this release is, for the login screen.</summary>
    public static string LoginRelease(StringTable strings, ViewerIdentity identity) =>
        strings.Get("login.release", identity.Name, identity.Version, ViewerIdentity.ReleaseLabel);

    /// <summary>Where the source code is published.</summary>
    public const string SourceAddress = "github.com/JohnLegionH/reverie-viewer";

    /// <summary>The About window's text as paragraphs: the name with the release line, the tagline, where the name comes from,
    /// the credits, the licence, support, and the disclaimer with the trademark line.</summary>
    public static IReadOnlyList<string> Paragraphs(StringTable strings, ViewerIdentity identity) =>
    [
        identity.Name + "\n" + Release(strings, identity),
        strings.Get("about.body"),
        strings.Get("about.name"),
        strings.Get("about.ai"),
        strings.Get("about.founders"),
        strings.Get("about.credits"),
        strings.Get("about.licence", identity.Name, SourceAddress),
        strings.Get("about.support"),
        Disclaimer(strings) + " " + strings.Get("about.trademarks"),
    ];

    /// <summary>The About window's whole text, paragraphs separated by an empty line.</summary>
    public static string Full(StringTable strings, ViewerIdentity identity) => string.Join("\n\n", Paragraphs(strings, identity));

    /// <summary>The sentence Linden Lab's third-party viewer policy asks a viewer to show.</summary>
    public static string Disclaimer(StringTable strings) => strings.Get("about.disclaimer");

    /// <summary>The Licences entry: the viewer's licence and the third-party notices (both carried inside this assembly),
    /// then the engine's own licence and notices as the running engine gives them.</summary>
    public static IReadOnlyList<LicenceDocument> Licences(StringTable strings, string engineLicence, string engineNotices) =>
    [
        new(strings.Get("about.licences.viewer"), Embedded("Viewer.Shell.Licences.LICENSE.txt")),
        new(strings.Get("about.licences.notices"), Embedded("Viewer.Shell.Licences.THIRD-PARTY-NOTICES.md")),
        new(strings.Get("about.licences.engine"), engineLicence.TrimEnd() + "\n\n" + engineNotices.Trim() + "\n"),
    ];

    private static string Embedded(string name)
    {
        using var stream = typeof(AboutText).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("missing embedded resource " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
