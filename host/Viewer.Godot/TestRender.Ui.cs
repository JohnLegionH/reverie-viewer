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

    /// <summary>The picture size of the interface scenes.</summary>
    private const int UiWidth = 960, UiHeight = 600;

    /// <summary>The scene names --tr-ui accepts: the in-world frame and each window on its own.</summary>
    public static readonly string[] UiScenes =
    [
        "inworld", "inventory", "outfits", "people", "conversations", "map", "minimap", "teleport", "notifications", "scriptdialog",
        "permission", "inspect", "land", "preferences", "preferences-general", "preferences-graphics", "preferences-chat", "preferences-moveview",
        "preferences-privacy", "preferences-cache", "reconnect", "login", "appearance", "appearance-shape", "appearance-shirt", "personal", "build",
        "build-look", "build-search", "colourpicker", "script", "script-error", "script-readonly", "commandlist", "commandlist-search", "commandlist-setting",
        "notecard", "notecard-readonly", "gesture", "gestures", "newmenu", "scriptqueue",
        "texturepicker", "build-contents", "takefolder", "gridoptions", "objectcontents", "buy", "objectinfo",
    ];

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

    /// <summary>
    /// The script editor with an example script: "script", a script inside an object with Save, Running (ticked, as the region answered),
    /// Reset and Mono, after a save that compiled; "script-error", the same script after a save whose compiler named a line (the error listed
    /// under the code and the caret put on its place by a click on it); "script-readonly", a library script opened read only. No session or grid.
    /// </summary>
    private static ScriptEditorPanel ExampleScript(ShellRoot shell, string scene)
    {
        static Guid G(int n) => new($"00000000-0000-4000-8000-{n:D12}");
        var feature = new ScriptEditorFeature(shell);
        feature.Bind(null, openSim: true);
        const uint full = 0x7fffffff;
        bool readOnly = scene == "script-readonly";
        var owner = readOnly ? G(3) : G(1);
        var access = new Viewer.Core.Inventory.ScriptAccess(G(20), G(21), G(1),
            new Viewer.Core.Inventory.NotecardPermissions(owner, owner, full, full, 0, readOnly ? Viewer.Core.Inventory.NotecardPermissions.Copy : 0, full),
            InLibrary: readOnly, ObjectId: readOnly ? Guid.Empty : G(30), LocalId: readOnly ? 0u : 7u);
        var panel = feature.Open(access, readOnly ? "Example library script" : "Example script");
        string text = scene == "script-error" ? ExampleScriptText : ExampleScriptText.Replace("count += total\n", "count += total;\n", StringComparison.Ordinal);
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

    /// <summary>The Gestures window with an example inventory: five gestures in the Gestures folder, two of them active (one with its
    /// trigger and Shift+F2, one whose file has a trigger only), the second row chosen. No session or grid.</summary>
    private static void ExampleGestureList(ShellRoot shell)
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
            Viewer.Core.Building.EditNumbers.FromEulerDegrees(new System.Numerics.Vector3(0, 0, 30)), new System.Numerics.Vector3(10f, 10f, 0.2f), Interaction: new Viewer.Core.World.PrimInteraction(0, own), Shape: shape));
        var edit = new Viewer.Shell.Build.EditController(world, _ => new ActionResult(ActionStatus.Sent));
        edit.SelectTool(Viewer.Shell.Build.BuildTool.Edit);
        edit.SelectMany([1], shift: false);
        edit.Receive(new Viewer.Core.Session.ObjectPropertiesReceived(G(31), "Floor", "Plank floor", G(32), G(32), Guid.Empty, null,
            0x7fffffff, 0, 1u << 19 | 1u << 15 | 1u << 13, 0, false));
        if (scene == "build-contents")
            edit.Receive(new TaskInventoryReceived(G(31), ExampleTaskItems()));
        // the owner and creator are named as the session's name cache would name them (a neutral example name)
        var panel = new EditPanel(strings, () => edit, null, new SettingsService(null), () => G(32), id => id == G(32) ? "Test User" : null);
        shell.AddFloater(new FloaterSpec(EditPanel.FloaterId, "edit.title", new UiSize(460, 680), new UiSize(400, 520)), panel);
        foreach (var section in Viewer.Shell.Build.BuildLayout.Sections)
            panel.SetOpen(section, scene == "build-look" && section == Viewer.Shell.Build.BuildSection.Look
                || scene == "build-contents" && section == Viewer.Shell.Build.BuildSection.Contents);
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
        var strings = StringTable.English();
        // NV_UI_NORESTYLE=1: the windows as the classic look built them, for the before list of controls (NV_UI_CONTROLS=1)
        ReverieRestyle.Enabled = System.Environment.GetEnvironmentVariable("NV_UI_NORESTYLE") != "1";
        var tokens = ThemePresets.Get(look);
        if (wrongToken)
            tokens = tokens with { Accent = new UiColour(1f, 0f, 1f), Text = new UiColour(0f, 1f, 0f) };
        UiTheme.Apply(tokens, 100);
        var rig = MakeRig(new Vector2I(UiWidth, UiHeight));
        var shell = new ShellRoot(new ShellController(strings, () => { })) { FixedClockText = TestClockText };
        rig.Viewport.AddChild(shell);
        var settings = new SettingsService(null);
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
            case "script":
            case "script-error":
            case "script-readonly":
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
            case "scriptqueue":
                ExampleScriptQueue(shell, strings);
                break;
            case "scriptdialog":
            case "permission":
                ScriptPanel(shell, strings, scene == "permission");
                break;
            default:
                var chat = ChatFeature.Install(shell, settings);
                PeopleFeature.Install(shell, settings, chat.Conversations);
                var inventoryPanel = InventoryFeature.Install(shell);
                if (scene is "inventory" or "newmenu")
                    ExampleInventoryDrag(inventoryPanel, dragCue: scene == "inventory");
                if (scene == "newmenu")
                {
                    newMenuOf = inventoryPanel;
                    rig.Viewport.GuiEmbedSubwindows = true;   // the menus are drawn inside the picture
                }
                OutfitFeature.Install(shell);
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
                open = scene switch
                {
                    "inworld" => "",
                    "inventory" or "newmenu" => InventoryFeature.FloaterId,
                    "outfits" => OutfitFeature.FloaterId,
                    "people" => PeopleFeature.PeopleId,
                    "conversations" => ChatFeature.ConversationsId,
                    "map" => TravelFeature.MapId,
                    "minimap" => TravelFeature.MiniMapId,
                    "teleport" => TravelFeature.TeleportId,
                    "notifications" => NotificationFeature.ListId,
                    "appearance" or "appearance-shape" or "appearance-shirt" => AppearanceFeature.FloaterId,
                    "inspect" => InteractFeature.InspectId,
                    "land" => InteractFeature.LandId,
                    "personal" => PersonalLightingFeature.FloaterId,
                    "build" or "build-look" or "build-search" or "build-contents" => EditPanel.FloaterId,
                    "colourpicker" or "texturepicker" or "gridoptions" or "takefolder" or "objectcontents" or "buy" or "objectinfo" => "",
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
            contentsPanel.ContentTab.OpenMenuOnRow(1);   // the right-click menu on the second row (the notecard)
            await Frames(10);
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
        var img = rig.Texture.GetImage();
        img.Convert(Image.Format.Rgba8);
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
                var diff = g.GetWidth() != UiWidth || g.GetHeight() != UiHeight
                    ? new DiffResult(false, g.GetWidth(), g.GetHeight(), 0, 0, 0, 0, "golden size differs")
                    : ImageDiff.Compare(img.GetData(), g.GetData(), UiWidth, UiHeight, 4, DiffTolerance.Ui);
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
            await PreferencesFit(strings, tokens);
        if (scene is "script" or "notecard" && !wrongToken)
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
    /// The "Save changes?" question of the script and notecard editors, checked after the picture is taken: at the interface sizes 100, 150 and
    /// 200 %, with the editor's window at its minimum size and at its default size, the question is shown and its three buttons (Save, Don't
    /// Save, Cancel) are each wholly seen: inside the window, and not cut by any scroll area or clipping control between them and it.
    /// </summary>
    private async Task UnsavedPromptFit(string scene, StringTable strings, ThemeTokens tokens)
    {
        const float slack = 0.5f;
        var problems = new List<string>();
        var table = new List<string>();
        int measured = 0;
        foreach (int pct in new[] { 100, 150, 200 })
        {
            float s = pct / 100f;
            UiTheme.Apply(tokens, pct);
            var rig = MakeRig(new Vector2I(UiWidth, UiHeight));
            rig.Viewport.Size2DOverride = new Vector2I((int)MathF.Round(UiWidth / s), (int)MathF.Round(UiHeight / s));
            rig.Viewport.Size2DOverrideStretch = true;
            var shell = new ShellRoot(new ShellController(strings, () => { })) { ScaleSink = _ => { }, FixedClockText = TestClockText };
            rig.Viewport.AddChild(shell);
            Control panel = scene == "script" ? ExampleScript(shell, "script") : ExampleNotecard(shell, false)!;
            var floaters = shell.Shell.Floaters;
            string id = floaters.OpenFloaters[0].Id;
            await Frames(8);
            Node? up = panel;
            while (up is not null and not FloaterFrame)
                up = up.GetParent();
            if (up is not FloaterFrame frame || panel.FindChild("unsaved", true, false) is not Control question)
            {
                problems.Add($"{scene} at {pct} %: the editor window or its question is not found");
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
                foreach (var name in new[] { "prompt_save", "prompt_dont_save", "prompt_cancel" })
                {
                    if (panel.FindChild(name, true, false) is not Control button || !button.IsVisibleInTree())
                    {
                        problems.Add($"{scene} at {pct} %, window {label}: the button {name} is not shown");
                        continue;
                    }
                    var whole = button.GetGlobalRect();
                    var seen = whole;
                    for (var a = button.GetParent() as Control; a is not null && a != frame; a = a.GetParent() as Control)
                        if (a.ClipContents)
                            seen = seen.Intersection(a.GetGlobalRect());
                    seen = seen.Intersection(box);
                    measured++;
                    string line = $"{scene} at {pct} %, window {label} ({box.Size.X:F0} x {box.Size.Y:F0} px): {name} {whole} is seen as {seen}, window {box}";
                    table.Add(line);
                    if (seen.Size.X < whole.Size.X - slack || seen.Size.Y < whole.Size.Y - slack)
                        problems.Add(line);
                }
            }
            rig.Viewport.QueueFree();
            await Frames(2);
        }
        _result["unsaved_prompt_fit_" + scene] = new { measured, problems = problems.Count, buttons = table };
        foreach (var line in problems.Take(6))
            _failures.Add("save-changes question: " + line);
        if (problems.Count > 6)
            _failures.Add($"save-changes question: {problems.Count - 6} more (unsaved_prompt_fit_{scene} in the result)");
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
