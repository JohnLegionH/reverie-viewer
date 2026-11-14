// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text;
using Viewer.Core.Appearance;
using Viewer.Core.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The wearable asset text format and the type tables. The format follows the official viewer's wearable import and export
/// (indra/llappearance/llwearable.cpp: header, name, description, permissions block, sale block, type, parameters, textures).
/// </summary>
public class WearableAssetTests
{
    private static readonly Guid Tex1 = Guid.Parse("0b5f2a64-1c3e-4d5a-9a77-3c2d1e0f8a11");
    private static readonly Guid Tex2 = Guid.Parse("7e1d9c20-5b4a-4f63-8d12-aa90b7c6d5e2");
    private static readonly Guid Owner = Guid.Parse("11111111-2222-4333-8444-555555555555");

    private static string Sample(string name = "Test shirt", string desc = "a plain one", string tail = "parameters 3\n803 .5\n804 1\n805 0\ntextures 1\n1 " + "0b5f2a64-1c3e-4d5a-9a77-3c2d1e0f8a11\n") =>
        "LLWearable version 22\n" + name + "\n" + desc + "\n"
        + "\tpermissions 0\n\t{\n\t\tbase_mask\t7fffffff\n\t\towner_mask\t7fffffff\n\t\tgroup_mask\t00000000\n\t\teveryone_mask\t00000000\n\t\tnext_owner_mask\t00082000\n"
        + "\t\tcreator_id\t" + Owner + "\n\t\towner_id\t" + Owner + "\n\t\tlast_owner_id\t" + Owner + "\n\t\tgroup_id\t00000000-0000-0000-0000-000000000000\n\t}\n"
        + "\tsale_info\t0\n\t{\n\t\tsale_type\tnot\n\t\tsale_price\t10\n\t}\n"
        + "type 4\n" + tail;

    private static WearableAsset Parse(string text)
    {
        Assert.True(WearableAsset.TryParse(Encoding.UTF8.GetBytes(text), out var a, out var err), err);
        return a;
    }

    [Fact]
    public void A_wearable_file_is_read_into_its_name_description_type_weights_and_textures()
    {
        var a = Parse(Sample());
        Assert.Equal(22, a.Version);
        Assert.Equal("Test shirt", a.Name);
        Assert.Equal("a plain one", a.Description);
        Assert.Equal(WearableType.Shirt, a.Type);
        Assert.Equal(new[] { 803, 804, 805 }, a.Params.Keys.ToArray());
        Assert.Equal(0.5f, a.Params[803]);
        Assert.Equal(1f, a.Params[804]);
        Assert.Equal(0f, a.Params[805]);
        Assert.Equal(Tex1, a.Textures[1]);
    }

    [Fact]
    public void A_file_written_back_without_changes_is_the_same_text_with_the_weights_in_the_files_terse_form()
    {
        var a = Parse(Sample());
        Assert.Equal(Sample(), Encoding.UTF8.GetString(a.ToBytes()));
    }

    [Fact]
    public void A_changed_weight_and_texture_are_written_and_read_back()
    {
        var a = Parse(Sample());
        a.Params[803] = 0.73f;
        a.Textures[1] = Tex2;
        a.Name = "Renamed";
        var back = Parse(Encoding.UTF8.GetString(a.ToBytes()));
        Assert.Equal("Renamed", back.Name);
        Assert.Equal(0.73f, back.Params[803], 2);
        Assert.Equal(Tex2, back.Textures[1]);
        Assert.Equal(a.PermissionsLines, back.PermissionsLines);
        Assert.Equal(a.SaleInfoLines, back.SaleInfoLines);
    }

