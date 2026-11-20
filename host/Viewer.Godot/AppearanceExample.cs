// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Viewer.Core.Actions;
using Viewer.Core.Appearance;
using Viewer.Core.Assets;
using Viewer.Core.Inventory;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>The ids of the example content: the items in the current outfit, the inventory texture and the folders.</summary>
internal sealed record AppearanceExampleIds(Guid Shape, Guid Shirt, Guid Eyes, Guid Weave, Guid BodyFolder, Guid ClothFolder, Guid Cof, Guid ShirtTextureAsset, Guid WeaveAsset);

/// <summary>Example content for the Appearance window, shared by the render cases and the start-up check (no session, no grid).</summary>
internal static class AppearanceExample
{
    /// <summary>A small avatar definition: a few sliders, labelled in plain example words.</summary>
    public const string Definition = """
        <linden_avatar version="2.0" wearable_definition_version="22">
          <param id="33" group="0" wearable="shape" edit_group="shape_body" edit_group_order="1" name="Height" label="Height" label_min="Short" label_max="Tall" value_min="-1" value_max="1" value_default="0"/>
          <param id="34" group="0" wearable="shape" edit_group="shape_body" edit_group_order="2" name="Thickness" label="Body thickness" label_min="Thin" label_max="Thick" value_min="0" value_max="1" value_default="0.5"/>
          <param id="35" group="0" wearable="shape" edit_group="shape_body" edit_group_order="3" name="Fat" label="Body fat" label_min="Less fat" label_max="More fat" value_min="0" value_max="1" value_default="0"/>
          <param id="36" group="0" wearable="shape" edit_group="shape_head" edit_group_order="1" name="HeadSize" label="Head size" label_min="Small head" label_max="Big head" value_min="0" value_max="1" value_default="0.5"/>
          <param id="37" group="0" wearable="shape" edit_group="shape_head" edit_group_order="2" name="HeadWidth" label="Head width" label_min="Narrow" label_max="Wide" value_min="0" value_max="1" value_default="0.5"/>
          <param id="38" group="0" wearable="shape" edit_group="shape_legs" edit_group_order="1" name="LegLength" label="Leg length" label_min="Short legs" label_max="Long legs" value_min="-1" value_max="1" value_default="0"/>
          <param id="80" group="0" wearable="shape" name="male" value_min="0" value_max="1" value_default="0"/>
          <param id="803" group="0" wearable="shirt" edit_group="colorpicker" name="shirt_red" value_min="0" value_max="1" value_default="1"/>
          <param id="804" group="0" wearable="shirt" edit_group="colorpicker" name="shirt_green" value_min="0" value_max="1" value_default="1"/>
          <param id="805" group="0" wearable="shirt" edit_group="colorpicker" name="shirt_blue" value_min="0" value_max="1" value_default="1"/>
          <param id="840" group="0" wearable="shirt" edit_group="shirt" edit_group_order="1" name="Sleeve" label="Sleeve length" label_min="Short" label_max="Long" value_min="0" value_max="1" value_default="0.5"/>
          <param id="841" group="0" wearable="shirt" edit_group="shirt" edit_group_order="2" name="Hem" label="Shirt length" label_min="Cropped" label_max="Long" value_min="0" value_max="1" value_default="0.8"/>
        </linden_avatar>
        """;

    public static Guid ExId(int n) => Guid.Parse($"0a11ce00-0000-4000-8000-{n:x12}");

