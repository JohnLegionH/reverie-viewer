// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Linq;
using Godot;
using Viewer.Core.Inventory;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Notifications;

namespace Viewer.Godot;

public partial class TestRender
{
    /// <summary>A small made-up inventory for the pickers: a Textures folder (five textures), an
    /// Objects folder and a Library folder, every folder loaded.</summary>
    private static InventoryModel ExamplePickModel()
    {
        static Guid G(int n) => new($"00000000-0000-4000-8000-{n:D12}");
        Guid owner = G(1), root = G(2), libraryOwner = G(3), libraryRoot = G(4), textures = G(10), objects = G(11), clothing = G(12);
        var model = new InventoryModel((_, _) => { });
        InventoryFolderData F(Guid id, Guid parent, string name, FolderKind kind = FolderKind.None) => new(id, parent, name, kind, 1, 0);
        var tops = new[] { F(textures, root, "Textures", FolderKind.Texture), F(objects, root, "Objects", FolderKind.Object), F(clothing, root, "Clothing", FolderKind.Clothing) };
        model.Apply(new InventorySkeletonReceived(owner, root, libraryOwner, libraryRoot,
            new[] { new InventoryFolderData(root, Guid.Empty, "My Inventory", FolderKind.Root, 1, -1) }.Concat(tops).ToArray(),
            [new InventoryFolderData(libraryRoot, Guid.Empty, "Library", FolderKind.Root, 1, 0)]));
        model.Apply(new InventoryFolderFetched(root, true, 2, tops.Length, tops, []));
        string[] names = ["Brick wall", "Grass", "Oak planks", "Sandstone", "Water ripple"];
        model.Apply(new InventoryFolderFetched(textures, true, 2, names.Length, [],
            names.Select((n, i) => new InventoryItemData(G(20 + i), textures, n, ItemKind.Texture, 0, 0, 0, null, AssetId: G(30 + i))).ToArray()));
        model.Apply(new InventoryFolderFetched(objects, true, 2, 0, [], []));
        model.Apply(new InventoryFolderFetched(clothing, true, 2, 0, [], []));
        model.Apply(new InventoryFolderFetched(libraryRoot, true, 2, 0, [], []));
        return model;
    }

    /// <summary>The Build window's texture picker over the example inventory, the third row chosen once its list is filled. No session or grid.</summary>
    private static FacePickerWindows ExampleTexturePicker(ShellRoot shell, StringTable strings, Viewer.Shell.Build.EditController edit)
    {
        var pickers = new FacePickerWindows(strings, shell, () => edit);
        pickers.OpenItems(ExamplePickModel(), ItemKind.Texture);
        return pickers;
    }

    /// <summary>The Take to folder window over the example inventory. No session or grid.</summary>
    private static FolderPickPanel ExampleTakeFolder(ShellRoot shell, StringTable strings)
    {
        var panel = new FolderPickPanel(strings, _ => { }, () => { });
        shell.AddFloater(new FloaterSpec(FolderPickPanel.FloaterId, "edit.take_folder.title", new UiSize(380, 460), new UiSize(320, 340)), panel);
        panel.Begin(ExamplePickModel());
        shell.Shell.Floaters.Open(FolderPickPanel.FloaterId);
        return panel;
    }

    /// <summary>The Grid options window with the example box's settings. No session or grid.</summary>
    private static void ExampleGridOptions(ShellRoot shell, StringTable strings, Viewer.Shell.Build.EditController edit)
    {
        var panel = new GridPanel(strings, () => edit);
        shell.AddFloater(new FloaterSpec(GridPanel.FloaterId, "grid.title", new UiSize(300, 360), new UiSize(260, 300)), panel);
        panel.Refresh();
        shell.Shell.Floaters.Open(GridPanel.FloaterId);
    }

    /// <summary>The items of the example box the Contents tab and the object contents window list: a script, a notecard and a texture.</summary>
    private static TaskItem[] ExampleTaskItems()
    {
        static Guid G(int n) => new($"00000000-0000-4000-8000-{n:D12}");
        const uint full = 0x7fffffff;
        return
        [
            new TaskItem(G(40), "Door script", 10, 10, G(1), full, 0, AssetId: G(50), CreatorId: G(1)),
            new TaskItem(G(41), "Read me", 7, 7, G(1), full, 0, AssetId: G(51), CreatorId: G(1)),
            new TaskItem(G(42), "Floor texture", 0, 0, G(1), full, 0, AssetId: G(52), CreatorId: G(1)),
        ];
    }

    /// <summary>The Open (object contents) window listing the example box's items, and the Buy window offering a copy of it. No session or grid.</summary>
    private static void ExampleTrade(InteractFeature.Panels interact, ShellRoot shell, string scene)
    {
        static Guid G(int n) => new($"00000000-0000-4000-8000-{n:D12}");
        var items = ExampleTaskItems();
        if (scene == "objectcontents")
        {
            interact.Contents.Show(new ObjectContentsView(G(31), 1, items, false), canCopy: true);
            shell.Shell.Floaters.Open(InteractFeature.ContentsId);
            return;
        }
        interact.Buy.Show(new BuyOffer(G(31), 1, "Example chair", 3, 50, true), null, new ObjectContentsView(G(31), 1, items, false));
        shell.Shell.Floaters.Open(InteractFeature.BuyId);
    }

    /// <summary>The object info window an object's name in a notice opens. No session or grid.</summary>
    private static void ExampleObjectInfo(NotificationFeature.Panels notices, ShellRoot shell)
    {
        static Guid G(int n) => new($"00000000-0000-4000-8000-{n:D12}");
        notices.Object.Show(new ObjectInfo(G(60), G(1), "Example fountain", false, "Example Region (128, 64, 22)"), "Test User");
        shell.Shell.Floaters.Open(NotificationFeature.ObjectId);
    }
}
