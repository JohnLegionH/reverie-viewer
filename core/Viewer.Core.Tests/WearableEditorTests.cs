// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Immutable;
using System.Text;
using Viewer.Core.Appearance;
using Viewer.Core.Inventory;
using Viewer.Core.Avatars;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>A small avatar definition written for these tests (its own words and numbers), shaped like the real one's parameter elements.</summary>
internal static class EditFixture
{
    public const string Lad = """
        <linden_avatar version="2.0" wearable_definition_version="22">
          <param id="33" group="0" wearable="shape" edit_group="shape_body" edit_group_order="2" name="Body_Height" label="Height" label_min="Short" label_max="Tall" value_min="-1" value_max="1" value_default="0"/>
          <param id="34" group="0" wearable="shape" edit_group="shape_body" edit_group_order="1" name="Body_Thick" label_min="Thin" label_max="Thick" value_min="0" value_max="2" value_default="0.5"/>
          <param id="35" group="1" wearable="shape" edit_group="shape_body" edit_group_order="3" name="Hidden_Body" label_min="a" label_max="b" value_min="0" value_max="1"/>
          <param id="36" group="2" wearable="shape" edit_group="shape_body" edit_group_order="4" name="Local_Only" label_min="a" label_max="b" value_min="0" value_max="1"/>
          <param id="40" group="0" wearable="shape" edit_group="shape_head" edit_group_order="1" name="Head_Size" label="Head size" label_min="Small" label_max="Big" value_min="0" value_max="1" value_default="0.5"/>
          <param id="50" group="0" wearable="shape" edit_group="shape_legs" edit_group_order="1" name="Leg_Length" label_min="Short" label_max="Long" value_min="-1" value_max="1" value_default="0">
            <param_driver>
              <driven id="51" min1="0" max1="1"/>
              <driven id="52" min1="-1" max1="0" max2="0" min2="0"/>
            </param_driver>
          </param>
          <param id="51" group="1" wearable="shape" name="Leg_Driven_Up" value_min="0" value_max="2" value_default="0"/>
          <param id="52" group="1" wearable="shape" name="Leg_Driven_Down" value_min="0" value_max="1" value_default="0"/>
          <param id="80" group="0" wearable="shape" name="male" value_min="0" value_max="1" value_default="0"/>
          <param id="600" group="0" wearable="hair" edit_group="hair_color" edit_group_order="1" name="Hair_Tone" label_min="Light" label_max="Dark" value_min="0" value_max="1" value_default="0.4"/>
          <param id="601" group="0" wearable="hair" edit_group="hair_facial" edit_group_order="1" sex="male" name="Beard" label_min="None" label_max="Full" value_min="0" value_max="1" value_default="0"/>
          <param id="602" group="0" wearable="hair" edit_group="hair_style" edit_group_order="1" name="Hair_Volume" label_min="Flat" label_max="Big" value_min="0" value_max="1" value_default="0.5"/>
          <param id="700" group="0" wearable="physics" edit_group="physics_breasts_updown" edit_group_order="1" sex="female" name="Bounce" label_min="Less" label_max="More" value_min="0" value_max="1" value_default="0.5"/>
          <param id="803" group="0" wearable="shirt" edit_group="colorpicker" name="shirt_red" value_min="0" value_max="1" value_default="1"/>
          <param id="804" group="0" wearable="shirt" edit_group="colorpicker" name="shirt_green" value_min="0" value_max="1" value_default="1"/>
          <param id="805" group="0" wearable="shirt" edit_group="colorpicker" name="shirt_blue" value_min="0" value_max="1" value_default="1"/>
          <param id="840" group="0" wearable="shirt" edit_group="shirt" edit_group_order="6" name="Sleeve_Flair" label="Sleeve Looseness" label_min="Tight" label_max="Loose" value_min="0" value_max="1.5" value_default="0"/>
          <param id="841" group="0" wearable="shirt" edit_group="shirt" edit_group_order="2" name="Shirt_Length" label_min="Short" label_max="Long" value_min="0" value_max="1" value_default="0.8"/>
        </linden_avatar>
        """;

    public static EditParamCatalog Catalog() => EditParamCatalog.Parse(Encoding.UTF8.GetBytes(Lad));