    /// <summary>
    /// Binds the window to example content: a current outfit of a shape, a shirt and eyes, a texture in the inventory, and the wearable files for
    /// the shape and the shirt. Everything the window submits goes to <paramref name="submit"/> (default: sent).
    /// </summary>
    public static AppearanceExampleIds Bind(AppearancePanel panel, Func<ActionIntent, ActionResult>? submit = null, bool canModify = true, bool canCopy = true)
    {
        Guid root = ExId(1), cof = ExId(2), mine = ExId(3), cloth = ExId(4), body = ExId(5), tex = ExId(6), libRoot = ExId(7);
        Guid shirt = ExId(100), shape = ExId(101), eyes = ExId(102), weave = ExId(110);
        Guid shirtTexture = Guid.Parse("5b9a0c3e-6a11-4a52-9c2e-0d74b3f1a801");
        var model = new InventoryModel((_, _) => { });
        model.Apply(new InventorySkeletonReceived(ExId(8), root, ExId(9), libRoot,
            [
                new InventoryFolderData(root, Guid.Empty, "", FolderKind.Root, 1), new InventoryFolderData(cof, root, "Current Outfit", FolderKind.CurrentOutfit, 3),
                new InventoryFolderData(mine, root, "My Outfits", FolderKind.MyOutfits, 1), new InventoryFolderData(cloth, root, "Clothing", FolderKind.Clothing, 1),
                new InventoryFolderData(body, root, "Body Parts", FolderKind.Bodypart, 1), new InventoryFolderData(tex, root, "Textures", FolderKind.Texture, 1),
            ],
            [new InventoryFolderData(libRoot, Guid.Empty, "", FolderKind.Root, 1)]));
        InventoryItemData Thing(Guid id, Guid parent, string n, ItemKind k, int asset, uint flags) => new(id, parent, n, k, asset, 18, flags, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        InventoryItemData Link(Guid id, Guid target, string n, string desc) => new(id, cof, n, ItemKind.Link, 24, 18, 0, null, target, desc);
        model.Apply(new InventoryFolderFetched(cloth, true, 2, 1, [], [Thing(shirt, cloth, "Example shirt", ItemKind.Clothing, 5, (uint)WearableType.Shirt)]));
        model.Apply(new InventoryFolderFetched(body, true, 2, 2, [], [Thing(shape, body, "Example shape", ItemKind.Bodypart, 13, (uint)WearableType.Shape), Thing(eyes, body, "Example eyes", ItemKind.Bodypart, 13, (uint)WearableType.Eyes)]));
        model.Apply(new InventoryFolderFetched(tex, true, 2, 1, [], [Thing(weave, tex, "Example weave", ItemKind.Texture, 0, 0)]));
        model.Apply(new InventoryFolderFetched(cof, true, 3, 3, [], [Link(ExId(200), shirt, "Example shirt", "@400"), Link(ExId(201), shape, "Example shape", ""), Link(ExId(202), eyes, "Example eyes", "")]));
        model.Apply(new InventoryFolderFetched(mine, true, 2, 0, [], []));

        var shapeAsset = new WearableAsset { Name = "Example shape", Type = WearableType.Shape, PermissionsLines = ["\tpermissions 0", "\t{", "\t}"], SaleInfoLines = ["\tsale_info\t0", "\t{", "\t}"] };
        foreach (var (id, w) in new (int, float)[] { (33, 0.25f), (34, 0.5f), (35, 0f), (36, 0.5f), (37, 0.5f), (38, 0f), (80, 0f) }) shapeAsset.Params[id] = w;
        var shirtAsset = new WearableAsset { Name = "Example shirt", Type = WearableType.Shirt, PermissionsLines = ["\tpermissions 0", "\t{", "\t}"], SaleInfoLines = ["\tsale_info\t0", "\t{", "\t}"] };
        foreach (var (id, w) in new (int, float)[] { (803, 0.8f), (804, 0.3f), (805, 0.25f), (840, 0.5f), (841, 0.8f) }) shirtAsset.Params[id] = w;
        shirtAsset.Textures[1] = shirtTexture;
        var files = new Dictionary<Guid, byte[]> { [ExId(900)] = shapeAsset.ToBytes(), [ExId(901)] = shirtAsset.ToBytes() };
        var info = new Dictionary<Guid, WearableItemInfo> { [shape] = new(ExId(900), 13, canModify, canCopy), [shirt] = new(ExId(901), 5, canModify, canCopy), [eyes] = new(ExId(902), 13, canModify, canCopy) };

        panel.Bind(model, submit ?? (_ => new ActionResult(ActionStatus.Sent, 1)), id => info.GetValueOrDefault(id),
            (asset, _, _) => Task.FromResult(files.TryGetValue(asset, out var b) ? AssetFetchResult.Ok(b) : AssetFetchResult.Of(AssetFetchStatus.NotFound)),
            EditParamCatalog.Parse(Encoding.UTF8.GetBytes(Definition)), () => false, id => id == weave ? ExId(903) : Guid.Empty, null, null, _ => Task.FromResult<Viewer.Core.Imaging.DecodedTexture?>(WeaveImage()));
        return new AppearanceExampleIds(shape, shirt, eyes, weave, body, cloth, cof, shirtTexture, ExId(903));
    }

    /// <summary>A small woven-cloth picture for the example shirt's texture swatch (made here, so no file or grid is read).</summary>
    private static Viewer.Core.Imaging.DecodedTexture WeaveImage()
    {
        const int n = 32;
        var rgba = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                bool over = ((x / 4) + (y / 4)) % 2 == 0;
                int i = (y * n + x) * 4;
                rgba[i] = (byte)(over ? 190 : 120);
                rgba[i + 1] = (byte)(over ? 150 : 90);
                rgba[i + 2] = (byte)(over ? 110 : 70);
                rgba[i + 3] = 255;
            }
        return new Viewer.Core.Imaging.DecodedTexture(n, n, rgba, Viewer.Core.Imaging.AlphaClass.Opaque, 3);
    }

    /// <summary>What an interface scene shows: the Outfit tab, the Edit tab on the shape (the Height slider moved, so there are unsaved changes) or on the shirt.</summary>
    public static async Task Show(AppearancePanel panel, string scene)
    {
        var ids = Bind(panel);
        panel.Outfit.Rebuild();
        if (scene == "appearance")
        {
            panel.Tabs.CurrentTab = 0;
            return;
        }
        panel.Tabs.CurrentTab = 1;
        await panel.OpenAsync(scene == "appearance-shape" ? ids.Shape : ids.Shirt);
        panel.Rebuild();
        if (scene == "appearance-shape")
        {
            panel.Editing!.Editor!.SetValue(33, 0.6f);
            panel.Rebuild();
        }
    }
}
