// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Security.Cryptography;
using Viewer.Core.Platform;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The settings folder follows the viewer's name. On the first start under the new name, the settings kept under the previous
/// name (window places, inventory sort, AO sets, add-on choices, saved logins) are copied across once; the old folder is never
/// changed, and the engine's own caches are not copied (they are rebuilt).
/// </summary>
public class SettingsCarryOverTests
{
    private static string OldFolder()
    {
        string root = TestTemp.NewFolder("carry");
        string old = Path.Combine(root, "NativeViewer");
        Directory.CreateDirectory(Path.Combine(old, "AddonSettings"));
        Directory.CreateDirectory(Path.Combine(old, "shader_cache", "x"));
        Directory.CreateDirectory(Path.Combine(old, "vulkan"));
        File.WriteAllText(Path.Combine(old, "settings.json"), "{\"ui.floater.inventory\":\"10,20,300,400\",\"inventory.sort\":1}");
        File.WriteAllBytes(Path.Combine(old, "remembered-logins.dat"), [1, 2, 3, 4, 5]);
        File.WriteAllText(Path.Combine(old, "addons-state.json"), "{\"ao\":true}");
        File.WriteAllText(Path.Combine(old, "AddonSettings", "ao-sets.json"), "{\"sets\":[\"Test\"]}");
        File.WriteAllText(Path.Combine(old, "shader_cache", "x", "cache.bin"), "engine cache");
        File.WriteAllText(Path.Combine(old, "vulkan", "pipelines.bin"), "engine cache");
        return old;
    }

    private static Dictionary<string, string> Snapshot(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(dir, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    [Fact]
    public void The_first_start_under_the_new_name_copies_the_settings_and_leaves_the_old_folder_as_it_was()
    {
        string old = OldFolder();
        string fresh = Path.Combine(Path.GetDirectoryName(old)!, "ReverieViewer");
        var before = Snapshot(old);

        var result = SettingsCarryOver.Run(old, fresh);

        Assert.Equal(CarryOverOutcome.Copied, result.Outcome);
        Assert.Equal(4, result.Files);
        foreach (var f in new[] { "settings.json", "remembered-logins.dat", "addons-state.json", Path.Combine("AddonSettings", "ao-sets.json") })
            Assert.Equal(before[f], Snapshot(fresh)[f]);
        Assert.False(Directory.Exists(Path.Combine(fresh, "shader_cache")));
        Assert.False(Directory.Exists(Path.Combine(fresh, "vulkan")));
        Assert.Equal(before, Snapshot(old));   // the old folder is untouched
    }

    [Fact]
    public void Nothing_is_copied_when_the_new_folder_already_exists()
    {
        string old = OldFolder();
        string fresh = Path.Combine(Path.GetDirectoryName(old)!, "ReverieViewer");
        Directory.CreateDirectory(fresh);
        File.WriteAllText(Path.Combine(fresh, "settings.json"), "{}");

        var result = SettingsCarryOver.Run(old, fresh);

        Assert.Equal(CarryOverOutcome.NewFolderExists, result.Outcome);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(fresh, "settings.json")));
        Assert.Single(Directory.GetFiles(fresh, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_new_folder_holding_only_the_engines_caches_still_gets_the_settings()
    {
        // the engine makes its user folder (and its shader and pipeline caches in it) before the viewer's code runs
        string old = OldFolder();
        string fresh = Path.Combine(Path.GetDirectoryName(old)!, "ReverieViewer");
        Directory.CreateDirectory(Path.Combine(fresh, "vulkan"));
        File.WriteAllText(Path.Combine(fresh, "vulkan", "new-pipelines.bin"), "the engine's own");

        var result = SettingsCarryOver.Run(old, fresh);

        Assert.Equal(CarryOverOutcome.Copied, result.Outcome);
        Assert.True(File.Exists(Path.Combine(fresh, "settings.json")));
        Assert.True(File.Exists(Path.Combine(fresh, "AddonSettings", "ao-sets.json")));
        Assert.Equal("the engine's own", File.ReadAllText(Path.Combine(fresh, "vulkan", "new-pipelines.bin")));
        Assert.Equal(new[] { "ReverieViewer", "NativeViewer" }.OrderBy(x => x), Directory.GetDirectories(Path.GetDirectoryName(old)!).Select(Path.GetFileName).OrderBy(x => x));
    }

    [Fact]
    public void Nothing_is_copied_when_there_is_no_old_folder()
    {
        string root = TestTemp.NewFolder("carry-none");
        string fresh = Path.Combine(root, "ReverieViewer");

        var result = SettingsCarryOver.Run(Path.Combine(root, "NativeViewer"), fresh);

        Assert.Equal(CarryOverOutcome.NothingToCopy, result.Outcome);
        Assert.False(Directory.Exists(fresh));
    }

    [Fact]
    public void A_copy_that_fails_part_way_leaves_no_half_folder_behind_and_is_tried_again_next_start()
    {
        string old = OldFolder();
        string fresh = Path.Combine(Path.GetDirectoryName(old)!, "ReverieViewer");
        using (File.Open(Path.Combine(old, "addons-state.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))   // locked: the copy cannot read it
        {
            var failed = SettingsCarryOver.Run(old, fresh);
            Assert.Equal(CarryOverOutcome.Failed, failed.Outcome);
            Assert.False(Directory.Exists(fresh));
        }

        var retried = SettingsCarryOver.Run(old, fresh);
        Assert.Equal(CarryOverOutcome.Copied, retried.Outcome);
        Assert.True(File.Exists(Path.Combine(fresh, "addons-state.json")));
    }
}
