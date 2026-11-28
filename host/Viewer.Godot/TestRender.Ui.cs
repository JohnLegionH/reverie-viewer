// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Notifications;

namespace Viewer.Godot;

public partial class TestRender
{
    /// <summary>The clock text every interface picture shows, so pictures taken hours apart match.</summary>
    private const string TestClockText = "12:00 PM";

    /// <summary>
    /// The inventory scene's content: a small made-up inventory with an item being dragged over a closed folder, so the picture shows
    /// the drag look (the drop-target outline with its text on the folder, the dashed outline on the dragged row). The marks are held in
    /// place, because a still picture has no drag going on.
    /// </summary>
    private static void ExampleInventoryDrag(InventoryPanel panel, bool dragCue = true)
    {
        Guid G(int n) => new($"00000000-0000-4000-8000-{n:D12}");
        Guid owner = G(1), root = G(2), libraryOwner = G(3), libraryRoot = G(4), note = G(20);
        var model = new Viewer.Core.Inventory.InventoryModel((_, _) => { });
        Viewer.Core.Inventory.InventoryFolderData F(Guid id, string name, Viewer.Core.Inventory.FolderKind kind = Viewer.Core.Inventory.FolderKind.None) =>
            new(id, id == root ? Guid.Empty : root, name, kind, 1, id == root ? -1 : 1);
        var tops = new[] { F(G(10), "Clothing"), F(G(11), "Landmarks"), F(G(12), "Notecards"), F(G(13), "Objects") };
        model.Apply(new Viewer.Core.Inventory.InventorySkeletonReceived(owner, root, libraryOwner, libraryRoot,
            new[] { F(root, "My Inventory", Viewer.Core.Inventory.FolderKind.Root) }.Concat(tops).ToArray(),
            new[] { new Viewer.Core.Inventory.InventoryFolderData(libraryRoot, Guid.Empty, "Library", Viewer.Core.Inventory.FolderKind.Root, 1, 0) }));
        model.Apply(new Viewer.Core.Inventory.InventoryFolderFetched(root, true, 2, tops.Length + 1, tops,
            new[] { new Viewer.Core.Inventory.InventoryItemData(note, root, "Welcome note", Viewer.Core.Inventory.ItemKind.Notecard, 7, 7, 0, null) }));
        panel.Bind(model, _ => new ActionResult(ActionStatus.Sent));
        if (dragCue)
            panel.TreeView.HoldCue(new Viewer.Shell.Inventory.DropCue(note, G(13)));
    }

    /// <summary>The Item Properties scene: a made-up item the agent may copy and give away but not modify, so the picture shows
    /// greyed and open fields side by side.</summary>
    private static void ExampleItemProperties(ShellRoot shell)
    {
        Guid G(int n) => new($"00000000-0000-4000-8000-{n:D12}");
        Guid owner = G(1), root = G(2), libraryOwner = G(3), libraryRoot = G(4), creator = G(5), chair = G(30);
        var model = new Viewer.Core.Inventory.InventoryModel((_, _) => { });
        model.Apply(new Viewer.Core.Inventory.InventorySkeletonReceived(owner, root, libraryOwner, libraryRoot,
            new[] { new Viewer.Core.Inventory.InventoryFolderData(root, Guid.Empty, "My Inventory", Viewer.Core.Inventory.FolderKind.Root, 1, -1) },
            new[] { new Viewer.Core.Inventory.InventoryFolderData(libraryRoot, Guid.Empty, "Library", Viewer.Core.Inventory.FolderKind.Root, 1, 0) }));
        const uint copy = Viewer.Core.Inventory.ItemPermissions.Copy, transfer = Viewer.Core.Inventory.ItemPermissions.Transfer;
        var rights = new Viewer.Core.Inventory.ItemPermissions(copy | transfer, copy | transfer, 0, 0, copy | transfer, creator, owner, Guid.Empty, false, 0, 0);
        model.Apply(new Viewer.Core.Inventory.InventoryFolderFetched(root, true, 2, 1, Array.Empty<Viewer.Core.Inventory.InventoryFolderData>(),
            new[] { new Viewer.Core.Inventory.InventoryItemData(chair, root, "Oak chair", Viewer.Core.Inventory.ItemKind.Object, 6, 6, 0,
                new DateTimeOffset(2026, 3, 14, 12, 0, 0, TimeSpan.Zero), null, "A chair for the porch", null, rights) }));
        var panel = ItemPropertiesFeature.Install(shell);
        panel.Bind(model, new Viewer.Shell.Inventory.InventoryActions(model, _ => new ActionResult(ActionStatus.Sent)), id => id == creator ? "Example Maker" : "Test User");
        panel.ShowItem(chair);
    }

    /// <summary>The picture size of the interface scenes.</summary>
    private const int UiWidth = 960, UiHeight = 600;

    /// <summary>The scene names --tr-ui accepts: the in-world frame and each window on its own.</summary>
    public static readonly string[] UiScenes =
    [
        "inworld", "inventory", "inventory-150", "inventory-200", "itemproperties", "itemproperties-150", "itemproperties-200", "outfits", "people", "people-150", "people-200", "people-nearby", "people-nearby-150", "people-nearby-200", "people-rows", "people-rows-150", "people-rows-200", "conversations", "map", "minimap", "teleport", "notifications", "scriptdialog",
        "permission", "inspect", "land", "preferences", "preferences-general", "preferences-graphics", "preferences-chat", "preferences-moveview",
        "preferences-privacy", "preferences-cache", "preferences-sound", "preferences-sound-150", "preferences-sound-200", "preferences-sound-open", "preferences-sound-open-150", "preferences-sound-open-200", "reconnect", "login", "appearance", "appearance-shape", "appearance-shirt", "personal", "build",
        "build-look", "build-search", "build-create", "build-object", "build-150", "build-200", "build-contents-150", "build-contents-200", "build-look-150", "build-look-200", "build-placement", "build-placement-150", "build-placement-200", "build-create-150", "build-create-200", "build-object-150", "build-object-200", "colourpicker", "script", "script-error", "script-readonly", "script-engine", "script-engine-on", "script-engine-early", "script-engine-nosave", "script-engine-slua", "script-sl", "commandlist", "commandlist-search", "commandlist-setting",
        "notecard", "notecard-readonly", "gesture", "gestures", "newmenu", "scriptqueue",
        "texturepicker", "build-contents", "takefolder", "gridoptions", "objectcontents", "buy", "objectinfo",
        "gestures-used", "addons", "addon-ao", "addon-rlv",
        "upload-image", "upload-bulk", "upload-model", "upload-bulk-models", "snapshot", "snapshot-150", "snapshot-200", "about", "licences", "login-dialog",
        "music-ask", "music-ask-150", "music-ask-200",
        "pack-ask", "pack-ask-150", "pack-ask-200", "pack-failed", "pack-failed-150", "pack-failed-200",
        "hover", "hover-150", "hover-200", "hover-off",
    ];

    /// <summary>A question box is as tall as what it holds: every line of its words shown, its buttons inside it, and no more than two lines'
    /// worth of empty space between the words and the buttons.</summary>
    private void CheckQuestionFits(AcceptDialog dialog, string name)
    {
        var label = dialog.GetLabel();
        var button = dialog.GetOkButton();
        int lines = label.GetLineCount();
        float line = label.GetLineHeight(), spacing = label.GetThemeConstant("line_spacing");
        float wordsEnd = label.GetGlobalRect().Position.Y + lines * line + Math.Max(0, lines - 1) * spacing;
        float buttonTop = button.GetGlobalRect().Position.Y, gap = buttonTop - wordsEnd;
        _result["question"] = new { lines, line, gap, width = dialog.Size.X, height = dialog.Size.Y };
        if (gap > 2 * line)
            _failures.Add($"{name}: {gap:F0} px of empty space between the question's words and its buttons (a line is {line:F0} px, {lines} lines of words)");
        if (gap < 0)
            _failures.Add($"{name}: the question's words run under its buttons by {-gap:F0} px");
        if (label.GetVisibleLineCount() < lines)
            _failures.Add($"{name}: {lines - label.GetVisibleLineCount()} of the question's {lines} lines are not shown");
        if (button.GetGlobalRect().End.Y > dialog.Size.Y + 0.5f)
            _failures.Add($"{name}: the buttons reach past the bottom of the box");
    }

    /// <summary>The example script the script editor's pictures show (this viewer's own text).</summary>
    private const string ExampleScriptText =
        "// Says hello when touched\n" +
        "integer count = 0;\n" +
        "\n" +
        "default\n" +
        "{\n" +
        "    state_entry()\n" +
        "    {\n" +
        "        llSetText(\"Touch me\", <1.0, 1.0, 1.0>, 1.0);\n" +
        "    }\n" +
        "\n" +
        "    touch_start(integer total)\n" +
        "    {\n" +
        "        count += total\n" +
        "        llSay(0, \"Hello, \" + llDetectedName(0) + \"! Touches: \" + (string)count);\n" +
        "    }\n" +
        "}\n";

