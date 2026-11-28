// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Interaction;
using Viewer.Shell;

namespace Viewer.Godot;

public partial class S1Smoke
{
    // The right-click menu where it lies over a window: a press and release at a menu entry, through the engine's pointer input, chooses
    // that entry and does not reach the window underneath. A press is picked by tree order, not by what is drawn on top, so this needs the
    // menu to stand over the window layer in the tree.
    private async Task MenuOverWindowCheck()
    {
        var problems = new List<string>();
        var shell = new ShellRoot(new ShellController(StringTable.English(), () => { }));
        var panels = InteractFeature.Install(shell);
        var under = new Button { Name = "menu_under_window", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        shell.AddFloater(new FloaterSpec("menu_over_check", "inspect.title", new UiSize(480, 360), new UiSize(320, 240)), under);
        AddChild(shell);
        async Task Frames(int n) { for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        shell.Shell.Floaters.Open("menu_over_check");
        await Frames(3);
        int underPressed = 0;
        under.Pressed += () => underPressed++;
        var picked = new List<string>();
        var window = under.GetGlobalRect();
        panels.Menu.Open(window.Position + new Vector2(60, 60), [new ContextEntry("touch", "menu.help.about")], id => picked.Add(id), GetViewport().GetVisibleRect().Size);
        await Frames(2);
        bool over = false;
        if (panels.Menu.FindChild("entry_touch", true, false) is not Button entry)
            problems.Add("the menu has no button for its entry");
        else
        {
            var at = entry.GetGlobalRect().GetCenter();
            over = under.IsVisibleInTree() && window.HasPoint(at);
            if (!over) problems.Add($"not exercised: the menu entry at {at} does not lie over the window {window}");
            Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at });
            await Frames(1);
            Input.ParseInputEvent(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left });
            await Frames(1);
            Input.ParseInputEvent(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = false });
            await Frames(2);
            if (picked.Count != 1 || picked.FirstOrDefault() != "touch") problems.Add($"a click on the menu entry over a window chose [{string.Join(",", picked)}]");
            if (underPressed != 0) problems.Add($"a click on the menu entry pressed the window under it {underPressed} times");
            if (panels.Menu.IsOpen) problems.Add("a click on the menu entry over a window left the menu open");
        }
        _result["menu_over_window"] = new Dictionary<string, object?> { ["over_window"] = over, ["chosen"] = picked.Count, ["window_pressed"] = underPressed, ["problems"] = problems };
        Log($"menu over window: over {over}, chosen {picked.Count}, window pressed {underPressed}, {problems.Count} problems{(problems.Count > 0 ? ": " + string.Join(" | ", problems) : "")}");
        RemoveChild(shell);
        shell.QueueFree();
        if (problems.Count > 0)
            _failures.Add("a right-click menu entry over a window was not chosen by a click on it (see menu_over_window)");
    }
}
