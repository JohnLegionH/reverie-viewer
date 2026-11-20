// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Viewer.Shell.Appearance;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>A wearable's texture slot shows the chosen texture as a picture, not its id as text. Pure and file reads; runs in parallel.</summary>
public class TextureSlotLookTests
{
    private static readonly StringTable S = StringTable.English();

    [Fact]
    public void A_slot_with_no_texture_shows_no_swatch_and_says_it_is_not_set()
    {
        var look = TextureSlotLook.Of(Guid.Empty);
        Assert.False(look.ShowsSwatch);
        Assert.Equal("appearance.texture.empty", look.ButtonKey);
        Assert.Equal("Not set", S.Get(look.ButtonKey));
    }

    [Fact]
    public void A_slot_with_a_texture_shows_its_swatch_and_a_button_to_change_it()
    {
        var asset = Guid.Parse("5b9a0c3e-0000-4000-8000-000000000001");
        var look = TextureSlotLook.Of(asset);
        Assert.True(look.ShowsSwatch);
        Assert.Equal("appearance.texture.change", look.ButtonKey);
        Assert.True(S.Has(look.ButtonKey));
        Assert.DoesNotContain("5b9a0c3e", S.Get(look.ButtonKey));
    }

    [Fact]
    public void The_Appearance_window_never_writes_a_texture_id_into_a_slot_button()
    {
        string source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot", "AppearancePanels.cs"));
        Assert.DoesNotContain("ToString(\"N\")[..8]", source);
        Assert.Contains("TextureSlotLook.Of(", source);
    }
}