    public static readonly Guid Fabric = Guid.Parse("5b9a0c3e-6a11-4a52-9c2e-0d74b3f1a801");
    public static readonly Guid Other = Guid.Parse("c3d1e8f0-27a4-4b6d-8e55-61f9a2b04c77");

    public static WearableAsset Shirt(bool withTexture = true)
    {
        var a = new WearableAsset { Name = "Plain shirt", Description = "d", Type = WearableType.Shirt, PermissionsLines = ["\tpermissions 0", "\t{", "\t}"], SaleInfoLines = ["\tsale_info\t0", "\t{", "\t}"] };
        a.Params[803] = 1f; a.Params[804] = 0.5f; a.Params[805] = 0.25f; a.Params[840] = 0f; a.Params[841] = 0.8f;
        if (withTexture) a.Textures[1] = Fabric;
        return a;
    }

    public static WearableAsset Shape()
    {
        var a = new WearableAsset { Name = "My shape", Type = WearableType.Shape, PermissionsLines = ["\tpermissions 0", "\t{", "\t}"], SaleInfoLines = ["\tsale_info\t0", "\t{", "\t}"] };
        foreach (var (id, w) in new (int, float)[] { (33, 0f), (34, 0.5f), (35, 0f), (36, 0f), (40, 0.5f), (50, 0f), (51, 0f), (52, 0f), (80, 0f) }) a.Params[id] = w;
        return a;
    }
}

public class EditParamCatalogTests
{
    private static readonly EditParamCatalog Cat = EditFixture.Catalog();

    [Fact]
    public void A_shape_panel_lists_its_tabs_in_the_official_order_and_leaves_out_tabs_with_no_sliders()
    {
        var tabs = Cat.Subparts(WearableType.Shape, male: false).Select(t => t.EditGroup).ToArray();
        Assert.Equal(new[] { "shape_body", "shape_head", "shape_legs" }, tabs);   // eyes, ears, nose, mouth, chin and torso have none in this file
    }

    [Fact]
    public void A_tab_lists_only_tweakable_parameters_ordered_by_the_files_group_order()
    {
        var body = Assert.Single(Cat.Subparts(WearableType.Shape, false), t => t.EditGroup == "shape_body");
        Assert.Equal(new[] { 34, 33, 36 }, body.Params.Select(p => p.Id).ToArray());   // 35 is group 1 (not tweakable); 36 is group 2 (tweakable, not sent)
    }

    [Fact]
    public void A_slider_has_its_labels_range_and_default_and_a_missing_label_falls_back_to_the_name()
    {
        Assert.True(Cat.TryGet(33, out var h));
        Assert.Equal("Height", h.Label);
        Assert.Equal("Short", h.LabelMin);
        Assert.Equal("Tall", h.LabelMax);
        Assert.Equal((-1f, 1f, 0f), (h.Min, h.Max, h.Default));
        Assert.Equal(WearableType.Shape, h.Wearable);
        Assert.True(Cat.TryGet(34, out var t));
        Assert.Equal("Body_Thick", t.Label);
        Assert.Equal(0.5f, t.Default);
    }

    [Fact]
    public void A_male_only_tab_shows_for_a_male_avatar_and_a_female_only_tab_for_a_female_one()
    {
        var female = Cat.Subparts(WearableType.Hair, male: false).Select(t => t.EditGroup).ToArray();
        var male = Cat.Subparts(WearableType.Hair, male: true).Select(t => t.EditGroup).ToArray();
        Assert.Equal(new[] { "hair_color", "hair_style" }, female);
        Assert.Equal(new[] { "hair_color", "hair_style", "hair_facial" }, male);
        Assert.Single(Cat.Subparts(WearableType.Physics, male: false));
        Assert.Empty(Cat.Subparts(WearableType.Physics, male: true));
    }