    /// <summary>The example Lua script of the SLua engine picture (this viewer's own text).</summary>
    private const string ExampleLuaText =
        "--!slua\n" +
        "-- Says hello when touched\n" +
        "local count = 0\n" +
        "\n" +
        "LLEvents:on(\"touch_start\", function(total)\n" +
        "    count += total\n" +
        "    ll.Say(0, \"Hello! Touches: \" .. count)\n" +
        "end)\n";

    /// <summary>
    /// The script editor with an example script: "script", a script inside an object with Save, Running (ticked, as the region answered),
    /// Reset and Mono, after a save that compiled; "script-error", the same script after a save whose compiler named a line (the error listed
    /// under the code and the caret put on its place by a click on it); "script-readonly", a library script opened read only. No session or grid.
    /// </summary>
    private static ScriptEditorPanel ExampleScript(ShellRoot shell, string scene)
    {
        static Guid G(int n) => new($"00000000-0000-4000-8000-{n:D12}");
        bool engineScene = scene.StartsWith("script-engine", StringComparison.Ordinal) || scene == "script-sl";
        var feature = new ScriptEditorFeature(shell);
        feature.Bind(null, openSim: scene != "script-sl");
        const uint full = 0x7fffffff;
        bool readOnly = scene == "script-readonly";
        var owner = readOnly ? G(3) : G(1);
        var access = new Viewer.Core.Inventory.ScriptAccess(G(20), G(21), G(1),
            new Viewer.Core.Inventory.NotecardPermissions(owner, owner, full, full, 0, readOnly ? Viewer.Core.Inventory.NotecardPermissions.Copy : 0, full),
            InLibrary: readOnly, ObjectId: readOnly ? Guid.Empty : G(30), LocalId: readOnly ? 0u : 7u);
        var panel = feature.Open(access, readOnly ? "Example library script" : "Example script");
        string text = scene == "script-error" ? ExampleScriptText : ExampleScriptText.Replace("count += total\n", "count += total;\n", StringComparison.Ordinal);
        if (engineScene)
        {
            ExampleEngineRow(panel, scene, text);
            return panel;
        }
        panel.ShowText(text);
        if (readOnly)
            return panel;
        panel.ShowRunning(true, true);
        if (scene == "script-error")
        {
            panel.ShowAnswer(new Viewer.Core.Inventory.ScriptSaveResult(Viewer.Core.Inventory.ScriptSaveStatus.Saved, false, G(22),
                [Viewer.Core.Inventory.ScriptMessage.Parse("(13, 8) : ERROR : Syntax error")]));
            panel.ErrorClicked(0);
        }
        else
            panel.ShowAnswer(new Viewer.Core.Inventory.ScriptSaveResult(Viewer.Core.Inventory.ScriptSaveStatus.Saved, true, G(22), []));
        return panel;
    }

    /// <summary>
    /// The script editor's engine row as a person meets it, for the guide's pictures. "script-engine": a region that lists YEngine and Alpha and
    /// keeps a choice, the tick clear and the greyed drop-down naming the engine the first line asks for; "script-engine-on": ticked, Alpha
    /// chosen; "script-engine-early": Alpha marked early access, picked, with the warning; "script-engine-nosave": the region lists engines
    /// but keeps no choice, so the tick is greyed; "script-engine-slua": a region that also lists SLua and runs Lua, SLua (Lua) picked; "script-sl": Second Life's four-way compiler list. No session or grid.
    /// </summary>
    private static void ExampleEngineRow(ScriptEditorPanel panel, string scene, string text)
    {
        var region = scene switch
        {
            "script-sl" => new Viewer.Core.Inventory.ScriptRegion([], true, false, false),
            "script-engine-nosave" => new Viewer.Core.Inventory.ScriptRegion(["YEngine", "Alpha"], false, false, false),
            "script-engine-slua" => new Viewer.Core.Inventory.ScriptRegion(["YEngine", "Alpha", "SLua"], true, true, false),
            "script-engine-early" => new Viewer.Core.Inventory.ScriptRegion(["YEngine", "Alpha"], false, true, false) { EarlyAccess = ["Alpha"] },
            _ => new Viewer.Core.Inventory.ScriptRegion(["YEngine", "Alpha"], false, true, false),
        };
        panel.Editing!.Region = region;
        // the first line names the engine the greyed drop-down shows
        panel.ShowText(scene is "script-engine" or "script-engine-nosave" ? "//YEngine:\n" + text
            : scene == "script-engine-slua" ? ExampleLuaText : text);
        panel.ShowRunning(true, true);
        if (scene is "script-engine-on" or "script-engine-early" or "script-engine-slua")
        {
            panel.Editing.SetChooseEngine(true);
            panel.Editing.Choose(scene == "script-engine-slua" ? Viewer.Core.Inventory.ScriptTarget.Luau : Viewer.Core.Inventory.ScriptTarget.Engine("Alpha"));
            if (scene == "script-engine-early")
                panel.EarlyWarning.Visible = true;
        }
        panel.ShowFixture();
    }

    /// <summary>
    /// The notecard editor with an example card holding an embedded landmark: "notecard", the user's own card, editable, with the caret at
    /// the end; "notecard-readonly", someone else's card that may be copied but not modified (Save hidden, "Read only" shown). No session or grid.
    /// </summary>
    private static NotecardPanel? ExampleNotecard(ShellRoot shell, bool readOnly)
    {
        static Guid G(int n) => new($"00000000-0000-4000-8000-{n:D12}");
        const uint full = 0x7fffffff;
        var feature = new NotecardFeature(shell);
        var owner = readOnly ? G(3) : G(1);
        var perms = new Viewer.Core.Inventory.NotecardPermissions(owner, owner, full, readOnly ? full & ~Viewer.Core.Inventory.NotecardPermissions.Modify : full, 0,
            readOnly ? Viewer.Core.Inventory.NotecardPermissions.Copy : 0, full);
        var access = new Viewer.Core.Inventory.NotecardAccess(G(40), G(41), G(1), perms, InLibrary: false);
        var landmark = new Viewer.Core.Inventory.NotecardEmbeddedItem(0, G(42), G(43), "Example landmark", "Example Region (128, 64, 22)", 3, 3, Viewer.Core.Inventory.ItemKind.Landmark,
            new Viewer.Core.Inventory.NotecardPermissions(G(1), G(1), full, full, 0, 0, full));
        string text = "Welcome!\n\nMeet us here: " + char.ConvertFromUtf32(Viewer.Core.Inventory.NotecardFormat.FirstEmbeddedChar)
            + "\n\nBring a friend, and see https://example.org/events for the times.";
        var doc = new Viewer.Core.Inventory.NotecardDocument(2, text, [landmark]);
        var opened = new Viewer.Shell.Inventory.NotecardOpenResult(Viewer.Shell.Inventory.NotecardOpenStatus.Ok, G(40), readOnly ? "Club rules" : "Example welcome",
            "Where to find us", access, doc);
        var panel = feature.ShowOpened(opened);
        if (panel is not null && !readOnly)
        {
            panel.Editor.SetCaretLine(panel.Editor.GetLineCount() - 1);
            panel.Editor.SetCaretColumn(panel.Editor.GetLine(panel.Editor.GetLineCount() - 1).Length);
        }
        return panel;
    }

    /// <summary>The gesture editor with an example gesture: a trigger and its replacement, Shift+F2, and five steps (an animation started,
    /// a sound, a line of chat, a timed wait and the animation stopped), the second step chosen. No session or grid.</summary>
    private static void ExampleGesture(ShellRoot shell, StringTable strings)
    {
        static Guid G(int n) => new($"00000000-0000-4000-8000-{n:D12}");
        var panel = new GesturePanel(strings);
        shell.AddFloater(new FloaterSpec(GesturePanel.FloaterId, "gesture.title", new UiSize(480, 560), new UiSize(400, 440)), panel);
        var doc = new Viewer.Core.Inventory.GestureDocument
        {
            Trigger = "/wave", Replace = "Hello!", Key = Viewer.Core.Inventory.GestureKeys.F2, Mask = Viewer.Core.Inventory.GestureKeys.MaskShift,
            Steps =
            {
                Viewer.Core.Inventory.GestureStep.Animation("Wave", G(50), stop: false),
                Viewer.Core.Inventory.GestureStep.Sound("Chime", G(51)),
                Viewer.Core.Inventory.GestureStep.Chat("waves hello"),
                Viewer.Core.Inventory.GestureStep.WaitTime(2f),
                Viewer.Core.Inventory.GestureStep.Animation("Wave", G(50), stop: true),
            },
        };
        panel.Show(doc, active: true, canEdit: true, [(G(50), "Wave"), (G(52), "Bow")], [(G(51), "Chime")]);
        panel.StepList.Select(1);
        panel.StepList.EmitSignal(ItemList.SignalName.ItemSelected, 1);
        shell.SetFloaterTitle(GesturePanel.FloaterId, "Wave hello");
        shell.Shell.Floaters.Open(GesturePanel.FloaterId);
    }

