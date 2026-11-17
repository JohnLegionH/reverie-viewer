// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

public partial class S1Smoke
{
    // The command list through the engine's own input: Ctrl+Shift+K opens it (Ctrl+K does not), a click on the top bar's search box
    // opens it, typed letters search, Up and Down move, Enter opens the result, Ctrl+Enter opens the menu it lives in with the entry
    // highlighted, Esc closes; a picked result shows first next time; a setting opens Preferences on its page with the keyboard on its
    // row; Statistics says where the official viewer keeps it; and every window and setting result opens what it names.
    private async Task CommandListCheck()
    {
        var problems = new List<string>();
        var shell = new ShellRoot(new ShellController(StringTable.English(), () => { }));
        var s = shell.Shell;
        var settings = new SettingsService(null);
        AddChild(shell);
        s.Menus.AddMenu("communicate", "menu.communicate");   // the chat windows' menu, which Notifications joins
        TravelFeature.Install(shell, settings);
        NotificationFeature.Install(shell);
        InteractFeature.Install(shell);
        s.Commands.Register("floater.about_land", () => s.Floaters.Open(InteractFeature.LandId));   // as the viewer wires it in the world
        var prefs = PreferencesFeature.Install(shell, settings, () => 0, () => false);
        var ran = new List<string>();
        var stubbed = new HashSet<string>();
        BuildMenu.Install(s.Menus, () => true);
        Viewer.Shell.EnvironmentMenu.Install(s.Menus, s.Commands, new Viewer.Shell.EnvironmentChoice(null), () => ran.Add("env.personal"));
        foreach (var e in Flat(s.Menus.Menus.SelectMany(m => m.Entries)))
            if (e.CommandId.Length > 0 && !s.Commands.Has(e.CommandId))
            {
                string c = e.CommandId;
                stubbed.Add(c);
                s.Commands.Register(c, () => ran.Add(c));
            }
        var window = CommandListFeature.Install(shell, settings, prefs);
        static IEnumerable<MenuEntry> Flat(IEnumerable<MenuEntry> es) => es.SelectMany(e => e.Children is { } k ? Flat(k).Prepend(e) : [e]);
        async Task Frames(int n) { for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        async Task Key(Key key, bool ctrl = false, bool shift = false, long unicode = 0)
        {
            Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Unicode = (char)unicode, Pressed = true, CtrlPressed = ctrl, ShiftPressed = shift });
            Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false, CtrlPressed = ctrl, ShiftPressed = shift });
            await Frames(1);
        }
        async Task Type(string text)
        {
            foreach (char c in text)
                await Key(c == ' ' ? global::Godot.Key.Space : (Key)char.ToUpperInvariant(c), unicode: c);
            await Frames(1);
        }
        async Task ClearField()
        {
            window.Field.SelectAll();
            await Key(global::Godot.Key.Backspace);
        }
        async Task Click(Control c)
        {
            var at = c.GetGlobalRect().GetCenter();
            Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at });
            await Frames(1);
            Input.ParseInputEvent(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left });
            await Frames(1);
            Input.ParseInputEvent(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = false });
            await Frames(2);
        }
        void CloseAll()
        {
            foreach (var f in s.Floaters.OpenFloaters.ToArray())
                s.Floaters.Close(f.Id);
            shell.TopBar.Menus.CloseMenus();
        }
        await Frames(3);
        GetViewport().GuiReleaseFocus();

        // the key: Ctrl+K stays Camera controls (nothing here), Ctrl+Shift+K opens the list with the keyboard in its field
        await Key(global::Godot.Key.K, ctrl: true);
        if (window.IsOpen) problems.Add("Ctrl+K opened the command list");
        await Key(global::Godot.Key.K, ctrl: true, shift: true);
        if (!window.IsOpen) problems.Add("Ctrl+Shift+K did not open the command list");
        if (!window.Field.HasFocus()) problems.Add("the field does not have the keyboard when the list opens");
        if (window.Rows.Count != 0) problems.Add($"{window.Rows.Count} recent picks before any pick");

        // about land, Enter: the About Land window opens and the list closes
        await Type("about land");
        string best = window.State.Groups.Best?.Target ?? "none";
        if (best != "menu:world.about_land") problems.Add("'about land' best match is " + best);
        if (window.Rows.Count == 0 || (window.Rows[0].FindChild("elsewhere", true, false) as Label)?.Text != "SL: World > About land")
            problems.Add("the About Land row does not say where SL keeps it: " + (window.Rows.FirstOrDefault()?.FindChild("elsewhere", true, false) as Label)?.Text);
        await Key(global::Godot.Key.Enter);
        if (!s.Floaters.IsOpen(InteractFeature.LandId)) problems.Add("Enter on About Land did not open the About Land window");
        if (window.IsOpen) problems.Add("the list stayed open after Enter");
        CloseAll();

        // opened again: the pick shows first
        await Key(global::Godot.Key.K, ctrl: true, shift: true);
        if (window.State.Shown.FirstOrDefault()?.Target != "menu:world.about_land") problems.Add("the recent pick does not show first");

        // statistics: not built, and the line says where SL keeps it
        await Type("statistics");
        if (!window.NotBuiltLine.Visible || !window.NotBuiltLine.Text.Contains("Advanced > Performance Tools > Statistics Bar", StringComparison.Ordinal))
            problems.Add("statistics: " + (window.NotBuiltLine.Visible ? window.NotBuiltLine.Text : "no line"));

        // Up and Down move the highlight, wrapping
        await ClearField();
        await Type("build");
        int n = window.Rows.Count;
        await Key(global::Godot.Key.Down);
        await Key(global::Godot.Key.Down);
        if (window.State.Selected != 2 || window.Rows[2].ThemeTypeVariation != ReverieStyle.ListRowSelected) problems.Add($"Down twice selects {window.State.Selected}");
        await Key(global::Godot.Key.Up);
        await Key(global::Godot.Key.Up);
        await Key(global::Godot.Key.Up);
        if (window.State.Selected != n - 1) problems.Add($"Up past the top selects {window.State.Selected} of {n}");

        // a setting: Preferences on its page, the keyboard on its row
        await ClearField();
        await Type("draw distance");
        await Key(global::Godot.Key.Enter);
        await Frames(6);
        if (!s.Floaters.IsOpen(PreferencesFeature.FloaterId)) problems.Add("Enter on draw distance did not open Preferences");
        if (prefs.CurrentCategory != "graphics") problems.Add("Preferences opened on " + prefs.CurrentCategory);
        var focus = GetViewport().GuiGetFocusOwner();
        var row = prefs.ControlFor(Settings.DrawDistance.Key);
        if (row is null || focus is null || (focus != row && !row.IsAncestorOf(focus))) problems.Add("the draw distance row does not have the keyboard: " + focus?.Name);
        CloseAll();

        // Ctrl+Enter: the menu the result lives in opens with the entry highlighted
        await Key(global::Godot.Key.K, ctrl: true, shift: true);
        await Type("world map");
        await Key(global::Godot.Key.Enter, ctrl: true);
        await Frames(2);
        if (shell.OpenMenu != "world") problems.Add("Ctrl+Enter on World map opened menu " + (shell.OpenMenu ?? "none"));
        else
        {
            int at = shell.TopBar.Menus.Menus.Select((m, i) => (m, i)).First(x => x.m.Id == "world").i;
            var popup = shell.TopBar.Menus.Popups[at];
            int item = popup.GetFocusedItem();
            if (item < 0 || popup.GetItemText(item) != s.Strings.Get("menu.world.map")) problems.Add("the World menu highlights " + (item < 0 ? "nothing" : popup.GetItemText(item)));
        }
        if (s.Floaters.IsOpen("world_map")) problems.Add("Ctrl+Enter opened the map instead of showing its menu");
        CloseAll();

        // Ctrl+Enter on an entry inside a submenu: World > Environment opens, with Sunrise highlighted in it
        await Key(global::Godot.Key.K, ctrl: true, shift: true);
        await Type("sunrise");
        await Key(global::Godot.Key.Enter, ctrl: true);
        await Frames(2);
        {
            int at = shell.TopBar.Menus.Menus.Select((m, i) => (m, i)).First(x => x.m.Id == "world").i;
            var env = shell.TopBar.Menus.Popups[at].GetNodeOrNull<PopupMenu>("world_environment");
            int item = env?.GetFocusedItem() ?? -1;
            if (env is null || !env.Visible || item < 0 || env.GetItemText(item) != s.Strings.Get("menu.env.sunrise"))
                problems.Add("Ctrl+Enter on Sunrise: the Environment submenu " + (env is null || !env.Visible ? "is not open" : "highlights " + (item < 0 ? "nothing" : env.GetItemText(item))));
        }
        CloseAll();

        // Esc closes; the top bar's search box opens it
        await Key(global::Godot.Key.K, ctrl: true, shift: true);
        await Key(global::Godot.Key.Escape);
        if (window.IsOpen) problems.Add("Esc did not close the list");
        shell.TopBar.Search.Visible = true;
        await Frames(2);
        await Click(shell.TopBar.Search);
        if (!window.IsOpen) problems.Add("a click on the top bar's search box did not open the list");
        await Click(shell.TopBar.Search);
        if (window.IsOpen) problems.Add("a second click on the search box did not close the list");

        // every result opens what it names: a window opens, a setting or page shows in Preferences, a command is one the viewer runs
        int windows = 0, settingsShown = 0, commands = 0;
        var index = window.State.Index;
        foreach (var item in index.Items)
        {
            CloseAll();
            ran.Clear();
            var opened = new List<string>();
            void OnOpen(string id) => opened.Add(id);
            s.Floaters.Opened += OnOpen;
            window.OpenItem!(item);
            await Frames(1);
            s.Floaters.Opened -= OnOpen;
            switch (item.Kind)
            {
                case CommandKind.Window:
                    windows++;
                    if (opened.Count == 0) problems.Add($"{item.Target} opened no window");
                    else
                    {
                        // picked again while it is open, it stays open
                        window.OpenItem!(item);
                        await Frames(1);
                        if (!opened.All(s.Floaters.IsOpen)) problems.Add($"{item.Target} closed its window when picked while open");
                    }
                    break;
                case CommandKind.Setting:
                    settingsShown++;
                    string page = item.PrefPageId ?? PreferenceCatalog.Find(item.PrefKey!)!.Page.ToString().ToLowerInvariant();
                    if (!s.Floaters.IsOpen(PreferencesFeature.FloaterId) || prefs.CurrentCategory != page) problems.Add($"{item.Target} showed {prefs.CurrentCategory}");
                    if (item.PrefKey is { } key && prefs.ControlFor(key) is null) problems.Add($"{item.Target} has no row on its page");
                    break;
                default:
                    commands++;
                    if (!s.Commands.Has(item.CommandId!)) problems.Add($"{item.Target} runs no command");
                    else if (stubbed.Contains(item.CommandId!) && (!item.Enabled ? ran.Count != 0 : ran.SingleOrDefault() != item.CommandId))
                        problems.Add($"{item.Target} ran [{string.Join(",", ran)}]");
                    break;
            }
        }
        CloseAll();
        _result["command_list"] = new Dictionary<string, object?> { ["items"] = index.Items.Count, ["windows"] = windows, ["settings"] = settingsShown, ["commands"] = commands, ["problems"] = problems };
        Log($"command list: {index.Items.Count} results ({windows} windows, {commands} commands, {settingsShown} settings), {problems.Count} problems{(problems.Count > 0 ? ": " + string.Join(" | ", problems.Take(12)) : "")}");
        RemoveChild(shell);
        shell.QueueFree();
        if (problems.Count > 0 || windows == 0 || settingsShown == 0)
            _failures.Add("the command list through the engine's key and pointer input (see command_list)");
    }
}