    [Fact]
    public void A_clothing_panel_has_its_own_group_and_leaves_the_colour_parameters_to_the_swatch()
    {
        var shirt = Assert.Single(Cat.Subparts(WearableType.Shirt, false));
        Assert.Equal("shirt", shirt.EditGroup);
        Assert.Equal(new[] { 841, 840 }, shirt.Params.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void The_parameters_of_a_wearable_type_are_all_the_ones_the_file_names_for_it_in_id_order()
    {
        Assert.Equal(new[] { 33, 34, 35, 36, 40, 50, 51, 52, 80 }, Cat.ParamsOf(WearableType.Shape).Select(p => p.Id).ToArray());
        Assert.Equal(new[] { 803, 804, 805, 840, 841 }, Cat.ParamsOf(WearableType.Shirt).Select(p => p.Id).ToArray());
        Assert.Empty(Cat.ParamsOf(WearableType.Eyes));
    }

    [Fact]
    public void A_driver_lists_what_it_drives()
    {
        Assert.True(Cat.TryGet(50, out var leg));
        Assert.Equal(new[] { 51, 52 }, leg.Driven.Select(d => d.Id).ToArray());
        Assert.False(Cat.TryGet(999, out _));
    }

    [Fact]
    public void On_the_real_definition_the_shape_panel_has_its_nine_tabs_and_a_height_slider()
    {
        string? dir = Environment.GetEnvironmentVariable("NV_CHARACTER_DIR");
        if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "avatar_lad.xml")))
            return;   // local input: skipped without the character folder
        var cat = EditParamCatalog.Parse(File.ReadAllBytes(Path.Combine(dir, "avatar_lad.xml")));
        var tabs = cat.Subparts(WearableType.Shape, male: false);
        Assert.Equal(new[] { "shape_body", "shape_head", "shape_eyes", "shape_ears", "shape_nose", "shape_mouth", "shape_chin", "shape_torso", "shape_legs" }, tabs.Select(t => t.EditGroup).ToArray());
        Assert.Contains(Assert.Single(tabs, t => t.EditGroup == "shape_body").Params, p => p.Id == 33);
        Assert.All(tabs.SelectMany(t => t.Params), p => { Assert.True(p.Min < p.Max); Assert.InRange(p.Default, p.Min, p.Max); });
        Assert.True(tabs.Sum(t => t.Params.Count) > 40);
        Assert.Equal(new[] { "skin_color", "skin_facedetail", "skin_makeup", "skin_bodydetail" }, cat.Subparts(WearableType.Skin, false).Select(t => t.EditGroup).Where(g => g.StartsWith("skin_")).ToArray());
    }
}

public class WearableEditorTests
{
    private static WearableEditor Shape(bool canModify = true) => new(EditFixture.Shape(), EditFixture.Catalog(), canModify);
    private static WearableEditor Shirt(bool canModify = true) => new(EditFixture.Shirt(), EditFixture.Catalog(), canModify);

    [Fact]
    public void A_fresh_editor_shows_the_saved_values_and_is_not_dirty()
    {
        var e = Shape();
        Assert.Equal(WearableType.Shape, e.Type);
        Assert.Equal("My shape", e.Name);
        Assert.Equal(0.5f, e.Value(34));
        Assert.False(e.IsDirty);
        Assert.True(e.CanModify);
    }

    [Fact]
    public void A_parameter_the_file_lacks_reads_as_its_default_and_an_unknown_id_as_zero()
    {
        var e = new WearableEditor(new WearableAsset { Type = WearableType.Shape }, EditFixture.Catalog(), true);
        Assert.Equal(0.5f, e.Value(34));
        Assert.Equal(0f, e.Value(99999));
    }

