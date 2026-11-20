// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Appearance;
using Viewer.Core.Avatars;
using Viewer.Core.Inventory;
using Viewer.Shell.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Three small editor behaviours of the official viewer: a new item waits to be renamed (LLFolderView needsAutoRename), a wearable's texture
/// slots start at the type's default texture (LLViewerWearable::setTexturesToDefaults) and the gesture editor lists the built-in animations
/// (LLPreviewGesture::addAnimations). Pure, runs in parallel.
/// </summary>
public class EditorSmallItemsTests
{
    private static readonly Guid White = Guid.Parse("5748decc-f629-461c-9a36-a35a221fe21f");
    private static readonly Guid DefaultAvatar = Guid.Parse("c228d1cf-4b5d-4ba8-84f4-899a0796aa97");

    // ---- rename a new item ----

    [Fact]
    public void A_new_item_is_taken_for_renaming_once_its_row_is_shown()
    {
        var rename = new AutoRename();
        var item = Guid.NewGuid();
        var t0 = DateTimeOffset.UnixEpoch;
        rename.Want(item, t0);
        Assert.Equal(Guid.Empty, rename.Take([Guid.NewGuid()], t0.AddSeconds(1)));   // not listed yet: keep waiting
        Assert.Equal(item, rename.Take([Guid.NewGuid(), item], t0.AddSeconds(2)));
        Assert.Equal(Guid.Empty, rename.Take([item], t0.AddSeconds(3)));             // once only
    }

    [Fact]
    public void A_new_item_that_never_shows_stops_waiting()
    {
        var rename = new AutoRename();
        var item = Guid.NewGuid();
        var t0 = DateTimeOffset.UnixEpoch;
        rename.Want(item, t0);
        Assert.Equal(Guid.Empty, rename.Take([], t0.AddSeconds(AutoRename.GiveUpSeconds + 1)));
        Assert.Equal(Guid.Empty, rename.Take([item], t0.AddSeconds(AutoRename.GiveUpSeconds + 2)));
    }

    [Fact]
    public void A_second_new_item_replaces_the_first()
    {
        var rename = new AutoRename();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var t0 = DateTimeOffset.UnixEpoch;
        rename.Want(a, t0);
        rename.Want(b, t0);
        Assert.Equal(Guid.Empty, rename.Take([a], t0));
        Assert.Equal(b, rename.Take([a, b], t0));
    }

    // ---- default texture slots ----

    [Theory]
    [InlineData(WearableType.Shirt, 1, "5748decc-f629-461c-9a36-a35a221fe21f")]
    [InlineData(WearableType.Pants, 2, "5748decc-f629-461c-9a36-a35a221fe21f")]
    [InlineData(WearableType.Shoes, 7, "5748decc-f629-461c-9a36-a35a221fe21f")]
    [InlineData(WearableType.Socks, 12, "5748decc-f629-461c-9a36-a35a221fe21f")]
    [InlineData(WearableType.Jacket, 13, "5748decc-f629-461c-9a36-a35a221fe21f")]
    [InlineData(WearableType.Jacket, 14, "5748decc-f629-461c-9a36-a35a221fe21f")]
    [InlineData(WearableType.Gloves, 15, "5748decc-f629-461c-9a36-a35a221fe21f")]
    [InlineData(WearableType.Undershirt, 16, "5748decc-f629-461c-9a36-a35a221fe21f")]
    [InlineData(WearableType.Underpants, 17, "5748decc-f629-461c-9a36-a35a221fe21f")]
    [InlineData(WearableType.Skirt, 18, "5748decc-f629-461c-9a36-a35a221fe21f")]
    [InlineData(WearableType.Alpha, 21, "5748decc-f629-461c-9a36-a35a221fe21f")]
    [InlineData(WearableType.Alpha, 25, "5748decc-f629-461c-9a36-a35a221fe21f")]
    [InlineData(WearableType.Eyes, 3, "6522e74d-1660-4e7f-b601-6f48c1659a77")]
    [InlineData(WearableType.Hair, 4, "7ca39b4c-bd19-4699-aff7-f93fd03d3e7b")]
    [InlineData(WearableType.Skin, 0, "c228d1cf-4b5d-4ba8-84f4-899a0796aa97")]
    [InlineData(WearableType.Skin, 6, "c228d1cf-4b5d-4ba8-84f4-899a0796aa97")]
    [InlineData(WearableType.Tattoo, 26, "c228d1cf-4b5d-4ba8-84f4-899a0796aa97")]
    [InlineData(WearableType.Universal, 39, "c228d1cf-4b5d-4ba8-84f4-899a0796aa97")]
    public void A_slot_defaults_to_the_texture_the_official_table_gives_it(WearableType type, int slot, string expected)
    {
        // llavatarappearancedefines.cpp (the slot's default image setting) and app_settings/settings.xml (UIImgDefault*UUID)
        Assert.Equal(Guid.Parse(expected), WearableKinds.DefaultTexture(type, slot));
    }

