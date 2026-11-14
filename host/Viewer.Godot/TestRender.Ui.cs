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
    private static void ExampleInventoryDrag(InventoryPanel panel)
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
        panel.TreeView.HoldCue(new Viewer.Shell.Inventory.DropCue(note, G(13)));
    }

    /// <summary>The picture size of the interface scenes.</summary>
    private const int UiWidth = 960, UiHeight = 600;

    /// <summary>The scene names --tr-ui accepts: the in-world frame and each window on its own.</summary>
    public static readonly string[] UiScenes =
    [
        "inworld", "inventory", "outfits", "people", "conversations", "map", "minimap", "teleport", "notifications", "scriptdialog",
        "permission", "inspect", "land", "preferences", "preferences-general", "preferences-graphics", "preferences-chat", "preferences-moveview",
        "preferences-privacy", "preferences-cache", "reconnect", "login", "appearance", "appearance-shape", "appearance-shirt", "personal",
    ];

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
        var tokens = ThemePresets.Get(look);
        if (wrongToken)
            tokens = tokens with { Accent = new UiColour(1f, 0f, 1f), Text = new UiColour(0f, 1f, 0f) };
        UiTheme.Apply(tokens, 100);
        var rig = MakeRig(new Vector2I(UiWidth, UiHeight));
        var shell = new ShellRoot(new ShellController(strings, () => { })) { FixedClockText = TestClockText };
        rig.Viewport.AddChild(shell);
        var settings = new SettingsService(null);
        settings.Set(Settings.DrawDistance, 1024);   // the pictures keep a distance of 1,024 (no level has it: Custom), whatever the default level is

        // a stand-in for the 3D view: a sky-to-ground blend, so the bars and windows are seen over a scene
        var backdrop = new TextureRect { Name = "backdrop", Texture = Backdrop(), StretchMode = TextureRect.StretchModeEnum.Scale, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, MouseFilter = Control.MouseFilterEnum.Ignore };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        shell.MainView.AddChild(backdrop);

        string open = "";
        switch (scene)
        {
            case "login":
                var login = new LoginScreen(strings, null);
                shell.MainView.AddChild(login);
                ((LineEdit)login.ById["grid"]).Text = ExampleGridAddress;
                login.ApplyBranding(ExampleBranding());
                login.ShowConnecting();
                break;
            case "reconnect":
                var model = new LoginModel(strings, _ => throw new InvalidOperationException("no login in a render case"), null);
                var drop = new DropPanel(strings, new Viewer.Shell.Reconnect.ReconnectModel(model, strings, (_, _) => Task.CompletedTask));
                shell.MainView.AddChild(drop);
                drop.ShowNotice(new Viewer.Shell.Reconnect.DropNotice(strings.Get("drop.title"), strings.Get("drop.body"), strings.Get("drop.reason.unknown"), true));
                break;
            case "scriptdialog":
            case "permission":
                ScriptPanel(shell, strings, scene == "permission");
                break;
            default:
                var chat = ChatFeature.Install(shell, settings);
                PeopleFeature.Install(shell, settings, chat.Conversations);
                var inventoryPanel = InventoryFeature.Install(shell);
                if (scene == "inventory")
                    ExampleInventoryDrag(inventoryPanel);
                OutfitFeature.Install(shell);
                var appearancePanel = AppearanceFeature.Install(shell, redrawGapMs: 0);
                if (scene.StartsWith("appearance", StringComparison.Ordinal))
                    await ExampleAppearance(appearancePanel, scene);
                TravelFeature.Install(shell, settings);
                NotificationFeature.Install(shell);
                InteractFeature.Install(shell);
                PersonalLightingFeature.Install(shell, new Viewer.Core.World.PersonalLighting(), () => null);
                var prefs = PreferencesFeature.Install(shell, settings, () => 0, () => false);
                if (scene.StartsWith("preferences", StringComparison.Ordinal))
                    prefs.ShowCategory(scene == "preferences" ? "interface" : scene["preferences-".Length..]);   // "preferences" is the Interface page
                open = scene switch
                {
                    "inworld" => "",
                    "inventory" => InventoryFeature.FloaterId,
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
                    _ => PreferencesFeature.FloaterId,
                };
                break;
        }
        if (scene == "inworld")
        {
            shell.ShowWorldRows(true);
            shell.SetConnected(true);
            shell.SetBrand(ExampleBranding().Name);
            shell.Navigation.SetLocation("Example Region");
            shell.SetUnread(3);
            shell.Shell.Floaters.Open(ChatFeature.NearbyId);
            shell.Shell.Floaters.Open(PeopleFeature.PeopleId);
            shell.Shell.Floaters.Open(PreferencesFeature.FloaterId);
        }
        else if (open.Length > 0)
            shell.Shell.Floaters.Open(open);
        await Frames(10);
        var img = rig.Texture.GetImage();
        img.Convert(Image.Format.Rgba8);
        Directory.CreateDirectory(_out);
        img.SavePng(Path.Combine(_out, _tag + ".png"));
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
                    : ImageDiff.Compare(img.GetData(), g.GetData(), UiWidth, UiHeight, 4, DiffTolerance.Terrain);
                _result["diff"] = new Dictionary<string, object?>
                {
                    ["golden"] = Path.GetFileName(golden), ["pass"] = diff.Pass, ["differing"] = diff.Differing,
                    ["differing_fraction"] = diff.DifferingFraction, ["mean_abs_error"] = Math.Round(diff.MeanAbsError, 4),
                    ["max_channel_error"] = diff.MaxChannelError, ["tolerance"] = DiffTolerance.Terrain, ["summary"] = diff.ToString(),
                };
                if (!diff.Pass)
                    _failures.Add("diff " + diff);
            }
        }
        shell.QueueFree();
        UiTheme.Apply(ThemePresets.Get(ThemePresets.Classic), 100);
        return _failures.Count == 0 ? 0 : 1;
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