    [Fact]
    public void Setting_a_value_changes_it_marks_the_editor_dirty_and_raises_Changed_once()
    {
        var e = Shape();
        int raised = 0;
        e.Changed += () => raised++;
        e.SetValue(33, 0.75f);
        Assert.Equal(0.75f, e.Value(33));
        Assert.True(e.IsDirty);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void A_value_is_held_inside_the_parameters_range()
    {
        var e = Shape();
        e.SetValue(33, 7f);
        Assert.Equal(1f, e.Value(33));
        e.SetValue(33, -7f);
        Assert.Equal(-1f, e.Value(33));
        e.SetValue(33, float.NaN);
        Assert.Equal(-1f, e.Value(33));   // not a number: ignored
    }

    [Fact]
    public void Putting_a_value_back_to_what_was_saved_makes_the_editor_clean_again()
    {
        var e = Shape();
        e.SetValue(33, 0.5f);
        Assert.True(e.IsDirty);
        e.SetValue(33, 0f);
        Assert.False(e.IsDirty);
    }

    [Fact]
    public void A_driver_passes_its_value_on_to_the_parameters_it_drives_by_their_ranges()
    {
        var e = Shape();
        e.SetValue(50, 0.5f);
        Assert.Equal(1f, e.Value(51), 3);    // halfway up its 0..2 range
        Assert.Equal(0f, e.Value(52), 3);    // this one only moves for negative input
        e.SetValue(50, -0.5f);
        Assert.Equal(0f, e.Value(51), 3);
        Assert.Equal(0.5f, e.Value(52), 3);
        e.SetValue(50, 1f);
        Assert.Equal(2f, e.Value(51), 3);
    }

    [Fact]
    public void An_unknown_parameter_changes_nothing()
    {
        var e = Shape();
        e.SetValue(99999, 1f);
        Assert.False(e.IsDirty);
        Assert.False(e.Values.ContainsKey(99999));
    }

    [Fact]
    public void An_item_that_cannot_be_modified_refuses_every_edit()
    {
        var e = Shirt(canModify: false);
        e.SetValue(840, 1f);
        e.SetTexture(1, EditFixture.Other);
        e.SetColour(WearableKinds.Swatches(WearableType.Shirt)[0], 0, 0, 0);
        e.Name = "x";
        Assert.False(e.IsDirty);
        Assert.Equal(EditFixture.Fabric, e.Texture(1));
        Assert.Equal("Plain shirt", e.Name);
    }

    [Fact]
    public void A_texture_slot_takes_a_new_texture_and_reads_it_back()
    {
        var e = Shirt();
        Assert.Equal(EditFixture.Fabric, e.Texture(1));
        e.SetTexture(1, EditFixture.Other);
        Assert.Equal(EditFixture.Other, e.Texture(1));
        Assert.True(e.IsDirty);
        Assert.Equal(EditFixture.Other, e.Current.Textures[1]);
    }

    [Fact]
    public void A_fabric_slot_cannot_be_emptied_but_a_body_paint_slot_can()
    {
        var shirt = Shirt();
        shirt.SetTexture(1, Guid.Empty);
        Assert.Equal(EditFixture.Fabric, shirt.Texture(1));
        Assert.False(shirt.IsDirty);

        var skinAsset = new WearableAsset { Type = WearableType.Skin, Name = "s" };
        skinAsset.Textures[0] = EditFixture.Other;
        var skin = new WearableEditor(skinAsset, EditFixture.Catalog(), true);
        skin.SetTexture(0, Guid.Empty);
        Assert.Equal(Guid.Empty, skin.Texture(0));
        Assert.True(skin.IsDirty);
        Assert.False(skin.Current.Textures.ContainsKey(0));   // an empty slot is left out of the file
    }

    [Fact]
    public void A_slot_the_type_does_not_have_is_ignored()
    {
        var e = Shirt();
        e.SetTexture(5, EditFixture.Other);
        Assert.False(e.IsDirty);
        Assert.False(e.Current.Textures.ContainsKey(5));
    }

    [Fact]
    public void A_colour_is_three_weights_held_between_zero_and_one()
    {
        var e = Shirt();
        var swatch = WearableKinds.Swatches(WearableType.Shirt)[0];
        Assert.Equal((1f, 0.5f, 0.25f), e.Colour(swatch));
        e.SetColour(swatch, 0.1f, 2f, -1f);
        Assert.Equal((0.1f, 1f, 0f), e.Colour(swatch));
        Assert.Equal(1f, e.Value(804));
        Assert.True(e.IsDirty);
    }

    [Fact]
    public void Renaming_marks_the_editor_dirty_and_going_back_cleans_it()
    {
        var e = Shirt();
        e.Name = "Red shirt";
        Assert.True(e.IsDirty);
        e.Name = "Plain shirt";
        Assert.False(e.IsDirty);
    }

    [Fact]
    public void Revert_puts_back_every_value_texture_colour_and_the_name()
    {
        var e = Shirt();
        e.SetValue(840, 1f);
        e.SetTexture(1, EditFixture.Other);
        e.SetColour(WearableKinds.Swatches(WearableType.Shirt)[0], 0, 0, 0);
        e.Name = "other";
        int raised = 0;
        e.Changed += () => raised++;
        e.Revert();
        Assert.False(e.IsDirty);
        Assert.Equal(0f, e.Value(840));
        Assert.Equal(EditFixture.Fabric, e.Texture(1));
        Assert.Equal((1f, 0.5f, 0.25f), e.Colour(WearableKinds.Swatches(WearableType.Shirt)[0]));
        Assert.Equal("Plain shirt", e.Name);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void The_file_to_save_carries_the_edits_and_keeps_the_permissions_and_everything_untouched()
    {
        var e = Shirt();
        e.SetValue(840, 0.6f);
        e.SetTexture(1, EditFixture.Other);
        e.Name = "Red shirt";
        Assert.True(WearableAsset.TryParse(e.ToBytes(), out var back, out var err), err);
        Assert.Equal("Red shirt", back.Name);
        Assert.Equal(0.6f, back.Params[840], 2);
        Assert.Equal(0.8f, back.Params[841], 2);
        Assert.Equal(EditFixture.Other, back.Textures[1]);
        Assert.Equal(EditFixture.Shirt().PermissionsLines, back.PermissionsLines);
        Assert.Equal(WearableType.Shirt, back.Type);
    }

    [Fact]
    public void The_saved_asset_is_never_changed_by_edits()
    {
        var saved = EditFixture.Shirt();
        var e = new WearableEditor(saved, EditFixture.Catalog(), true);
        e.SetValue(840, 1f);
        e.SetTexture(1, EditFixture.Other);
        Assert.Equal(0f, saved.Params[840]);
        Assert.Equal(EditFixture.Fabric, saved.Textures[1]);
    }
}

public class PreviewParamsTests
{
    private static VisualParamSet Set(params (int id, byte value)[] items) =>
        new(items.Select(i => i.value), [.. items.Select(i => i.id)]);

    [Theory]
    [InlineData(-1f, 0)]
    [InlineData(1f, 255)]
    [InlineData(0f, 128)]
    [InlineData(5f, 255)]
    [InlineData(-5f, 0)]
    public void A_weight_becomes_a_byte_across_the_parameters_range(float value, int expected) =>
        Assert.Equal((byte)expected, PreviewParams.ByteOf(value, -1f, 1f));

    [Fact]
    public void A_byte_read_back_is_within_one_step_of_the_weight()
    {
        foreach (float w in new[] { 0.1f, 0.33f, 0.5f, 0.9f })
        {
            byte b = PreviewParams.ByteOf(w, 0f, 1f);
            Assert.True(Math.Abs(AvatarShape.WeightOf(b, 0f, 1f) - w) <= 1f / 255f + 1e-5f);
        }
    }

    [Fact]
    public void The_edited_weight_replaces_its_byte_and_the_rest_stay()
    {
        var cat = EditFixture.Catalog();
        var current = Set((33, 128), (34, 64), (40, 200));
        var result = PreviewParams.Apply(current, cat, new Dictionary<int, float> { [33] = 1f });
        Assert.Equal(new byte[] { 255, 64, 200 }, result.Raw.ToArray());
        Assert.Equal(current.WireOrder, result.WireOrder);
    }

    [Fact]
    public void A_weight_the_set_does_not_carry_or_the_definition_does_not_know_is_ignored()
    {
        var cat = EditFixture.Catalog();
        var current = Set((33, 128), (34, 64));
        var result = PreviewParams.Apply(current, cat, new Dictionary<int, float> { [40] = 1f, [99999] = 1f });
        Assert.True(result.SameAs(current));
    }

    [Fact]
    public void Edits_equal_to_what_was_there_leave_the_set_the_same()
    {
        var cat = EditFixture.Catalog();
        float w = AvatarShape.WeightOf(100, -1f, 1f);
        var current = Set((33, 100));
        var result = PreviewParams.Apply(current, cat, new Dictionary<int, float> { [33] = w });
        Assert.Equal(new byte[] { 100 }, result.Raw.ToArray());
    }

    [Fact]
    public void An_empty_set_stays_empty()
    {
        var result = PreviewParams.Apply(VisualParamSet.Empty, EditFixture.Catalog(), new Dictionary<int, float> { [33] = 1f });
        Assert.Equal(0, result.Count);
    }
}