    /// <summary>The compile queue's window after a run over two objects whose contents never came (the wait ends at once): the queue's
    /// lines in the list, the count line under it and Close. No session or grid.</summary>
    private static void ExampleScriptQueue(ShellRoot shell, StringTable strings)
    {
        static Guid G(int n) => new($"00000000-0000-4000-8000-{n:D12}");
        var window = new ScriptQueueWindow(strings, () => { });
        shell.AddFloater(new FloaterSpec(ScriptQueueWindow.FloaterId, "queue.title.compile", new UiSize(300, 400), new UiSize(240, 240)), window.Root);
        var queue = new Viewer.Shell.Build.ScriptQueue(Viewer.Shell.Build.ScriptQueueAction.CompileMono,
            [new Viewer.Shell.Build.ScriptQueueObject(1, G(30), "Example door"), new Viewer.Shell.Build.ScriptQueueObject(2, G(31), "Example lamp")],
            G(1), _ => new ActionResult(ActionStatus.Sent), null, (_, _) => Task.CompletedTask);
        queue.RunAsync().GetAwaiter().GetResult();
        window.Show(queue);
        shell.SetFloaterTitle(ScriptQueueWindow.FloaterId, strings.Get(Viewer.Shell.Build.ScriptQueue.TitleKey(queue.Action)));
        shell.Shell.Floaters.Open(ScriptQueueWindow.FloaterId);
    }

    /// <summary>The Snapshot window with an example picture and a folder already chosen. No session or grid.</summary>
    private static void ExampleSnapshot(ShellRoot shell)
    {
        var settings = new SettingsService(null);
        settings.Set(Settings.SnapshotFileFormat, 1);   // JPEG, so the quality row is in the picture
        TravelFeature.Install(shell, settings);   // it makes the World menu, which Snapshot joins
        var window = SnapshotFeature.Install(shell, settings);
        shell.Shell.Floaters.Open(SnapshotFeature.FloaterId);
        window.ShowPreview(Backdrop());
        window.ShowFolder("Pictures/Reverie");
        window.ShowStatus(shell.Shell.Strings.Get("snapshot.ready", 1920, 1080));
    }

    /// <summary>The Gestures window with an example inventory: five gestures in the Gestures folder, two of them active (one with its
    /// trigger and Shift+F2, one whose file has a trigger only), the second row chosen. No session or grid.</summary>
    private static GesturesWindow ExampleGestureList(ShellRoot shell)
    {
        static Guid G(int n) => new($"00000000-0000-4000-8000-{n:D12}");
        Guid owner = G(1), root = G(2), libraryOwner = G(3), libraryRoot = G(4), folder = G(10);
        var model = new Viewer.Core.Inventory.InventoryModel((_, _) => { });
        var gestures = new Viewer.Core.Inventory.InventoryFolderData(folder, root, "Gestures", Viewer.Core.Inventory.FolderKind.Gesture, 1, 5);
        model.Apply(new Viewer.Core.Inventory.InventorySkeletonReceived(owner, root, libraryOwner, libraryRoot,
            [new Viewer.Core.Inventory.InventoryFolderData(root, Guid.Empty, "My Inventory", Viewer.Core.Inventory.FolderKind.Root, 1, -1), gestures],
            [new Viewer.Core.Inventory.InventoryFolderData(libraryRoot, Guid.Empty, "Library", Viewer.Core.Inventory.FolderKind.Root, 1, 0)]));
        string[] names = ["Wave hello", "Bow", "Clap", "Laugh", "Shrug"];
        model.Apply(new Viewer.Core.Inventory.InventoryFolderFetched(folder, true, 2, names.Length, [],
            names.Select((n, i) => new Viewer.Core.Inventory.InventoryItemData(G(20 + i), folder, n, Viewer.Core.Inventory.ItemKind.Gesture, 21, 20, 0, null)).ToArray()));
        var docs = new Dictionary<Guid, Viewer.Core.Inventory.GestureDocument>
        {
            [G(20)] = new() { Trigger = "/wave", Key = Viewer.Core.Inventory.GestureKeys.F2, Mask = Viewer.Core.Inventory.GestureKeys.MaskShift },
            [G(23)] = new() { Trigger = "/lol" },
        };
        var window = GesturesFeature.Install(shell);
        shell.Shell.Floaters.Open(GesturesFeature.FloaterId);
        window.Show(Viewer.Shell.Inventory.GestureListRows.Build(model, docs.Keys.ToArray(), id => docs.GetValueOrDefault(id), "Loading..."));
        window.ClickRow(G(22), add: false);
        return window;
    }

    /// <summary>Scrolls the Build window so the section's card is at the top of its view (the section comes after others and may be below the fold).</summary>
    private async Task ScrollSectionToTop(ShellRoot shell, string section)
    {
        if (shell.FindChild("section_box_" + section, true, false) is not Control box)
            return;
        ScrollContainer? scroll = null;
        for (Node? n = box.GetParent(); n is not null && scroll is null; n = n.GetParent())
            scroll = n as ScrollContainer;
        if (scroll is not null && box.GetParent() is Control card)
        {
            scroll.ScrollVertical = (int)(card.GetGlobalRect().Position.Y - scroll.GetGlobalRect().Position.Y + scroll.ScrollVertical);
            await Frames(4);
        }
    }


