// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The engine's on-disk shader cache and pipeline cache are on in the shipping export, so a second start loads what the first compiled,
/// and off everywhere else (development exports, and test renders through their launch folder's override.cfg). The settings sit in the
/// host project's project.godot; the shipping export is the one that carries the "shipping" feature. Reads a file only, so the class runs in parallel.
/// </summary>
public class EngineCacheSettingsTests
{
    private static readonly string[] Caches = ["shader_compiler/shader_cache/enabled", "rendering_device/pipeline_cache/enable"];

    private static string ProjectFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Viewer.sln"))) dir = dir.Parent;
        string root = dir?.FullName ?? throw new InvalidOperationException("repo root not found");
        return File.ReadAllText(Path.Combine(root, "host", "Viewer.Godot", "project.godot"));
    }

    /// <summary>The value a project file gives a setting under a feature (null: no such line). A line is name=value, ';' starts a comment.</summary>
    internal static string? ValueOf(string project, string name, string? feature = null)
    {
        string key = feature is null ? name : name + "." + feature;
        foreach (var raw in project.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith(';') || !line.StartsWith(key + "=", StringComparison.Ordinal)) continue;
            return line[(key.Length + 1)..].Trim();
        }
        return null;
    }

    /// <summary>Whether a shipping export of this project file would run with the setting on.</summary>
    internal static bool OnInShipping(string project, string name) =>
        (ValueOf(project, name, "shipping") ?? ValueOf(project, name)) == "true";

    [Fact]
    public void The_shipping_export_keeps_the_shader_cache_and_the_pipeline_cache()
    {
        string project = ProjectFile();
        foreach (var name in Caches) Assert.True(OnInShipping(project, name), name + " is not on in the shipping export");
    }

    [Fact]
    public void Everything_but_the_shipping_export_leaves_both_caches_off()
    {
        string project = ProjectFile();
        foreach (var name in Caches) Assert.NotEqual("true", ValueOf(project, name));
    }

    [Fact]
    public void The_shipping_export_keeps_the_engines_caches_in_the_viewers_own_roaming_folder()
    {
        // The engine writes its shader cache and pipeline cache under its user data folder, and 4.7.2 has no setting for the cache path itself.
        // The shipping export names that folder after the product (%APPDATA%\<product>), where the viewer's settings already live; without it
        // the caches go to %APPDATA%\Godot\app_userdata\<project name>.
        string project = ProjectFile();
        Assert.Equal("true", ValueOf(project, "config/use_custom_user_dir", "shipping"));
        Assert.Equal("\"" + Viewer.Core.Identity.ViewerIdentity.DataFolderName + "\"", ValueOf(project, "config/custom_user_dir_name", "shipping"));
    }

    [Fact]
    public void Everything_but_the_shipping_export_keeps_the_engines_default_user_folder()
    {
        string project = ProjectFile();
        Assert.NotEqual("true", ValueOf(project, "config/use_custom_user_dir"));
        Assert.Null(ValueOf(project, "config/custom_user_dir_name"));
    }

    [Fact]
    public void The_check_reads_a_project_file_that_leaves_the_caches_off()
    {
        // the negative control: the file as it was before the shipping lines (both caches off for every export) must be refused
        const string before = "[rendering]\nshader_compiler/shader_cache/enabled=false\nrendering_device/pipeline_cache/enable=false\n";
        foreach (var name in Caches) Assert.False(OnInShipping(before, name));
        // a commented line counts for nothing
        Assert.False(OnInShipping("; shader_compiler/shader_cache/enabled.shipping=true\n", Caches[0]));
    }
}