    [Theory]
    [InlineData(1.2f, "1.2")]
    [InlineData(24f, "24")]
    [InlineData(0.59f, ".59")]
    [InlineData(-0.59f, "-.59")]
    [InlineData(0f, "0")]
    [InlineData(0.5f, ".5")]
    [InlineData(-2f, "-2")]
    [InlineData(100f, "100")]
    [InlineData(0.004f, "0")]
    public void A_weight_is_written_with_two_decimals_and_without_trailing_zeros_or_a_leading_zero(float value, string expected) =>
        Assert.Equal(expected, WearableAsset.TerseWeight(value));

    [Fact]
    public void An_empty_name_and_description_are_kept_as_empty_lines()
    {
        var a = Parse(Sample("", ""));
        Assert.Equal("", a.Name);
        Assert.Equal("", a.Description);
        Assert.Equal(Sample("", ""), Encoding.UTF8.GetString(a.ToBytes()));
    }

    [Theory]
    [InlineData("not a wearable\n")]
    [InlineData("LLWearable version 23\nx\ny\n")]
    [InlineData("LLWearable version 22\n")]
    public void A_file_with_a_bad_header_or_no_body_is_refused_with_a_reason(string text)
    {
        Assert.False(WearableAsset.TryParse(Encoding.UTF8.GetBytes(text), out _, out var err));
        Assert.NotEqual("", err);
    }

    [Fact]
    public void A_file_with_a_type_outside_the_table_a_missing_block_or_too_many_entries_is_refused()
    {
        Assert.False(WearableAsset.TryParse(Encoding.UTF8.GetBytes(Sample().Replace("type 4", "type 99")), out _, out var e1));
        Assert.NotEqual("", e1);
        Assert.False(WearableAsset.TryParse(Encoding.UTF8.GetBytes(Sample().Replace("parameters 3", "parameters 1001")), out _, out var e2));
        Assert.NotEqual("", e2);
        Assert.False(WearableAsset.TryParse(Encoding.UTF8.GetBytes(Sample().Replace("textures 1", "textures 101")), out _, out var e3));
        Assert.NotEqual("", e3);
        Assert.False(WearableAsset.TryParse(Encoding.UTF8.GetBytes(Sample().Replace("\tsale_info\t0", "\tsale_infx\t0")), out _, out var e4));
        Assert.NotEqual("", e4);
        Assert.False(WearableAsset.TryParse(Encoding.UTF8.GetBytes(Sample(tail: "parameters 2\n803 .5\n")), out _, out var e5));   // fewer lines than declared
        Assert.NotEqual("", e5);
    }

    [Fact]
    public void A_file_over_the_size_limit_is_refused_without_being_read()
    {
        Assert.False(WearableAsset.TryParse(new byte[WearableAsset.MaxBytes + 1], out _, out var err));
        Assert.NotEqual("", err);
    }

    [Fact]
    public void A_clone_is_independent_of_the_original()
    {
        var a = Parse(Sample());
        var c = a.Clone();
        c.Params[803] = 0.1f;
        c.Textures[1] = Tex2;
        c.Name = "other";
        Assert.Equal(0.5f, a.Params[803]);
        Assert.Equal(Tex1, a.Textures[1]);
        Assert.Equal("Test shirt", a.Name);
        Assert.Equal(a.PermissionsLines, c.PermissionsLines);
    }
}

public class WearableKindsTests
{
    [Theory]
    [InlineData(WearableType.Shape, 0, true, 13)]
    [InlineData(WearableType.Skin, 1, true, 13)]
    [InlineData(WearableType.Hair, 2, true, 13)]
    [InlineData(WearableType.Eyes, 3, true, 13)]
    [InlineData(WearableType.Shirt, 4, false, 5)]
    [InlineData(WearableType.Pants, 5, false, 5)]
    [InlineData(WearableType.Physics, 15, false, 5)]
    [InlineData(WearableType.Universal, 16, false, 5)]
    public void Each_type_has_its_number_and_is_a_body_part_or_clothing(WearableType t, int number, bool body, int assetType)
    {
        Assert.Equal(number, (int)t);
        Assert.Equal(body, WearableKinds.IsBodyPart(t));
        Assert.Equal(assetType, WearableKinds.AssetType(t));
        Assert.True(WearableKinds.TryFromNumber(number, out var back));
        Assert.Equal(t, back);
    }

