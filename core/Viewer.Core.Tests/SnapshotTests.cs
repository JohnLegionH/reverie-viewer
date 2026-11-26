// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Viewer.Shell.Snapshots;
using Xunit;

namespace Viewer.Core.Tests;

public sealed class SnapshotTests
{
    private static string NewFolder() => TestTemp.NewFolderPath("snapshot-test");

    [Fact]
    public void The_official_shortcut_belongs_to_the_snapshot_entry()
    {
        Assert.Equal("control|shift|S", ViewerShortcuts.ByEntry.GetValueOrDefault("world.snapshot"));
        Assert.DoesNotContain(ViewerShortcuts.NotBuilt, s => s.Shortcut == "control|shift|S");
        Assert.Contains(ViewerShortcuts.NotBuilt, s => s.Shortcut == "control|grave");
    }

    [Fact]
    public void The_defaults_are_the_window_size_png_and_the_interface_hidden()
    {
        var settings = new SettingsService(null);
        Assert.False(settings.Get(Settings.SnapshotInterface));
        Assert.Equal((int)SnapshotFormat.Png, settings.Get(Settings.SnapshotFileFormat));
        Assert.Equal(0, settings.Get(Settings.SnapshotSizeChoice));
        Assert.Equal(75, settings.Get(Settings.SnapshotJpegQuality));
        Assert.Null(settings.GetText(Settings.SnapshotFolderKey));
    }

    [Fact]
    public void The_window_size_comes_first_and_the_fixed_sizes_grow()
    {
        Assert.True(SnapshotModel.Sizes[0].IsWindow);
        Assert.Equal([(1920, 1080), (2560, 1440), (3840, 2160), (5120, 2880), (7680, 4320)],
            SnapshotModel.Sizes.Skip(1).Select(s => (s.Width, s.Height)));
        Assert.Equal((1280, 720), SnapshotModel.Resolve(SnapshotModel.Sizes[0], 1280, 720));
        Assert.Equal((3840, 2160), SnapshotModel.Resolve(new SnapshotSize(3840, 2160), 1280, 720));
    }

    [Fact]
    public void A_size_beyond_the_graphics_card_is_refused()
    {
        Assert.False(SnapshotModel.Allowed(new SnapshotSize(7680, 4320), 4096));
        Assert.True(SnapshotModel.Allowed(new SnapshotSize(3840, 2160), 4096));
        Assert.True(SnapshotModel.Allowed(SnapshotModel.Sizes[0], 4096));
    }

    // the engine's rule in its viewport stretch mode: what is drawn is the content size divided by the content scale factor, rounded down
    [Theory]
    [InlineData(0.75f)]
    [InlineData(1f)]
    [InlineData(1.1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void A_chosen_size_is_drawn_at_that_size_at_any_interface_size(float interfaceScale)
    {
        foreach (var size in SnapshotModel.Sizes.Skip(1))
        {
            var scale = SnapshotModel.ContentScale(size.Width, size.Height, interfaceScale, 648);
            Assert.Equal((size.Width, size.Height), ((int)MathF.Floor(scale.Width / scale.Factor), (int)MathF.Floor(scale.Height / scale.Factor)));
        }
    }

    [Fact]
    public void The_capture_sets_the_content_scale_the_rule_gives()
    {
        Assert.Contains("SnapshotModel.ContentScale(width, height", Host());
        Assert.Contains("root.ContentScaleFactor = scale.Factor", Host());
    }

    [Fact]
    public void The_default_folder_is_reverie_under_pictures() =>
        Assert.Equal(Path.Combine("pictures", "Reverie"), SnapshotModel.DefaultFolder("pictures"));

    [Fact]
    public void Saved_files_are_numbered_across_both_formats()
    {
        string folder = NewFolder();
        Assert.Equal("Snapshot_001.png", Path.GetFileName(SnapshotModel.Save(folder, SnapshotFormat.Png, [1])));
        Assert.Equal("Snapshot_002.jpg", Path.GetFileName(SnapshotModel.Save(folder, SnapshotFormat.Jpeg, [2])));
        File.WriteAllBytes(Path.Combine(folder, "Snapshot_007.png"), [7]);
        Assert.Equal("Snapshot_008.png", Path.GetFileName(SnapshotModel.Save(folder, SnapshotFormat.Png, [8])));
    }

    [Fact]
    public void A_write_that_fails_is_reported_once_and_leaves_no_file()
    {
        string folder = NewFolder();
        int writes = 0;
        var thrown = Record.Exception(() => SnapshotModel.Save(folder, SnapshotFormat.Png, _ =>
        {
            if (++writes > 3) throw new InvalidOperationException("tried again and again");
            throw new IOException("disk full");
        }));
        Assert.Equal(1, writes);
        Assert.IsType<IOException>(thrown);
        Assert.Empty(Directory.GetFiles(folder));
    }

    private static string Host() => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot", "Main.Snapshot.cs"));

    [Fact]
    public void A_picture_without_the_interface_leaves_out_ban_lines_and_is_not_taken_under_a_dialog()
    {
        Assert.Contains("_hoverText, _handles, _banWall", Host());
        Assert.Contains("GetEmbeddedSubwindows()", Host());
    }

    [Fact]
    public void The_preview_picture_is_let_go_and_a_session_s_end_releases_the_snapshot()
    {
        Assert.Contains("_snapshotPreview?.Dispose()", Host());
        Assert.Contains("ReleaseSnapshot();", File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot", "Main.cs")));
    }

    [Fact]
    public void The_window_is_added_once_the_world_menu_is_there()
    {
        string host = Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot");
        string travel = File.ReadAllText(Path.Combine(host, "Main.Travel.cs"));
        int menu = travel.IndexOf("TravelFeature.Install(", StringComparison.Ordinal);
        Assert.True(menu >= 0 && travel.IndexOf("InstallSnapshot();", StringComparison.Ordinal) > menu, "added after the travel windows make the World menu");
        Assert.DoesNotContain("InstallSnapshot();", File.ReadAllText(Path.Combine(host, "Main.cs")));
    }

    [Fact]
    public void The_automation_server_lets_the_snapshot_command_through()
    {
        string server = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Automation", "AutomationServer.cs"));
        string list = server[server.IndexOf("string[] Commands = [", StringComparison.Ordinal)..];
        Assert.Contains("\"snapshot\"", list[..list.IndexOf(']', list.IndexOf("= [", StringComparison.Ordinal))]);
    }

    [Fact]
    public void A_save_never_overwrites_a_file()
    {
        string folder = NewFolder();
        Directory.CreateDirectory(folder);
        string kept = Path.Combine(folder, "Snapshot_001.png");
        File.WriteAllBytes(kept, [42]);
        string saved = SnapshotModel.Save(folder, SnapshotFormat.Png, [1, 2]);
        Assert.NotEqual(kept, saved);
        Assert.Equal([(byte)42], File.ReadAllBytes(kept));
        Assert.True(File.Exists(saved));
    }
}
