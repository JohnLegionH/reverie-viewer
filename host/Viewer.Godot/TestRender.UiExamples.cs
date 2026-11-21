// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Linq;
using Godot;
using Viewer.Core.Inventory;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Addons;
using Viewer.Shell.Addons;
using Viewer.Shell.Notifications;

namespace Viewer.Godot;

public partial class TestRender
{
    /// <summary>What the add-on host needs of the viewer in a picture: nothing is sent and there is no inventory.</summary>
    private sealed class RenderAddonServices : IAddonServices
    {
        public double Now => 0;
        public void SendAnimations(System.Collections.Generic.IReadOnlyList<(Guid Animation, bool Start)> requests) { }
        public bool Say(string text) => false;
        public IAddonInventory? Inventory => null;
    }

    /// <summary>An empty world for the restraints in a picture.</summary>
    private sealed class RenderRlvWorld : IRlvWorld
    {
        public bool Seated => false;
        public System.Numerics.Vector3? AgentPosition => null;
        public System.Numerics.Vector3? ObjectPosition(Guid objectId) => null;
        public System.Collections.Generic.IReadOnlyList<RlvAttachment> Attachments => [];
        public bool HasImSession(Guid id) => false;
    }

    /// <summary>The Add-ons window ("addons"), or one of the built-in add-ons' windows each in its own window as the viewer draws them
    /// ("addon-ao": the overrider's sets and animations editor; "addon-rlv": the restraints window), over the add-ons this build carries,
    /// started as the viewer starts them. No session or grid.</summary>
    private static void ExampleAddons(ShellRoot shell, StringTable strings, string scene)
    {
        var host = new AddonHost(new RenderAddonServices(), new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "render"))
        {
            Restraints = new RlvGate(new RenderRlvWorld(), (_, _) => true),
        };
        if (Main.BuiltInAddonsFolder() is { } folder)
            host.Discover(folder, builtIn: true);
        host.StartEnabled();
        if (scene == "addon-rlv")
        {
            // restrictions are off until switched on and take hold at the next login; one object has asked for one restriction
            host.SetEnabled("rlv", true);
            host.LoggedIn();
            host.Chat(new Viewer.Addons.ChatLine(Viewer.Addons.ChatKind.OwnerSay, new Guid("00000000-0000-4000-8000-0000000000a1"), "Example collar", "@sit=n"));
        }
        if (scene == "addons")
        {
            shell.AddFloater(new FloaterSpec(AddonsPanel.FloaterId, "addons.title", new UiSize(420, 460), new UiSize(320, 260)), new AddonsPanel(strings, host));
            shell.Shell.Floaters.Open(AddonsPanel.FloaterId);
            return;
        }
        string addon = scene == "addon-ao" ? "ao" : "rlv", window = scene == "addon-ao" ? "editor" : "restrictions";
        var (addonId, made) = host.Windows.First(w => w.AddonId == addon && w.Window.Id == window);
        string key = $"addon.{addonId}.{made.Id}";
        strings.Define(key + ".title", made.Title);
        var view = new AddonWindowView(strings, host, addonId);
        var scroll = new ScrollContainer { Name = "addon_scroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        scroll.AddChild(view);
        shell.AddFloater(new FloaterSpec(key, key + ".title", new UiSize(380, 420), new UiSize(260, 200)), scroll);
        view.Bind(made);
        // the restraints window shows the wall-clock time a restriction was set: the picture shows the picture clock's time instead
        foreach (var child in view.GetChildren())
            if (child is Label label && System.Text.RegularExpressions.Regex.IsMatch(label.Text, @"set at [0-9]{2}:[0-9]{2}:[0-9]{2}"))
                label.Text = System.Text.RegularExpressions.Regex.Replace(label.Text, @"set at [0-9]{2}:[0-9]{2}:[0-9]{2}", "set at 12:00:00");
        shell.Shell.Floaters.Open(key);
    }

    /// <summary>The Gestures window after some use: sorted by chat trigger (descending), a gesture playing so Stop takes Play's place, and
    /// the question Delete asks showing. No session or grid.</summary>
    private static void ExampleGestureListUsed(ShellRoot shell)
    {
        var window = ExampleGestureList(shell);
        window.SortBy(Viewer.Shell.Inventory.GestureColumn.Trigger);
        window.SortBy(Viewer.Shell.Inventory.GestureColumn.Trigger);
        window.SetPlaying([new Guid("00000000-0000-4000-8000-000000000022")]);
        window.DeleteButton.EmitSignal(BaseButton.SignalName.Pressed);
    }
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

    /// <summary>The four upload windows (image, bulk images, model, bulk models) with no session: each opens empty, as it does before a file is chosen.</summary>
    private static void ExampleUploads(ShellRoot shell)
    {
        BuildFeature.Install(shell, () => null, () => Guid.Empty);
        ModelFeature.Install(shell, () => null, () => Guid.Empty, () => Guid.Empty);
    }

    /// <summary>An unknown-authority certificate for a reserved example host.</summary>
    private static Viewer.Core.Net.CertificateRefusal ExampleCertificate() =>
        new("grid.example.org", "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", "UntrustedRoot", "CN=grid.example.org", new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero))
        {
            Kind = Viewer.Core.Net.CertificateProblem.UnknownAuthority, Issuer = "CN=Example Authority",
            NotBefore = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };

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
