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
        "notecard", "notecard-readonly", "gesture", "gestures", "newmenu",
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
    private static void ExampleScript(ShellRoot shell, string scene)
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
            return;
        panel.ShowRunning(true, true);
        if (scene == "script-error")
        {
            panel.ShowAnswer(new Viewer.Core.Inventory.ScriptSaveResult(Viewer.Core.Inventory.ScriptSaveStatus.Saved, false, G(22),
                [Viewer.Core.Inventory.ScriptMessage.Parse("(13, 8) : ERROR : Syntax error")]));
            panel.ErrorClicked(0);
        }
        else
            panel.ShowAnswer(new Viewer.Core.Inventory.ScriptSaveResult(Viewer.Core.Inventory.ScriptSaveStatus.Saved, true, G(22), []));
    }

    /// <summary>
    /// The notecard editor with an example card holding an embedded landmark: "notecard", the user's own card, editable, with the caret at
    /// the end; "notecard-readonly", someone else's card that may be copied but not modified (Save hidden, "Read only" shown). No session or grid.
    /// </summary>
    private static void ExampleNotecard(ShellRoot shell, bool readOnly)
    {
        static Guid G(int n) => new($"00000000-0000-4000-8000-{n:D12}");
        const uint full = 0x7fffffff;
        var feature = new NotecardFeature(shell);
        var owner = readOnly ? G(3) : G(1);
        var perms = new Viewer.Core.Inventory.NotecardPermissions(owner, owner, full, readOnly ? full & ~Viewer.Core.Inventory.NotecardPermissions.Modify : full, 0,
            readOnly ? Viewer.Core.Inventory.NotecardPermissions.Copy : 0, full);
        var access = new Viewer.Core.Inventory.NotecardAccess(G(40), G(41), G(1), perms, InLibrary: false);
        var landmark = new Viewer.Core.Inventory.NotecardEmbeddedItem(0, G(42), G(43), "Harbour", "Example Region (128, 64, 22)", 3, 3, Viewer.Core.Inventory.ItemKind.Landmark,
            new Viewer.Core.Inventory.NotecardPermissions(G(1), G(1), full, full, 0, 0, full));
        string text = "Welcome to the harbour!\n\nMeet us at the pier: " + char.ConvertFromUtf32(Viewer.Core.Inventory.NotecardFormat.FirstEmbeddedChar)
            + "\n\nBring a friend, and see https://example.org/events for the times.";
        var doc = new Viewer.Core.Inventory.NotecardDocument(2, text, [landmark]);
        var opened = new Viewer.Shell.Inventory.NotecardOpenResult(Viewer.Shell.Inventory.NotecardOpenStatus.Ok, G(40), readOnly ? "Club rules" : "Harbour welcome",
            "Where to find us", access, doc);
        var panel = feature.ShowOpened(opened);
        if (panel is not null && !readOnly)
        {
            panel.Editor.SetCaretLine(panel.Editor.GetLineCount() - 1);
            panel.Editor.SetCaretColumn(panel.Editor.GetLine(panel.Editor.GetLineCount() - 1).Length);
        }
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
    private static Viewer.Shell.Build.EditController ExampleBuild(ShellRoot shell, StringTable strings, string scene = "build")
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
        // the owner and creator are named as the session's name cache would name them (a neutral example name)
        var panel = new EditPanel(strings, () => edit, null, new SettingsService(null), () => G(32), id => id == G(32) ? "Test User" : null);
        shell.AddFloater(new FloaterSpec(EditPanel.FloaterId, "edit.title", new UiSize(460, 680), new UiSize(400, 520)), panel);
        foreach (var section in Viewer.Shell.Build.BuildLayout.Sections)
            panel.SetOpen(section, scene == "build-look" && section == Viewer.Shell.Build.BuildSection.Look);
        if (scene == "build-search")
            panel.SearchText = "hollow";
        panel.Refresh();
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
        InventoryPanel? newMenuOf = null;
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
                ExampleNotecard(shell, scene == "notecard-readonly");
                break;
            case "gesture":
                ExampleGesture(shell, strings);
                break;
            case "gestures":
                ExampleGestureList(shell);
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
                NotificationFeature.Install(shell);
                InteractFeature.Install(shell);
                PersonalLightingFeature.Install(shell, new Viewer.Core.World.PersonalLighting(), () => null);
                if (scene.StartsWith("build", StringComparison.Ordinal) || scene == "colourpicker")
                {
                    ExampleBuild(shell, strings, scene);
                    if (scene == "colourpicker")
                        ColourPickerWindow.Install(shell).Open(new ColourRequest("edit.picker.colour.title", new UiColour(0.8f, 0.3f, 0.2f), false, _ => { }));
                }
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
                    "build" or "build-look" or "build-search" => EditPanel.FloaterId,
                    "colourpicker" => "",
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
        if (loginShown is not null && loginShown.ById["password"] is LineEdit field && loginShown.ById["reveal"] is Control eye)
        {
            // the field's text area ends where its face's right margin begins; the show button must start at or after it
            float textEnd = field.GetGlobalRect().End.X - field.GetThemeStylebox("normal").ContentMarginRight;
            float eyeStart = eye.GetGlobalRect().Position.X;
            _result["password_room"] = new { textEnd, eyeStart };
            if (textEnd > eyeStart + 0.5f)
                _failures.Add($"the password field's text runs under its show button: text to {textEnd:F1}, button from {eyeStart:F1}");
        }
        var img = rig.Texture.GetImage();
        img.Convert(Image.Format.Rgba8);
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
        UiTheme.Apply(ThemePresets.Get(ThemePresets.Classic), 100);
        return _failures.Count == 0 ? 0 : 1;
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
