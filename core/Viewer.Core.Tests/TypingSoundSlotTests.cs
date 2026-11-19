// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell.Sound;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Every typing sound, the own avatar's (interface sounds) and other avatars' (world sounds), plays the one typing slot, so a change
/// of the typing sound's file changes all of them. A source scan: only the catalog names the slot's file and grid id. Reads
/// committed source only, so the class runs in parallel.
/// </summary>
public class TypingSoundSlotTests
{
    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Viewer.sln")))
                return dir.FullName;
        throw new InvalidOperationException("no repository root above the test");
    }

    [Fact]
    public void Only_the_catalog_and_the_generator_name_the_typing_sounds_file_or_grid_id()
    {
        var info = UiSoundCatalog.Of(UiSoundSlot.Typing);
        string[] allowed = ["UiSoundCatalog.cs", "UiSoundSynth.cs"];   // the catalog's row; the generator's recipe for the same file
        var hits = Directory.EnumerateFiles(Path.Combine(Root(), "host"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                return text.Contains(info.File, StringComparison.Ordinal) || text.Contains(info.GridId.ToString(), StringComparison.OrdinalIgnoreCase);
            })
            .Select(Path.GetFileName)
            .Where(n => !allowed.Contains(n))
            .ToArray();
        Assert.Empty(hits);
    }

    [Fact]
    public void Both_typing_paths_read_the_typing_slot()
    {
        string wiring = File.ReadAllText(Path.Combine(Root(), "host/Viewer.Shell/Sound/UiSoundWiring.cs"));
        string others = File.ReadAllText(Path.Combine(Root(), "host/Viewer.Godot/Main.WorldSounds.cs"));
        Assert.Contains("sounds.Play(UiSoundSlot.Typing)", wiring);                    // the own avatar's
        Assert.Contains("UiSoundCatalog.Of(UiSoundSlot.Typing)", others);              // other avatars'
    }
}