    [Fact]
    public void Numbers_outside_the_table_are_not_types()
    {
        Assert.True(WearableKinds.TryFromNumber(0, out _) && WearableKinds.TryFromNumber(16, out _));
        Assert.False(WearableKinds.TryFromNumber(-1, out _));
        Assert.False(WearableKinds.TryFromNumber(17, out _));
    }

    [Fact]
    public void Every_type_has_a_distinct_lower_case_name()
    {
        var names = Enum.GetValues<WearableType>().Where(t => t != WearableType.None).Select(WearableKinds.Name).ToArray();
        Assert.All(names, n => Assert.Matches("^[a-z]+$", n));
        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.Equal("shirt", WearableKinds.Name(WearableType.Shirt));
    }

    [Theory]
    [InlineData(WearableType.Shirt, new[] { 1 })]
    [InlineData(WearableType.Pants, new[] { 2 })]
    [InlineData(WearableType.Skin, new[] { 0, 5, 6 })]
    [InlineData(WearableType.Hair, new[] { 4 })]
    [InlineData(WearableType.Eyes, new[] { 3 })]
    [InlineData(WearableType.Jacket, new[] { 13, 14 })]
    [InlineData(WearableType.Shape, new int[0])]
    [InlineData(WearableType.Physics, new int[0])]
    [InlineData(WearableType.Alpha, new[] { 21, 22, 23, 24, 25 })]
    public void The_texture_slots_of_a_type_are_the_official_ones(WearableType t, int[] slots) =>
        Assert.Equal(slots, WearableKinds.Slots(t).Select(s => s.Index).ToArray());

    [Fact]
    public void Body_paint_and_alpha_slots_may_be_left_empty_and_fabric_slots_may_not()
    {
        Assert.NotEmpty(WearableKinds.Slots(WearableType.Skin));
        Assert.All(WearableKinds.Slots(WearableType.Skin), s => Assert.True(s.AllowNone));
        Assert.NotEmpty(WearableKinds.Slots(WearableType.Alpha));
        Assert.All(WearableKinds.Slots(WearableType.Alpha), s => Assert.True(s.AllowNone));
        Assert.NotEmpty(WearableKinds.Slots(WearableType.Shirt));
        Assert.All(WearableKinds.Slots(WearableType.Shirt), s => Assert.False(s.AllowNone));
    }

    [Theory]
    [InlineData(WearableType.Shirt, 1, 803, 804, 805)]
    [InlineData(WearableType.Pants, 2, 806, 807, 808)]
    [InlineData(WearableType.Shoes, 7, 812, 813, 817)]
    [InlineData(WearableType.Gloves, 15, 827, 829, 830)]
    [InlineData(WearableType.Skirt, 18, 921, 922, 923)]
    [InlineData(WearableType.Tattoo, 26, 1071, 1072, 1073)]
    [InlineData(WearableType.Universal, 29, 1238, 1239, 1240)]
    public void A_clothing_type_has_a_colour_swatch_made_of_three_parameters(WearableType t, int slot, int r, int g, int b)
    {
        var swatch = Assert.Single(WearableKinds.Swatches(t));
        Assert.Equal(new ColourSwatch(slot, r, g, b), swatch);
    }

    [Fact]
    public void Body_parts_alpha_and_physics_have_no_colour_swatch()
    {
        Assert.NotEmpty(WearableKinds.Swatches(WearableType.Shirt));
        foreach (var t in new[] { WearableType.Shape, WearableType.Skin, WearableType.Hair, WearableType.Eyes, WearableType.Alpha, WearableType.Physics })
            Assert.Empty(WearableKinds.Swatches(t));
    }
}
