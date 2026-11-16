// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Viewer.Core.Identity;
using Viewer.Core.Platform;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The viewer's name comes from one place, the core's identity: what a grid is told at login, the per-user data folders,
/// the string table's brand entry, the export and package names, the About window and the login screen's release line.
/// </summary>
public class ReleaseNameTests
{
    [Fact]
    public void The_identity_names_the_viewer_and_its_release()
    {
        Assert.Equal("Reverie Viewer", ViewerIdentity.ProductName);
        Assert.Equal("Reverie", ViewerIdentity.ShortName);
        Assert.Equal("ReverieViewer", ViewerIdentity.DataFolderName);
        Assert.Equal("NativeViewer", ViewerIdentity.PreviousDataFolderName);
        Assert.Equal("reverie-viewer", ViewerIdentity.PackageName);
        Assert.Equal("Reverie", ViewerIdentity.ExecutableName);
        Assert.Equal("early preview", ViewerIdentity.ReleaseLabel);
    }

    [Fact]
    public void A_grid_is_told_Reverie_Viewer_and_its_version()
    {
        var id = ViewerIdentity.Current;
        Assert.Equal("Reverie Viewer", id.Name);
        Assert.Equal("Reverie Viewer", id.Channel);
        Assert.Equal("0.1.1", id.Version);
        Assert.Equal("ReverieViewer/0.1.1", id.UserAgent);
    }

    [Fact]
    public void The_data_folders_follow_the_name()
    {
        var env = new Dictionary<string, string?> { ["HOME"] = "/home/u" };
        var dirs = DataDirectories.For(DataPlatform.Linux, k => env.GetValueOrDefault(k), ViewerIdentity.DataFolderName);
        Assert.Equal("/home/u/.config/ReverieViewer", dirs.Settings);
        var defaults = typeof(DataDirectories).GetMethod(nameof(DataDirectories.ForCurrentUser))!.GetParameters()[0].DefaultValue;
        Assert.Equal("ReverieViewer", defaults);
    }

    [Fact]
    public void The_string_tables_brand_is_the_identitys_short_name()
    {
        Assert.Equal(ViewerIdentity.ShortName, StringTable.English().Get("brand.default"));
        Assert.Equal(ViewerIdentity.ShortName, BrandText.For(null, StringTable.English()));
    }

    [Fact]
    public void The_login_screen_shows_the_product_its_version_and_the_release_label()
    {
        Assert.Equal("Reverie Viewer 0.1.1 (early preview)", AboutText.LoginRelease(StringTable.English(), ViewerIdentity.Current));
    }

    [Fact]
    public void The_About_window_shows_the_whole_text()
    {
        var expected = string.Join("\n\n",
            "Reverie Viewer\nReverie 0.1.1",
            "Your dreams, in view.",
            "A reverie is a daydream, and a virtual world is a daydream we share. Reverie Viewer is a modern, open-source viewer for OpenSimulator grids and Second Life: familiar where it should be, better where it can be.",
            "The name also nods to the AI it was built with. \"Reverie\" was the simulation at the heart of the Generative Agents study, where AI agents lived out their days in a small virtual town. Reverie Viewer was built with AI, and AI agents helped test it.",
            "Reverie comes from one of the original Founders of InWorldz, whose motto was \"Where your dreams are our vision.\" This viewer carries that idea forward to the grids that came after.",
            "Created by Legion Builds and contributors. Includes code from the Second Life viewer by Linden Research, Inc. and from other open-source projects; see Licences for each project and its notice.",
            "Reverie Viewer is free software under the GNU Lesser General Public License, version 2.1. Source code: github.com/JohnLegionH/reverie-viewer",
            "Support is best effort: report problems on the GitHub issues page.",
            "This software is not provided or supported by Linden Lab, the makers of Second Life. Second Life and Linden Lab are trademarks of Linden Research, Inc.");
        Assert.Equal(expected, AboutText.Full(StringTable.English(), ViewerIdentity.Current));
    }

    [Fact]
    public void The_exports_are_named_after_the_viewer()
    {
        var presets = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot", "export_presets.cfg"));
        var paths = Regex.Matches(presets, "export_path=\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(["build/Reverie.exe", "build/dev/Reverie.exe", "build/linux-x86_64/Reverie.x86_64", "build/linux-arm64/Reverie.arm64"], paths);
        var project = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot", "project.godot"));
        Assert.Contains("config/name=\"Reverie\"", project);
        Assert.Contains("config/custom_user_dir_name.shipping=\"ReverieViewer\"", project);
    }

    [Fact]
    public void The_packages_are_named_after_the_viewer()
    {
        var script = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "tools", "package.ps1"));
        Assert.Contains("const string PackageName = ", script);   // the regex that reads the name from the identity's source
        Assert.Contains("Exe = \"Reverie.exe\"", script);
        Assert.Contains("Exe = \"Reverie.x86_64\"", script);
        Assert.Contains("Exe = \"Reverie.arm64\"", script);
        Assert.DoesNotContain("brand\\.default", script);
    }

    [Fact]
    public void No_working_or_placeholder_name_is_left_in_user_facing_text()
    {
        var en = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Shell", "Strings", "en.json"));
        foreach (var word in new[] { "Raccoon", "Possum", "NativeViewer", "Native Viewer", "A native viewer" })
            Assert.DoesNotContain(word, en, StringComparison.OrdinalIgnoreCase);
        foreach (var file in new[] { "FIRST-RUN-windows.txt", "FIRST-RUN-linux.txt" })
        {
            var text = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "tools", "release", file));
            Assert.DoesNotContain("Viewer.exe", text);
            Assert.DoesNotContain("Viewer.x86_64", text);
            Assert.DoesNotContain("Viewer.arm64", text);
            Assert.DoesNotContain("NativeViewer", text);
        }
    }
}
