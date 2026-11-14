// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Viewer.Core.Identity;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// What the About window and the login screen say about the viewer: its release name (from the string table's brand entry,
/// which a build's branding replaces, never a name written in code) and version, the disclaimer the third-party viewer
/// policy asks for, and the Licences entry (the viewer's licence, the third-party notices and the engine's own text).
/// </summary>
public class AboutTextTests
{
    private static readonly ViewerIdentity Identity = new("ExampleViewer", "ExampleViewer Test", "2.3.4");

    [Fact]
    public void The_release_line_is_the_brand_name_and_the_version()
    {
        var en = StringTable.English();
        Assert.Equal($"{en.Get("brand.default")} 2.3.4", AboutText.Release(en, Identity));
    }

    [Fact]
    public void The_release_name_comes_from_the_string_table()
    {
        var pseudo = StringTable.Pseudo();
        var line = AboutText.Release(pseudo, Identity);
        Assert.Contains(pseudo.Get("brand.default"), line);
        Assert.NotEqual(AboutText.Release(StringTable.English(), Identity), line);
    }

    [Fact]
    public void The_disclaimer_is_the_policy_sentence()
    {
        Assert.Equal("This software is not provided or supported by Linden Lab, the makers of Second Life.", AboutText.Disclaimer(StringTable.English()));
    }

    [Fact]
    public void The_licences_entry_holds_the_viewer_licence_the_notices_and_the_engine_text()
    {
        var docs = AboutText.Licences(StringTable.English(), "ENGINE LICENCE TEXT", "ENGINE NOTICES");
        Assert.Equal(3, docs.Count);
        Assert.Contains("GNU LESSER GENERAL PUBLIC LICENSE", docs[0].Text);
        Assert.Contains("Version 2.1, February 1999", docs[0].Text);
        Assert.Contains("# Third-party notices", docs[1].Text);
        Assert.Contains("Avatar data folder", docs[1].Text);
        Assert.Contains("ENGINE LICENCE TEXT", docs[2].Text);
        Assert.Contains("ENGINE NOTICES", docs[2].Text);
        Assert.All(docs, d => Assert.False(string.IsNullOrWhiteSpace(d.Title)));
    }

    [Fact]
    public void The_shell_has_a_Licences_floater_its_command_opens()
    {
        var shell = new ShellController(StringTable.English(), () => { });
        Assert.Contains(shell.Floaters.Specs, s => s.Id == ShellController.LicencesFloaterId && s.TitleKey == "about.licences.title");
        Assert.True(shell.Commands.Has("floater.licences"));
        Assert.True(shell.Commands.Execute("floater.licences"));
        Assert.True(shell.Floaters.IsOpen(ShellController.LicencesFloaterId));
    }

    [Fact]
    public void The_new_strings_are_in_the_English_table()
    {
        var en = StringTable.English();
        foreach (var key in new[] { "about.release", "about.disclaimer", "about.licences", "about.licences.title", "about.licences.viewer", "about.licences.notices", "about.licences.engine", "login.release" })
            Assert.True(en.Has(key), key);
    }
}