    [Fact]
    public void Every_slot_of_every_type_has_a_default_and_other_slots_have_none()
    {
        foreach (WearableType t in Enum.GetValues<WearableType>())
            foreach (var s in WearableKinds.Slots(t))
                Assert.NotEqual(Guid.Empty, WearableKinds.DefaultTexture(t, s.Index));
        Assert.Equal(Guid.Empty, WearableKinds.DefaultTexture(WearableType.Shirt, 2));   // pants' slot, not a shirt's
        Assert.Equal(Guid.Empty, WearableKinds.DefaultTexture(WearableType.Shape, 0));
    }

    [Fact]
    public void A_new_wearable_file_writes_every_texture_slot_at_its_default()
    {
        var catalog = EditFixture.Catalog();
        foreach (WearableType t in new[] { WearableType.Shirt, WearableType.Jacket, WearableType.Eyes, WearableType.Skin, WearableType.Universal })
        {
            byte[] file = NewItemCatalog.DefaultWearable(t, "New", catalog);
            Assert.True(WearableAsset.TryParse(file, out var asset, out string error), error);
            Assert.Equal(WearableKinds.Slots(t).Select(s => s.Index).OrderBy(i => i), asset.Textures.Keys);
            foreach (var s in WearableKinds.Slots(t))
                Assert.Equal(WearableKinds.DefaultTexture(t, s.Index), asset.Textures[s.Index]);
        }
        Assert.True(WearableAsset.TryParse(NewItemCatalog.DefaultWearable(WearableType.Shape, "New", catalog), out var shape, out _));
        Assert.Empty(shape.Textures);
    }

    [Fact]
    public void The_editor_resets_a_slot_to_its_default_and_says_whether_it_is_there()
    {
        var saved = new WearableAsset { Type = WearableType.Shirt };
        var other = Guid.NewGuid();
        saved.Textures[1] = other;
        var e = new WearableEditor(saved, EditFixture.Catalog(), true);
        Assert.False(e.IsDefaultTexture(1));
        Assert.True(e.ResetTexture(1));
        Assert.Equal(White, e.Texture(1));
        Assert.True(e.IsDefaultTexture(1));
        Assert.True(e.IsDirty);
        Assert.False(e.ResetTexture(1));   // already there: nothing to change
        e.Revert();
        Assert.Equal(other, e.Texture(1));
    }

    [Fact]
    public void An_empty_slot_is_not_at_its_default_and_a_read_only_item_does_not_reset()
    {
        var skin = new WearableEditor(new WearableAsset { Type = WearableType.Skin }, EditFixture.Catalog(), true);
        Assert.False(skin.IsDefaultTexture(0));
        Assert.True(skin.ResetTexture(0));
        Assert.Equal(DefaultAvatar, skin.Texture(0));
        var locked = new WearableEditor(new WearableAsset { Type = WearableType.Shirt }, EditFixture.Catalog(), false);
        Assert.False(locked.ResetTexture(1));
        Assert.Equal(Guid.Empty, locked.Texture(1));
    }

    // ---- built-in animations ----

    [Fact]
    public void The_built_in_list_is_the_official_user_animation_states_in_order()
    {
        // llanimationstates.cpp gUserAnimStates: 69 entries; labels from strings.xml (anim_<name>)
        Assert.Equal(69, BuiltInAnimations.All.Count);
        Assert.Equal(("express_afraid", "Afraid", Guid.Parse("6b61c8e8-4747-0d75-12d7-e49ff207a4ca")), Row(0));
        Assert.Equal("express_anger", BuiltInAnimations.All[1].Name);
        Assert.Equal("away", BuiltInAnimations.All[2].Name);
        Assert.Equal("yes_head", BuiltInAnimations.All[^1].Name);
        Assert.Equal(BuiltInAnimations.All.Count, BuiltInAnimations.All.Select(a => a.Id).Distinct().Count());
        Assert.DoesNotContain(BuiltInAnimations.All, a => a.Id == Guid.Empty || a.Label.Length == 0);
    }

    private static (string, string, Guid) Row(int i) => (BuiltInAnimations.All[i].Name, BuiltInAnimations.All[i].Label, BuiltInAnimations.All[i].Id);

    [Fact]
    public void The_gesture_editor_offers_the_built_ins_first_then_the_inventory_animations_by_name()
    {
        var mine = new List<(Guid Id, string Name)> { (Guid.NewGuid(), "Zed"), (Guid.NewGuid(), "alpha") };
        var choices = BuiltInAnimations.Choices(mine);
        Assert.Equal(69 + 2, choices.Count);
        Assert.Equal("Afraid", choices[0].Name);
        Assert.Equal(BuiltInAnimations.All[68].Label, choices[68].Name);
        Assert.Equal(new[] { "alpha", "Zed" }, choices.Skip(69).Select(c => c.Name).ToArray());
    }

    [Fact]
    public void A_built_in_animation_is_found_by_its_id()
    {
        Assert.Equal("Afraid", BuiltInAnimations.LabelOf(Guid.Parse("6b61c8e8-4747-0d75-12d7-e49ff207a4ca")));
        Assert.Null(BuiltInAnimations.LabelOf(Guid.NewGuid()));
    }
}