    /// <summary>The Build window with an example box selected (the Edit tool; a hollowed, twisted box), on an in-memory world with no
    /// session or grid. <paramref name="scene"/>: "build" every section closed (each shows its summary); "build-look" only Look open;
    /// "build-search" a search for "hollow" (the Shape section open on its two rows); the colour picker scenes as "build".</summary>
    private static Viewer.Shell.Build.EditController ExampleBuild(ShellRoot shell, StringTable strings, string scene = "build", Action<EditPanel>? panelOut = null)
    {
        static Guid G(int n) => new($"00000000-0000-4000-8000-{n:D12}");
        var world = new Viewer.Core.World.WorldState(new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "render"));
        var sim = new Viewer.Core.Session.SimRef(256000UL << 32 | 256000UL, "Example");
        world.BeginRegion(sim, new Viewer.Core.Session.RegionInfo("Example", 1000, 1000, 256, 256, false));
        const uint own = Viewer.Core.Interaction.ObjectFlagBits.YouOwner | Viewer.Core.Interaction.ObjectFlagBits.Modify | Viewer.Core.Interaction.ObjectFlagBits.Move
            | Viewer.Core.Interaction.ObjectFlagBits.Copy | Viewer.Core.Interaction.ObjectFlagBits.Transfer;
        var shape = Viewer.Core.Meshing.PrimShape.Box with { ProfileHollow = 0.5, PathTwist = 0.5 };
        world.ApplyObject(sim, new Viewer.Core.World.PrimData(1, G(31), 0, 0, new System.Numerics.Vector3(128.5f, 64.25f, 22.1f),
            Viewer.Core.Building.EditNumbers.FromEulerDegrees(new System.Numerics.Vector3(0, 0, 30)), new System.Numerics.Vector3(10f, 10f, 0.2f), Interaction: new Viewer.Core.World.PrimInteraction(scene == "build-object" ? Viewer.Core.Interaction.ClickAction.Buy : (byte)0, own), Shape: shape));
        var edit = new Viewer.Shell.Build.EditController(world, _ => new ActionResult(ActionStatus.Sent));
        edit.SelectTool(Viewer.Shell.Build.BuildTool.Edit);
        edit.SelectMany([1], shift: false);
        // the Object scene shows the sale (a copy for L$ 50) and a land impact; the Create scene the Create tool's options
        bool sold = scene == "build-object";
        edit.Receive(new Viewer.Core.Session.ObjectPropertiesReceived(G(31), "Floor", "Plank floor", G(32), G(32), Guid.Empty, null,
            0x7fffffff, 0, 1u << 19 | 1u << 15 | 1u << 13, sold ? 50 : 0, sold, sold ? (byte)2 : (byte)0));
        edit.Receive(new Viewer.Core.Session.ObjectCostReceived(G(31), 3.4f, 3.4f));
        if (scene == "build-create")
        {
            edit.SelectTool(Viewer.Shell.Build.BuildTool.Create);
            edit.ChooseShape(Viewer.Core.Building.CreateShape.Torus);
        }
        if (scene == "build-contents")
            edit.Receive(new TaskInventoryReceived(G(31), [.. ExampleTaskItems(), ExampleLongTaskItem()]));   // one name too long for the list: it must end in an ellipsis
        // the owner and creator are named as the session's name cache would name them (a neutral example name)
        var panel = new EditPanel(strings, () => edit, null, new SettingsService(null), () => G(32), id => id == G(32) ? "Test User" : null);
        shell.AddFloater(new FloaterSpec(EditPanel.FloaterId, "edit.title", new UiSize(460, 680), new UiSize(400, 520)), panel);
        foreach (var section in Viewer.Shell.Build.BuildLayout.Sections)
            panel.SetOpen(section, scene == "build-look" && section == Viewer.Shell.Build.BuildSection.Look
                || scene == "build-contents" && section == Viewer.Shell.Build.BuildSection.Contents
                || scene == "build-object" && section == Viewer.Shell.Build.BuildSection.Object
                || scene == "build-placement" && section is Viewer.Shell.Build.BuildSection.Placement or Viewer.Shell.Build.BuildSection.Shape);
        if (scene == "build-search")
            panel.SearchText = "hollow";
        panel.Refresh();
        panelOut?.Invoke(panel);
        return edit;
    }

    /// <summary>
    /// --tr-ui=SCENE [--tr-theme=PRESET] [--tr-alter=wrongtoken]: one interface scene drawn as the viewer draws it, in a look, against
    /// its golden. The windows are shown empty or with example content (no session, no grid). --tr-alter=wrongtoken draws the same
    /// scene with the accent and the text colour replaced by wrong values: the diff against the golden MUST fail.
    /// </summary>
    private async Task<int> UiSceneRun(string scene, string look, bool wrongToken, string? golden)
    {
        if (Array.IndexOf(ReverieScenes, scene) >= 0)
            return await ReverieSceneRun(scene, look, golden);   // the Reverie top bar and component gallery (TestRender.Reverie.cs)
        if (Array.IndexOf(UiScenes, scene) < 0)
            throw new ArgumentException("--tr-ui must be one of " + string.Join(", ", UiScenes) + ", " + string.Join(", ", ReverieScenes));
        // the Sound page at interface sizes 150 and 200 % is drawn in a picture 1.5 and 2 times as large (the same window, larger on screen);
        // "-open" has its collapsed sections open, as the page keeps them
        int pct = scene.EndsWith("-150", StringComparison.Ordinal) ? 150 : scene.EndsWith("-200", StringComparison.Ordinal) ? 200 : 100;
        bool soundOpen = scene.StartsWith("preferences-sound-open", StringComparison.Ordinal);
        if (scene.StartsWith("preferences-sound", StringComparison.Ordinal))
            scene = soundOpen ? "preferences-sound" : scene;
        if (pct != 100 && scene.StartsWith("build", StringComparison.Ordinal))
            scene = scene[..^4];   // a Build case at 150 or 200 %: the same window, drawn larger
        else if (pct != 100 && scene.StartsWith("snapshot", StringComparison.Ordinal))
            scene = "snapshot";
        else if (pct != 100 && (scene.StartsWith("music-ask", StringComparison.Ordinal) || scene.StartsWith("pack-", StringComparison.Ordinal)))
            scene = scene[..^4];
        else if (pct != 100 && (scene.StartsWith("inventory", StringComparison.Ordinal) || scene.StartsWith("itemproperties", StringComparison.Ordinal)))
            scene = scene[..^4];
        else if (pct != 100 && (scene.StartsWith("people-", StringComparison.Ordinal) || scene.StartsWith("hover-", StringComparison.Ordinal)))
            scene = scene[..^4];
        else if (pct != 100)
            scene = "preferences-sound";
        // --tr-ui-small-window: not a larger picture but the same one, the window staying the smallest supported size on screen, so at
        // 150 and 200 percent the layout has that much less room (a by-hand look, not a case of the render set: it has no golden)
        bool smallWindow = OS.GetCmdlineUserArgs().Contains("--tr-ui-small-window");
        int picW = smallWindow ? 1152 : UiWidth * pct / 100, picH = smallWindow ? 648 : UiHeight * pct / 100;
        var strings = StringTable.English();
        // NV_UI_NORESTYLE=1: the windows as the classic look built them, for the before list of controls (NV_UI_CONTROLS=1)
        ReverieRestyle.Enabled = System.Environment.GetEnvironmentVariable("NV_UI_NORESTYLE") != "1";
        var tokens = ThemePresets.Get(look);
        if (wrongToken)
            tokens = tokens with { Accent = new UiColour(1f, 0f, 1f), Text = new UiColour(0f, 1f, 0f) };
        UiTheme.Apply(tokens, pct);
        var rig = MakeRig(new Vector2I(picW, picH));
        if (pct != 100)
        {
            rig.Viewport.Size2DOverride = new Vector2I(picW * 100 / pct, picH * 100 / pct);
            rig.Viewport.Size2DOverrideStretch = true;
        }
        var shell = new ShellRoot(new ShellController(strings, () => { })) { FixedClockText = TestClockText };
        if (pct != 100)
            shell.ScaleSink = _ => { };   // the override above stands in for the window's content scale
        rig.Viewport.AddChild(shell);
        var settings = new SettingsService(null);
        if (soundOpen)
            settings.SetText(PrefPageView.SoundSectionsKey, "interface,more");
        ThemeStore.Save(settings, "render", ThemeChoice.Default with { Preset = ThemePresets.Classic });   // the stored look the pictures were made with (the Classic preset)
        settings.Set(Settings.DrawDistance, 1024);   // the pictures keep a distance of 1,024 (no level has it: Custom), whatever the default level is

        // a stand-in for the 3D view: a sky-to-ground blend, so the bars and windows are seen over a scene
        var backdrop = new TextureRect { Name = "backdrop", Texture = Backdrop(), StretchMode = TextureRect.StretchModeEnum.Scale, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, MouseFilter = Control.MouseFilterEnum.Ignore };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        shell.MainView.AddChild(backdrop);

        string open = "";
        LoginScreen? loginShown = null;
        NotecardPanel? notecardShown = null;
        InventoryPanel? newMenuOf = null;
        EditPanel? contentsPanel = null;
        FacePickerWindows? texturePickers = null;
        FolderPickPanel? takeFolder = null;
        AcceptDialog? askShown = null;
        HoverHeightWindow? hoverShown = null;
        switch (scene)
        {
            case "login":
                var login = new LoginScreen(strings, null);
                shell.MainView.AddChild(login);
                login.ShowExampleGrid(ExampleGrid());
                login.ApplyBranding(ExampleBranding());
                ((LineEdit)login.ById["password"]).Text = new string('x', 64);   // a long password: its dots must stop before the show button
                loginShown = login;
                break;
            case "reconnect":
                var model = new LoginModel(strings, _ => throw new InvalidOperationException("no login in a render case"), null);
                var drop = new DropPanel(strings, new Viewer.Shell.Reconnect.ReconnectModel(model, strings, (_, _) => Task.CompletedTask));
                shell.MainView.AddChild(drop);
                drop.ShowNotice(new Viewer.Shell.Reconnect.DropNotice(strings.Get("drop.title"), strings.Get("drop.body"), strings.Get("drop.reason.unknown"), true));
                break;
            case "login-dialog":
                var dialogLogin = new LoginScreen(strings, null);
                shell.MainView.AddChild(dialogLogin);
                dialogLogin.ShowExampleGrid(ExampleGrid());
                dialogLogin.ApplyBranding(ExampleBranding());
                dialogLogin.Dialogs.Show(LoginDialogs.TrustCertificate(strings, ExampleCertificate()));
                break;
            case "script":
            case "script-error":
            case "script-readonly":
            case "script-engine":
            case "script-engine-on":
            case "script-engine-early":
            case "script-engine-nosave":
            case "script-engine-slua":
            case "script-sl":
                ExampleScript(shell, scene);
                break;
            case "notecard":
            case "notecard-readonly":
                notecardShown = ExampleNotecard(shell, scene == "notecard-readonly");
                break;
            case "gesture":
                ExampleGesture(shell, strings);
                break;
            case "gestures":
                ExampleGestureList(shell);
                break;
            case "snapshot":
                ExampleSnapshot(shell);
                break;
            case "music-ask":   // the parcel music question, built and shown as the viewer does it
                rig.Viewport.GuiEmbedSubwindows = true;
                var musicAsk = Main.BuildMusicAsk(strings);
                shell.AddChild(musicAsk);
                Main.ShowMusicAsk(musicAsk, strings, "radio.example.org");
                askShown = musicAsk;
                break;
            case "pack-ask":   // the question before a pack's download, built and shown as the viewer does it
                rig.Viewport.GuiEmbedSubwindows = true;
                var packAsk = Main.BuildPackAsk(strings);
                shell.AddChild(packAsk);
                Main.ShowPackQuestion(packAsk, strings, "video", 52_000_000, "downloads.example.org");
                askShown = packAsk;
                break;
            case "pack-failed":   // the word after a download that did not end well (the longer of its two texts)
                rig.Viewport.GuiEmbedSubwindows = true;
                var packDone = Main.BuildPackDone(strings);
                shell.AddChild(packDone);
                Main.ShowPackDone(packDone, strings, "web", installed: false);
                askShown = packDone;
                break;
            case "gestures-used":
                ExampleGestureListUsed(shell);
                break;
            case "scriptqueue":
                ExampleScriptQueue(shell, strings);
                break;
            case "scriptdialog":
            case "permission":
                ScriptPanel(shell, strings, scene == "permission");
                break;
            default:
                var chat = ChatFeature.Install(shell, settings);
                var peoplePanel = PeopleFeature.Install(shell, settings, chat.Conversations);
                if (scene == "people")
                    peoplePanel.Tabs.CurrentTab = 1;   // this picture checks the friends list; the window opens on Nearby
                if (scene == "people-rows")
                {
                    peoplePanel.Nearby.Source = () =>
                    [
                        (new Viewer.Core.Social.NearbyAvatar(Guid.Parse("00000000-0000-0000-0000-00000000000a"), 4.2f, false), "Test User"),
                        (new Viewer.Core.Social.NearbyAvatar(Guid.Parse("00000000-0000-0000-0000-00000000000b"), 37.5f, false), "Example Resident With A Long Name"),
                        (new Viewer.Core.Social.NearbyAvatar(Guid.Parse("00000000-0000-0000-0000-00000000000c"), 112.0f, true), "Another Example"),
                    ];
                    peoplePanel.Nearby.Refresh();
                }
                var inventoryPanel = InventoryFeature.Install(shell);
                if (scene is "inventory" or "newmenu")
                    ExampleInventoryDrag(inventoryPanel, dragCue: scene == "inventory");
                if (scene == "newmenu")
                {
                    newMenuOf = inventoryPanel;
                    rig.Viewport.GuiEmbedSubwindows = true;   // the menus are drawn inside the picture
                }
                OutfitFeature.Install(shell);
                if (scene == "itemproperties")
                    ExampleItemProperties(shell);
                var appearancePanel = AppearanceFeature.Install(shell, redrawGapMs: 0);
                if (scene.StartsWith("appearance", StringComparison.Ordinal))
                    await ExampleAppearance(appearancePanel, scene);
                TravelFeature.Install(shell, settings);
                var noticePanels = NotificationFeature.Install(shell);
                var interactPanels = InteractFeature.Install(shell);
                PersonalLightingFeature.Install(shell, new Viewer.Core.World.PersonalLighting(), () => null);
                if (scene.StartsWith("build", StringComparison.Ordinal) || scene is "colourpicker" or "texturepicker" or "gridoptions")
                {
                    var buildEdit = ExampleBuild(shell, strings, scene, p => contentsPanel = p);
                    if (scene == "colourpicker")
                        ColourPickerWindow.Install(shell).Open(new ColourRequest("edit.picker.colour.title", new UiColour(0.8f, 0.3f, 0.2f), false, _ => { }));
                    else if (scene == "texturepicker")
                        texturePickers = ExampleTexturePicker(shell, strings, buildEdit);
                    else if (scene == "gridoptions")
                        ExampleGridOptions(shell, strings, buildEdit);
                    else if (scene == "build-contents")
                        rig.Viewport.GuiEmbedSubwindows = true;   // the right-click menu is drawn inside the picture
                }
                if (scene is "addons" or "addon-ao" or "addon-rlv")
                    ExampleAddons(shell, strings, scene);
                if (scene.StartsWith("upload-", StringComparison.Ordinal))
                    ExampleUploads(shell);
                if (scene == "takefolder")
                    takeFolder = ExampleTakeFolder(shell, strings);
                if (scene is "objectcontents" or "buy")
                    ExampleTrade(interactPanels, shell, scene);
                if (scene == "objectinfo")
                    ExampleObjectInfo(noticePanels, shell);
                var prefs = PreferencesFeature.Install(shell, settings, () => 0, () => false);
                if (scene.StartsWith("preferences", StringComparison.Ordinal))
                    prefs.ShowCategory(scene == "preferences" ? "interface" : scene["preferences-".Length..]);   // "preferences" is the Interface page
                if (scene.StartsWith("commandlist", StringComparison.Ordinal))
                    ExampleCommandList(shell, settings, prefs, scene);
                if (scene.StartsWith("hover", StringComparison.Ordinal))
                    hoverShown = HoverHeightFeature.Install(shell);
                open = scene switch
                {
                    "inworld" => "",
                    "inventory" or "newmenu" => InventoryFeature.FloaterId,
                    "itemproperties" => ItemPropertiesFeature.FloaterId,
                    "outfits" => OutfitFeature.FloaterId,
                    "people" or "people-nearby" or "people-rows" => PeopleFeature.PeopleId,
                    "hover" or "hover-off" => HoverHeightFeature.FloaterId,
                    "conversations" => ChatFeature.ConversationsId,
                    "map" => TravelFeature.MapId,
                    "minimap" => TravelFeature.MiniMapId,
                    "teleport" => TravelFeature.TeleportId,
                    "notifications" => NotificationFeature.ListId,
                    "appearance" or "appearance-shape" or "appearance-shirt" => AppearanceFeature.FloaterId,
                    "inspect" => InteractFeature.InspectId,
                    "land" => InteractFeature.LandId,
                    "personal" => PersonalLightingFeature.FloaterId,
                    "build" or "build-look" or "build-placement" or "build-search" or "build-contents" or "build-create" or "build-object" => EditPanel.FloaterId,
                    "colourpicker" or "texturepicker" or "gridoptions" or "takefolder" or "objectcontents" or "buy" or "objectinfo" or "addons" or "addon-ao" or "addon-rlv" => "",
                    "upload-image" => BuildFeature.ImageFloaterId,
                    "upload-bulk" => BuildFeature.BulkFloaterId,
                    "upload-model" => ModelFeature.ModelFloaterId,
                    "upload-bulk-models" => ModelFeature.BulkModelsFloaterId,
                    "about" => ShellController.AboutFloaterId,
                    "licences" => ShellController.LicencesFloaterId,
                    "commandlist" or "commandlist-search" or "commandlist-setting" => "",
                    _ => PreferencesFeature.FloaterId,
                };
                break;
        }
        if (scene.StartsWith("commandlist", StringComparison.Ordinal))
        {
            shell.ShowWorldRows(true);
            shell.SetConnected(true);
            shell.SetBrand(ExampleBranding().Name);
            shell.SetLocation(new LocationParts(null, "Example Region", "128, 128, 30"));
            await Frames(2);
            shell.PlaceCommandList();
        }
        if (scene == "inworld")
        {
            shell.ShowWorldRows(true);
            shell.SetConnected(true);
            shell.SetBrand(ExampleBranding().Name);
            shell.SetLocation(new LocationParts(null, "Example Region", "128, 128, 30"));
            shell.SetUnread(3);
            shell.Shell.Floaters.Open(ChatFeature.NearbyId);
            shell.Shell.Floaters.Open(PeopleFeature.PeopleId);
            shell.Shell.Floaters.Open(PreferencesFeature.FloaterId);
        }
        else if (open.Length > 0)
            shell.Shell.Floaters.Open(open);
        await Frames(10);
        if (hoverShown is not null)
        {
            // an example height in a region that takes it; "hover-off" is a region that does not, where each greyed control says why
            bool takes = scene != "hover-off";
            hoverShown.ShowHeight(takes ? 0.25f : 0f);
            hoverShown.ShowSupport(takes);
            await Frames(4);
            string why = strings.Get("hover.unsupported");
            if (!takes && (hoverShown.Slider.TooltipText != why || hoverShown.Entry.GetLineEdit().TooltipText != why))
                _failures.Add("hover-off: a greyed hover height control has no tooltip saying why");
        }
        if (scene == "preferences-sound" && soundOpen && shell.FindChild("page_sound", true, false) is PrefPageView soundView
            && soundView.FindChild("section_more", true, false) is Control moreSection)
        {
            // the picture of the opened sections starts at More, so the table of interface sounds is in it
            soundView.ScrollVertical = (int)(moreSection.GlobalPosition.Y - soundView.GetGlobalRect().Position.Y + soundView.ScrollVertical - 6);
            await Frames(4);
        }
        if (newMenuOf is not null)
        {
            newMenuOf.ShowNewMenu(clothing: true);   // the + menu with New Clothes open
            await Frames(10);
        }
        if (texturePickers is not null)
        {
            texturePickers.Items.List.Select(2);   // a row chosen once the list has filled
            await Frames(4);
        }
        if (takeFolder is not null)
        {
            takeFolder.List.Select(1);
            await Frames(4);
        }
        if (scene == "build-contents" && contentsPanel is not null)
        {
            contentsPanel.ContentTab.OpenMenuOnRow(1);   // the right-click menu on the second row (the notecard): its entries are read by the layout check
            await Frames(10);
            contentsPanel.ContentTab.CloseMenu();   // the picture shows the list without the menu over the row ends, where a long name must end in an ellipsis
            await Frames(4);
        }
        if (scene == "build-object" && shell.FindChild("section_box_object", true, false) is Control objectBox)
        {
            // the Object section is below the fold: the window is scrolled so its card is at the top, and its controls must be in view
            await Frames(4);
            ScrollContainer? objectScroll = null;
            for (Node? n = objectBox.GetParent(); n is not null && objectScroll is null; n = n.GetParent())
                objectScroll = n as ScrollContainer;
            if (objectScroll is not null && objectBox.GetParent() is Control objectCard)
            {
                objectScroll.ScrollVertical = (int)(objectCard.GetGlobalRect().Position.Y - objectScroll.GetGlobalRect().Position.Y + objectScroll.ScrollVertical);
                await Frames(4);
                // then to the sale and click rows, which the case exists to show: their top a little below the top of the view
                if (shell.FindChild("sale_row", true, false) is Control saleRow)
                {
                    objectScroll.ScrollVertical = (int)(saleRow.GetGlobalRect().Position.Y - objectScroll.GetGlobalRect().Position.Y + objectScroll.ScrollVertical - 60);
                    await Frames(4);
                    var sv = objectScroll.GetGlobalRect();
                    var clickBottom = (shell.FindChild("click_row", true, false) as Control)?.GetGlobalRect().End.Y ?? float.MaxValue;
                    if (clickBottom > sv.End.Y)
                        _failures.Add($"build-object: the click action row ({clickBottom:F0}) is below the Build window's view ({sv.End.Y:F0})");
                }
                else
                    _failures.Add("build-object: the For sale row was not found");
            }
            else
                _failures.Add("build-object: the Object section's scroll area was not found");
        }
        if (scene == "build-placement" && shell.FindChild("rotation_line", true, false) is Control rotationLine)
        {
            // the Placement section is open above the Shape section: the window is scrolled to the Rotation row, so the last three-value row, the
            // copy and paste buttons and the first Shape rows are in the picture (Position and Size stand above it, checked by the layout check)
            await Frames(4);
            ScrollContainer? placeScroll = null;
            for (Node? n = rotationLine.GetParent(); n is not null && placeScroll is null; n = n.GetParent())
                placeScroll = n as ScrollContainer;
            if (placeScroll is not null)
            {
                placeScroll.ScrollVertical = (int)(rotationLine.GetGlobalRect().Position.Y - placeScroll.GetGlobalRect().Position.Y + placeScroll.ScrollVertical - 8);
                await Frames(4);
            }
        }
        if (scene == "build-look" && shell.FindChild("section_box_look", true, false) is Control lookBox)
        {
            // the case shows the open Look section: its body must be in the Build window's view, not below the fold
            await Frames(4);
            ScrollContainer? lookScroll = null;
            for (Node? n = lookBox.GetParent(); n is not null && lookScroll is null; n = n.GetParent())
                lookScroll = n as ScrollContainer;
            var body = lookBox.FindChild("look_page", false, false) as Control;
            if (lookScroll is not null && lookBox.GetParent() is Control lookCard)
            {
                // Contents comes first and Look is below the fold: the window is scrolled so the Look section's card is at the top
                lookScroll.ScrollVertical = (int)(lookCard.GetGlobalRect().Position.Y - lookScroll.GetGlobalRect().Position.Y + lookScroll.ScrollVertical);
                await Frames(4);
            }
            if (lookScroll is null || body is null)
                _failures.Add("build-look: the Look section's body or its scroll area was not found");
            else
            {
                var view = lookScroll.GetGlobalRect();
                var b = body.GetGlobalRect();
                _result["look_in_view"] = new { view = new[] { view.Position.Y, view.End.Y }, body = new[] { b.Position.Y, b.End.Y } };
                if (b.Position.Y < view.Position.Y - 0.5f || b.Position.Y + 24 > view.End.Y)
                    _failures.Add($"build-look: the Look section's body ({b.Position.Y:F0}) is not in the Build window's view ({view.Position.Y:F0} to {view.End.Y:F0})");
            }
        }
        if (scene == "build-contents")
        {
            CheckBuildLayout(shell, scene, pct, contentsPanel, " as opened");   // the window as it opens: the Contents buttons must be whole there too
            await ScrollSectionToTop(shell, "contents");   // the Contents card at the top of the window: its buttons are checked there, and the picture shows them
        }
        if (scene.StartsWith("build", StringComparison.Ordinal))
        {
            CheckBuildLayout(shell, scene, pct, contentsPanel);
            // and again in the Build window's smallest size (400 x 520), where the same layout rules hold
            FloaterFrame? buildFrame = null;
            for (Node? n = shell.FindChild("edit_panel", true, false); n is not null && buildFrame is null; n = n.GetParent())
                buildFrame = n as FloaterFrame;
            if (buildFrame is not null)
            {
                var keepSize = buildFrame.Size;
                buildFrame.Size = new Vector2(400, 520);
                await Frames(6);
                if (scene == "build-contents")
                    await ScrollSectionToTop(shell, "contents");
                CheckBuildLayout(shell, scene, pct, contentsPanel, " smallest");
                buildFrame.Size = keepSize;   // the picture is of the window at its usual size
                await Frames(6);
                if (scene == "build-contents")
                    await ScrollSectionToTop(shell, "contents");
            }
            else
                _failures.Add($"{scene}@{pct}: the Build window's frame was not found, so its smallest size was not checked");
        }
        if (loginShown is not null && loginShown.ById["password"] is LineEdit field && loginShown.ById["reveal"] is Control eye)
        {
            // the field's text area ends where its face's right margin begins; the show button must start at or after it
            float textEnd = field.GetGlobalRect().End.X - field.GetThemeStylebox("normal").ContentMarginRight;
            float eyeStart = eye.GetGlobalRect().Position.X;
            _result["password_room"] = new { textEnd, eyeStart };
            if (textEnd > eyeStart + 0.5f)
                _failures.Add($"the password field's text runs under its show button: text to {textEnd:F1}, button from {eyeStart:F1}");
        }
        if (notecardShown is not null)
        {
            // every embedded item's link covers its whole name: each run the overlay draws is at least as wide as its text in the editor's font
            var editor = notecardShown.Editor;
            var font = editor.GetThemeFont("font");
            int size = editor.GetThemeFontSize("font_size");
            var runs = editor.LinkRuns();
            var found = new List<object>();
            foreach (var run in runs)
            {
                float textWidth = font.GetStringSize(run.Text, HorizontalAlignment.Left, -1, size).X;
                found.Add(new { text = run.Text, x = run.Row.Position.X, width = run.Row.Size.X, textWidth });
                if (run.Row.Size.X + 0.5f < textWidth)
                    _failures.Add($"the link over \"{run.Text}\" is {run.Row.Size.X:F1} px wide, its text {textWidth:F1} px: the name's end is not covered");
            }
            _result["item_links"] = found;
            if (runs.Count == 0)
                _failures.Add("the notecard shows no embedded item link");
        }
        if (askShown is not null)
            CheckQuestionFits(askShown, $"{scene}@{pct}");
        if (scene == "pack-ask" && askShown is ConfirmationDialog download && !download.GetCancelButton().HasFocus())
            _failures.Add($"{scene}@{pct}: Enter must not start a download: the refusing button does not have the focus");
        var img = rig.Texture.GetImage();
        img.Convert(Image.Format.Rgba8);
        if (scene == "preferences-sound" && !wrongToken)
            SoundPageStandard(shell, soundOpen, pct, img);
        if (loginShown is not null)
            LinkUnderlines(loginShown, img, rig.Viewport.GetVisibleRect().Size);
        Directory.CreateDirectory(_out);
        img.SavePng(Path.Combine(_out, _tag + ".png"));
        if (System.Environment.GetEnvironmentVariable("NV_UI_CONTROLS") == "1")
            File.WriteAllLines(Path.Combine(_out, _tag + ".controls.tsv"), ControlList(shell));
        _result["ui"] = new { scene, look, wrongToken };
        if (golden is not null)
        {
            if (!Path.IsPathFullyQualified(golden) || !File.Exists(golden))
                _failures.Add("golden not found: " + Path.GetFileName(golden));
            else
            {
                var g = Image.LoadFromFile(golden);
                g.Convert(Image.Format.Rgba8);
                var diff = g.GetWidth() != picW || g.GetHeight() != picH
                    ? new DiffResult(false, g.GetWidth(), g.GetHeight(), 0, 0, 0, 0, "golden size differs")
                    : ImageDiff.Compare(img.GetData(), g.GetData(), picW, picH, 4, DiffTolerance.Ui);
                _result["diff"] = new Dictionary<string, object?>
                {
                    ["golden"] = Path.GetFileName(golden), ["pass"] = diff.Pass, ["differing"] = diff.Differing,
                    ["differing_fraction"] = diff.DifferingFraction, ["mean_abs_error"] = Math.Round(diff.MeanAbsError, 4),
                    ["max_channel_error"] = diff.MaxChannelError, ["tolerance"] = DiffTolerance.Ui, ["summary"] = diff.ToString(),
                };
                if (!diff.Pass)
                    _failures.Add("diff " + diff);
            }
        }
        shell.QueueFree();
        if (scene == "preferences" && !wrongToken)
        {
            await PreferencesFit(strings, tokens);
            await PreferencesLayout(strings, tokens);
        }
        if (scene is "script" or "notecard" or "appearance-shape" && !wrongToken)
            await UnsavedPromptFit(scene, strings, tokens);
        UiTheme.Apply(ThemePresets.Get(ThemePresets.Classic), 100);
        return _failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// The Preferences window's right margin, checked after the picture is taken (the picture does not change): on every page, at the
    /// interface sizes 100, 150 and 200 % (drawn as the window applies its content scale) and at window widths from the window's minimum
    /// to the widest the main view allows (every 40 px, plus the minimum, the default and the widest; one height), nothing seen in the window comes nearer its right border than the content is to its
    /// left border (the room between the border and the list of pages). What is seen is the control as its scroll areas cut it: a page
    /// wider than its place scrolls sideways inside that margin, while content that runs under the border fails.
    /// </summary>
    /// <summary>The login card's account links are drawn with their underline, on the picture itself: below each shown link's text there is a
    /// row whose pixels differ from the card's background across at least 80 percent of the link's width (a glyph row never covers that much).
    /// A scroll area that clips the underline away fails here.</summary>
    private void LinkUnderlines(LoginScreen login, Image img, Vector2 layoutSize)
    {
        float sx = img.GetWidth() / layoutSize.X, sy = img.GetHeight() / layoutSize.Y;
        var found = new List<object>();
        foreach (var name in new[] { "register", "forgot" })
        {
            if (login.FindChild(name, true, false) is not LinkButton link || !link.IsVisibleInTree())
                continue;
            var r = link.GetGlobalRect();
            int x0 = (int)MathF.Ceiling(r.Position.X * sx), x1 = (int)MathF.Floor(r.End.X * sx) - 1;
            int yTop = (int)MathF.Floor(r.Position.Y * sy), yEnd = Math.Min(img.GetHeight() - 1, (int)MathF.Ceiling(r.End.Y * sy) + 8);
            if (x1 <= x0 || yTop < 0 || yTop >= img.GetHeight())
                continue;
            var bg = img.GetPixel(Math.Max(0, x0 - 3), yTop);   // the card just left of the link, above its text
            var ink = link.GetThemeColor("font_color");
            float span = MathF.Max(0.15f, MathF.Abs(ink.R - bg.R) + MathF.Abs(ink.G - bg.G) + MathF.Abs(ink.B - bg.B));
            int width = x1 - x0 + 1, best = 0, bestRow = -1;
            for (int y = yTop + (int)(r.Size.Y * sy / 2); y <= yEnd; y++)
            {
                int count = 0;
                for (int x = x0; x <= x1; x++)
                {
                    var p = img.GetPixel(x, y);
                    if (MathF.Abs(p.R - bg.R) + MathF.Abs(p.G - bg.G) + MathF.Abs(p.B - bg.B) > span * 0.4f)
                        count++;
                }
                if (count > best) { best = count; bestRow = y; }
            }
            found.Add(new { name, rect = new[] { r.Position.X, r.Position.Y, r.End.X, r.End.Y }, width, best, row = bestRow });
            if (best < width * 0.8f)
                _failures.Add($"the {name} link is drawn without its underline: the fullest row below its text covers {best} of {width} px");
        }
        _result["link_underlines"] = found;
    }

    private async Task PreferencesFit(StringTable strings, ThemeTokens tokens)
    {
        const float slack = 0.5f;
        var problems = new List<string>();
        var table = new List<string>();
        int measured = 0;
        float worst = float.MaxValue;
        foreach (int pct in new[] { 100, 150, 200 })
        {
            float s = pct / 100f;
            UiTheme.Apply(tokens, pct);
            var rig = MakeRig(new Vector2I(UiWidth, UiHeight));
            rig.Viewport.Size2DOverride = new Vector2I((int)MathF.Round(UiWidth / s), (int)MathF.Round(UiHeight / s));
            rig.Viewport.Size2DOverrideStretch = true;
            var shell = new ShellRoot(new ShellController(strings, () => { })) { ScaleSink = _ => { }, FixedClockText = TestClockText };   // the override above stands in for the window's content scale
            rig.Viewport.AddChild(shell);
            var settings = new SettingsService(null);
            ThemeStore.Save(settings, "render", ThemeChoice.Default with { Preset = ThemePresets.Classic });
            var prefs = PreferencesFeature.Install(shell, settings, () => 0, () => false);
            var floaters = shell.Shell.Floaters;
            floaters.Open(PreferencesFeature.FloaterId);
            await Frames(8);
            Node? up = prefs;
            while (up is not null and not FloaterFrame)
                up = up.GetParent();
            if (up is not FloaterFrame frame || prefs.FindChild("categories_scroll", true, false) is not Control nav)
            {
                problems.Add($"at {pct} %: the Preferences window or its list of pages is not shown");
                rig.Viewport.QueueFree();
                continue;
            }
            var spec = floaters.SpecOf(PreferencesFeature.FloaterId)!;
            float minW = Math.Min(spec.MinSize.W, floaters.Bounds.W), maxW = floaters.Bounds.W;
            var widths = new SortedSet<float> { minW, maxW, Math.Clamp(spec.DefaultSize.W, minW, maxW) };
            for (float w = minW; w < maxW; w += 40)
                widths.Add(w);
            float height = floaters.RectOf(PreferencesFeature.FloaterId)?.H ?? spec.DefaultSize.H;
            foreach (float w in widths)
            {
                floaters.Move(PreferencesFeature.FloaterId, floaters.Bounds.X, floaters.Bounds.Y);
                floaters.Resize(PreferencesFeature.FloaterId, w, height);
                foreach (var page in PreferencesPanel.Categories)
                {
                    prefs.ShowCategory(page);
                    await Frames(3);
                    var box = frame.GetGlobalRect();
                    float left = nav.GetGlobalRect().Position.X - box.Position.X;
                    var under = new List<Control>();
                    var stack = new Stack<Node>();
                    stack.Push(prefs);
                    if (frame.FindChild("footer_pad", true, false) is { } footer)
                        stack.Push(footer);
                    while (stack.Count > 0)
                    {
                        var node = stack.Pop();
                        foreach (var child in node.GetChildren())
                            stack.Push(child);
                        if (node is Control { } c && c is not Container && c.IsVisibleInTree() && c.Size.X > 0)
                            under.Add(c);
                    }
                    // the control nearest the right border on this page, at this size
                    Control? nearest = null;
                    float nearestRight = float.MaxValue;
                    foreach (var c in under)
                    {
                        // what is seen of the control: a scroll area (or any control that clips) cuts it at its own edge
                        var seen = c.GetGlobalRect();
                        for (var a = c.GetParent() as Control; a is not null && a != frame; a = a.GetParent() as Control)
                            if (a.ClipContents)
                                seen = seen.Intersection(a.GetGlobalRect());
                        if (seen.Size.X <= 0 || seen.Size.Y <= 0)
                            continue;   // scrolled out of view
                        float right = box.End.X - seen.End.X;
                        measured++;
                        if (right < nearestRight)
                            (nearest, nearestRight) = (c, right);
                    }
                    if (nearest is null)
                        continue;
                    worst = Math.Min(worst, nearestRight - left);
                    string line = $"at {pct} %, window {box.Size.X:F0} px wide, page {page}: {nearest.Name} ({nearest.GetClass()}) ends {nearestRight:F1} px from the right border, the content starts {left:F1} px from the left one";
                    table.Add(line);
                    if (nearestRight < left - slack)
                        problems.Add(line);
                }
            }
            rig.Viewport.QueueFree();
            await Frames(2);
        }
        _result["preferences_fit"] = new { measured, worst_right_minus_left = worst, problems = problems.Count, nearest_per_page = table };
        foreach (var line in problems.Take(6))
            _failures.Add("preferences margin: " + line);
        if (problems.Count > 6)
            _failures.Add($"preferences margin: {problems.Count - 6} more (preferences_fit in the result)");
        if (measured == 0)
            _failures.Add("preferences margin: nothing measured");
    }

    /// <summary>
    /// The "Save changes?" question of the script and notecard editors and of the Appearance window (scene "appearance-shape", its Edit tab),
    /// checked after the picture is taken: at the interface sizes 100, 150 and 200 %, with the window at its minimum size and at its default
    /// size, the question is shown and its three buttons (Save, Don't Save, Cancel) are each wholly seen: inside the window, and not cut by
    /// any scroll area or clipping control between them and it.
    /// </summary>
    private async Task UnsavedPromptFit(string scene, StringTable strings, ThemeTokens tokens)
    {
        const float slack = 0.5f;
        var problems = new List<string>();
        var table = new List<string>();
        int measured = 0;
        bool appearance = scene == "appearance-shape";
        string kind = appearance ? "appearance" : scene;
        string[] buttonNames = appearance ? new[] { "unsaved_save", "unsaved_discard", "unsaved_cancel" } : new[] { "prompt_save", "prompt_dont_save", "prompt_cancel" };
        foreach (int pct in new[] { 100, 150, 200 })
        {
            float s = pct / 100f;
            UiTheme.Apply(tokens, pct);
            var rig = MakeRig(new Vector2I(UiWidth, UiHeight));
            rig.Viewport.Size2DOverride = new Vector2I((int)MathF.Round(UiWidth / s), (int)MathF.Round(UiHeight / s));
            rig.Viewport.Size2DOverrideStretch = true;
            var shell = new ShellRoot(new ShellController(strings, () => { })) { ScaleSink = _ => { }, FixedClockText = TestClockText };
            rig.Viewport.AddChild(shell);
            Control panel;
            if (appearance)
            {
                var appearancePanel = AppearanceFeature.Install(shell, redrawGapMs: 0);
                await AppearanceExample.Show(appearancePanel, scene, _ => new ActionResult(ActionStatus.Failed, 0, "failed"));   // the shape open on the Edit tab with a slider moved: unsaved changes; the grid refuses a save
                shell.Shell.Floaters.Open(AppearanceFeature.FloaterId);
                panel = appearancePanel;
            }
            else
                panel = scene == "script" ? ExampleScript(shell, "script") : ExampleNotecard(shell, false)!;
            var floaters = shell.Shell.Floaters;
            string id = floaters.OpenFloaters[0].Id;
            await Frames(8);
            Node? up = panel;
            while (up is not null and not FloaterFrame)
                up = up.GetParent();
            if (up is not FloaterFrame frame || panel.FindChild(appearance ? "unsaved_prompt" : "unsaved", true, false) is not Control question)
            {
                problems.Add($"{kind} at {pct} %: the editor window or its question is not found");
                rig.Viewport.QueueFree();
                continue;
            }
            question.Visible = true;   // what the editors' close guard does when there are unsaved changes
            var spec = floaters.SpecOf(id)!;
            foreach (var (label, w, h) in new[] { ("minimum", spec.MinSize.W, spec.MinSize.H), ("default", spec.DefaultSize.W, spec.DefaultSize.H) })
            {
                floaters.Move(id, floaters.Bounds.X, floaters.Bounds.Y);
                floaters.Resize(id, w, h);
                await Frames(4);
                var box = frame.GetGlobalRect();
                // a save that fails while the question shows: its reason must be seen in the window, and the answers stay whole
                string? reasonWanted = null;
                if (appearance)
                {
                    ((AppearancePanel)panel).PromptSave.EmitSignal(BaseButton.SignalName.Pressed);
                    await Frames(4);   // the window shows the model's state on its next frames
                    reasonWanted = ((AppearancePanel)panel).StatusLabel.Text;
                }
                else if (panel is ScriptEditorPanel scriptPanel)
                {
                    scriptPanel.ShowAnswer(Viewer.Core.Inventory.ScriptSaveResult.Of(Viewer.Core.Inventory.ScriptSaveStatus.Failed, "failed"));
                    reasonWanted = scriptPanel.StatusLabel.Text;
                }
                else if (panel is NotecardPanel notecardPanel)
                {
                    notecardPanel.ShowAnswer(Viewer.Core.Inventory.NotecardSaveResult.Of(Viewer.Core.Inventory.NotecardSaveStatus.Failed, "failed"));
                    reasonWanted = notecardPanel.StatusLabel.Text;
                }
                await Frames(4);
                if (!question.Visible)
                    problems.Add($"{kind} at {pct} %, window {label}: the question went away after the failed save");
                if (string.IsNullOrEmpty(reasonWanted))
                    problems.Add($"{kind} at {pct} %, window {label}: the failed save left no reason text");
                else
                {
                    measured++;
                    var reasonLabel = panel.FindChild("unsaved_reason", true, false) as Label;
                    if (reasonLabel is null || !reasonLabel.IsVisibleInTree() || reasonLabel.Text != reasonWanted)
                        problems.Add($"{kind} at {pct} %, window {label}: the reason \"{reasonWanted}\" is not shown with the question (shown: \"{reasonLabel?.Text}\", visible {reasonLabel?.IsVisibleInTree()})");
                    else
                    {
                        var seenReason = reasonLabel.GetGlobalRect();
                        for (var a = reasonLabel.GetParent() as Control; a is not null && a != frame; a = a.GetParent() as Control)
                            if (a.ClipContents)
                                seenReason = seenReason.Intersection(a.GetGlobalRect());
                        seenReason = seenReason.Intersection(box);
                        if (seenReason.Size.X < reasonLabel.GetGlobalRect().Size.X - slack || seenReason.Size.Y < reasonLabel.GetGlobalRect().Size.Y - slack)
                            problems.Add($"{kind} at {pct} %, window {label}: the reason is cut off ({reasonLabel.GetGlobalRect()} seen as {seenReason})");
                    }
                }
                foreach (var name in buttonNames)
                {
                    if (panel.FindChild(name, true, false) is not Control button || !button.IsVisibleInTree())
                    {
                        problems.Add($"{kind} at {pct} %, window {label}: the button {name} is not shown");
                        continue;
                    }
                    var whole = button.GetGlobalRect();
                    var seen = whole;
                    for (var a = button.GetParent() as Control; a is not null && a != frame; a = a.GetParent() as Control)
                        if (a.ClipContents)
                            seen = seen.Intersection(a.GetGlobalRect());
                    seen = seen.Intersection(box);
                    measured++;
                    string line = $"{kind} at {pct} %, window {label} ({box.Size.X:F0} x {box.Size.Y:F0} px): {name} height {whole.Size.Y:F0} is seen as {seen.Size.Y:F0}; {name} {whole} is seen as {seen}, window {box}";
                    table.Add(line);
                    if (seen.Size.X < whole.Size.X - slack || seen.Size.Y < whole.Size.Y - slack)
                        problems.Add(line);
                }
            }
            rig.Viewport.QueueFree();
            await Frames(2);
        }
        _result["unsaved_prompt_fit_" + kind] = new { measured, problems = problems.Count, buttons = table };
        foreach (var line in problems.Take(6))
            _failures.Add("save-changes question: " + line);
        if (problems.Count > 6)
            _failures.Add($"save-changes question: {problems.Count - 6} more (unsaved_prompt_fit_{kind} in the result)");
        if (measured == 0)
            _failures.Add("save-changes question: nothing measured");
    }

    /// <summary>
    /// The command list in the world frame: "commandlist", the field empty with three recent picks; "commandlist-search", a search for
    /// "about land"; "commandlist-setting", a search for "draw distance" (a setting result). The About Land command is wired as the viewer
    /// wires it in the world.
    /// </summary>
    private static void ExampleCommandList(ShellRoot shell, SettingsService settings, PreferencesPanel prefs, string scene)
    {
        var s = shell.Shell;
        s.Commands.Register("floater.about_land", () => s.Floaters.Open(InteractFeature.LandId));
        settings.SetText(CommandRecents.Key, string.Join('\n', "menu:world.about_land", "pref:draw_distance", "menu:content.inventory"));
        var window = CommandListFeature.Install(shell, settings, prefs);
        window.Open();
        if (scene == "commandlist-search")
            window.Type("about land");
        else if (scene == "commandlist-setting")
            window.Type("draw distance");
    }

    /// <summary>A grid's branding made of reserved example values.</summary>
    private static GridBranding ExampleBranding() => GridBranding.None with
    {
        Name = "Example Grid", Tagline = "A grid for examples",
        News = [new NewsItem("Opening night", "Come to the plaza at noon."), new NewsItem("Maintenance", "A short break on Sunday.")],
        Register = new Uri("https://grid.example.org/register"), ForgotPassword = new Uri("https://grid.example.org/forgot"),
    };

    /// <summary>A script dialog or a permission request, built as the toast view builds it.</summary>
    private static void ScriptPanel(ShellRoot shell, StringTable strings, bool permission)
    {
        var model = new Viewer.Shell.Notifications.NotificationModel(() => 0);
        var prompts = new ScriptPrompts(model, i => new ActionResult(ActionStatus.Sent, 1), () => 0, () => "Test User");
        var task = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000a1");
        if (permission)
            prompts.Receive(new ScriptPermissionRequested(null, task, Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000a2"), "Example Vendor", "Example Owner", 4 | 16));
        else
            prompts.Receive(new ScriptDialogReceived(null, task, Guid.Empty, "Example Vendor", "Example Owner", false,
                new MessageBody("Choose one of the options below.", MessageChannel.LocalChat), 7, ["Yes", "No", "Maybe"]));
        var panel = NotificationView.Build(model.List[0], strings, UiTheme.Spec, (_, _, _) => { }, id => { });
        panel.Position = new Vector2(240, 160);
        shell.MainView.AddChild(panel);
        ReverieRestyle.Apply(panel);   // as the toast stack restyles every notice it shows
    }

    /// <summary>Every control shown, one line each: its path, engine class, text, disabled and theme variation (the before and after
    /// lists of a restyle: the same lines but for the variation column).</summary>
    private static IEnumerable<string> ControlList(Node root)
    {
        yield return "path\tclass\ttext\tdisabled\tvariation";
        var stack = new Stack<Node>();
        stack.Push(root);
        var lines = new List<string>();
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            foreach (var child in node.GetChildren())
                stack.Push(child);
            if (node is not Control { } c || !c.IsVisibleInTree())
                continue;
            string text = c switch { Button b => b.Text, Label l => l.Text, LineEdit e => e.Text, _ => "" };
            bool disabled = c is BaseButton { Disabled: true } || c is LineEdit { Editable: false };
            lines.Add(string.Join('\t', root.GetPathTo(c).ToString(), c.GetClass(), text.Replace('\t', ' ').Replace('\n', ' '), disabled ? "disabled" : "", c.ThemeTypeVariation.ToString()));
        }
        lines.Sort(StringComparer.Ordinal);
        foreach (var line in lines)
            yield return line;
    }

    /// <summary>A two-colour vertical blend: sky over ground.</summary>
    private static Texture2D Backdrop()
    {
        var gradient = new Gradient();
        gradient.SetColor(0, ShellTheme.C(UiTheme.Tokens.Bar).Lerp(ShellTheme.C(UiTheme.Tokens.Accent), 0.35f));
        gradient.SetColor(1, ShellTheme.C(UiTheme.Tokens.Input));
        return new GradientTexture2D { Gradient = gradient, Width = 4, Height = 64, FillFrom = new Vector2(0, 0), FillTo = new Vector2(0, 1) };
    }
}
