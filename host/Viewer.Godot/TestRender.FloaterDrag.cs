// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

public partial class TestRender
{
    /// <summary>
    /// --tr-floaterdrag=PCT:WxH: the Inventory window in a window of W x H pixels at an interface size of PCT %, driven by real pointer events pushed
    /// into the viewport in window pixels (the way the operating system delivers them). For every edge, every corner and the grip it presses, drags
    /// outward step by step, then inward past where it began, then far inward, and releases. The run FAILS unless each step grows the window while the
    /// pointer goes out and shrinks it while the pointer comes in (never the other way, never back and forth), the window ends exactly the pointer's
    /// travel larger and smaller, the edges not dragged stay where they are, the minimum size holds, the window stays inside the main view, a drag of
    /// the title bar moves it by the pointer's travel, and the close button closes it on the first click anywhere on its drawn icon, straight after
    /// a resize. The official floater (LLFloater, LLResizeBar, LLResizeHandle, LLDragHandle) is the behaviour followed.
    /// </summary>
    private async Task<int> FloaterDragRun(string spec)
    {
        var parts = spec.Split(':', 'x');
        if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pct)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int w) || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int h))
            throw new ArgumentException("--tr-floaterdrag must be PCT:WxH, e.g. 150:3840x2066");
        float s = pct / 100f;
        var strings = StringTable.English();
        UiTheme.Apply(ThemePresets.Get(ThemePresets.Classic), pct);
        var rig = MakeRig(new Vector2I(w, h));
        rig.Viewport.Size2DOverride = new Vector2I((int)MathF.Round(w / s), (int)MathF.Round(h / s));
        rig.Viewport.Size2DOverrideStretch = true;
        var shell = new ShellRoot(new ShellController(strings, () => { })) { ScaleSink = _ => { }, FixedClockText = TestClockText };
        rig.Viewport.AddChild(shell);
        InventoryFeature.Install(shell);
        shell.ShowWorldRows(true);
        shell.SetConnected(true);
        await Frames(6);

        var floaters = shell.Shell.Floaters;
        const string id = InventoryFeature.FloaterId;
        var min = floaters.SpecOf(id)!.MinSize;
        var fails = new List<string>();
        var steps = new List<string>();
        Vector2 pointer = Vector2.Zero;   // in window pixels

        void Push(InputEvent e) => rig.Viewport.PushInput(e, false);
        void Button(Vector2 logical, bool pressed)
        {
            pointer = logical * s;
            Push(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = pressed, Position = pointer, GlobalPosition = pointer, ButtonMask = pressed ? MouseButtonMask.Left : 0 });
        }
        void MoveTo(Vector2 logical, bool held)
        {
            var next = logical * s;
            Push(new InputEventMouseMotion { Position = next, GlobalPosition = next, Relative = next - pointer, ButtonMask = held ? MouseButtonMask.Left : 0 });
            pointer = next;
        }
        UiRect R() => floaters.RectOf(id) ?? default;
        bool Near(float a, float b) => MathF.Abs(a - b) <= 1.01f;
        var main = shell.MainArea;
        bool InsideMain(UiRect r) => r.X >= main.Position.X - 0.5f && r.Y >= main.Position.Y - 0.5f && r.Right <= main.End.X + 0.5f && r.Bottom <= main.End.Y + 0.5f;

        async Task Reset()
        {
            if (!floaters.IsOpen(id)) floaters.Open(id);
            floaters.Resize(id, 600, 500);
            floaters.Move(id, main.Position.X + (main.Size.X - 600) / 2, main.Position.Y + (main.Size.Y - 500) / 2);
            await Frames(3);
        }

        var frame = shell.Frames[id];
        (string Name, Vector2 Out)[] handles =
        [
            ("resize_left", new(-1, 0)), ("resize_right", new(1, 0)), ("resize_top", new(0, -1)), ("resize_bottom", new(0, 1)),
            ("resize_top_left", new(-1, -1)), ("resize_top_right", new(1, -1)), ("resize_bottom_left", new(-1, 1)), ("resize_bottom_right", new(1, 1)),
            ("grip", new(1, 1)),
        ];
        const float step = 8;
        const int outSteps = 10;
        foreach (var (name, dir) in handles)
        {
            await Reset();
            var start = R();
            if (frame.FindChild(name, true, false) is not Control handle || !handle.IsVisibleInTree())
            {
                fails.Add($"{name}: not found");
                continue;
            }
            var hr = handle.GetGlobalRect();
            // an edge strip is pressed at its middle; a corner on its outer border, 2 units in from the window's corner (a corner other than the
            // bottom-right one takes the pointer only along that border)
            bool isCorner = dir.X != 0 && dir.Y != 0;
            var at = isCorner
                ? new Vector2(dir.X < 0 ? hr.Position.X + 2 : hr.End.X - 2, dir.Y < 0 ? hr.Position.Y + 2 : hr.End.Y - 2)
                : hr.GetCenter();
            Button(at, true);
            await Frames(1);
            var seen = new List<UiRect> { R() };
            int bad = 0;
            // outward, then inward past the start, one pointer step a frame
            for (int i = 1; i <= outSteps * 3; i++)
            {
                int k = i <= outSteps ? i : 2 * outSteps - i;   // 1..10 then 9..-10
                MoveTo(at + dir * step * k, true);
                await Frames(1);
                var r = R();
                var prev = seen[^1];
                float growW = (r.W - prev.W) * (i <= outSteps ? 1 : -1), growH = (r.H - prev.H) * (i <= outSteps ? 1 : -1);
                if ((dir.X != 0 && growW < -0.01f) || (dir.Y != 0 && growH < -0.01f))
                    bad++;
                seen.Add(r);
            }
            var afterOut = seen[outSteps];   // seen[0] is the press, seen[k] the k-th step
            var end = seen[^1];
            float wantW = dir.X != 0 ? start.W + step * outSteps : start.W, wantH = dir.Y != 0 ? start.H + step * outSteps : start.H;
            if (!Near(afterOut.W, wantW) || !Near(afterOut.H, wantH))
                fails.Add($"{name}: dragged out {step * outSteps} units, the window is {afterOut.W}x{afterOut.H}, wanted {wantW}x{wantH}");
            float endW = dir.X != 0 ? start.W - step * outSteps : start.W, endH = dir.Y != 0 ? start.H - step * outSteps : start.H;
            if (!Near(end.W, endW) || !Near(end.H, endH))
                fails.Add($"{name}: dragged in {step * outSteps} units past the start, the window is {end.W}x{end.H}, wanted {endW}x{endH}");
            if (bad > 0)
                fails.Add($"{name}: {bad} of {outSteps * 3} steps changed the size against the pointer (bounce)");
            // the edges not dragged stay put
            if (dir.X == 0 && (!Near(end.X, start.X) || !Near(end.Right, start.Right))) fails.Add($"{name}: the left or right edge moved");
            if (dir.Y == 0 && (!Near(end.Y, start.Y) || !Near(end.Bottom, start.Bottom))) fails.Add($"{name}: the top or bottom edge moved");
            if (dir.X < 0 && !Near(end.Right, start.Right)) fails.Add($"{name}: the right edge moved ({start.Right} to {end.Right})");
            if (dir.X > 0 && !Near(end.X, start.X)) fails.Add($"{name}: the left edge moved ({start.X} to {end.X})");
            if (dir.Y < 0 && !Near(end.Bottom, start.Bottom)) fails.Add($"{name}: the bottom edge moved ({start.Bottom} to {end.Bottom})");
            if (dir.Y > 0 && !Near(end.Y, start.Y)) fails.Add($"{name}: the top edge moved ({start.Y} to {end.Y})");
            // far inward: the minimum holds
            MoveTo(at - dir * 2000, true);
            await Frames(1);
            var tiny = R();
            if ((dir.X != 0 && !Near(tiny.W, min.W)) || (dir.Y != 0 && !Near(tiny.H, min.H)))
                fails.Add($"{name}: dragged far in, the window is {tiny.W}x{tiny.H}, the minimum is {min.W}x{min.H}");
            // far outward: it stays inside the main view
            MoveTo(at + dir * 4000, true);
            await Frames(1);
            if (!InsideMain(R()))
                fails.Add($"{name}: dragged far out, the window {R()} left the main view {main}");
            Button(pointer / s, false);
            await Frames(1);
            var drawn = frame.GetGlobalRect();
            var held = R();
            if (!Near(drawn.Position.X, held.X) || !Near(drawn.Position.Y, held.Y) || !Near(drawn.Size.X, held.W) || !Near(drawn.Size.Y, held.H))
                fails.Add($"{name}: drawn at {drawn}, the manager holds {held}");
            steps.Add($"{name}: start {start.W}x{start.H}, out {afterOut.W}x{afterOut.H}, in {end.W}x{end.H}, min {tiny.W}x{tiny.H}, bad {bad}");
        }

        // the title bar moves the window by the pointer's travel
        await Reset();
        {
            var start = R();
            var bar = frame.TitleBar.GetGlobalRect();
            var at = new Vector2(bar.Position.X + 40, bar.GetCenter().Y);
            Button(at, true);
            await Frames(1);
            for (int i = 1; i <= 10; i++) { MoveTo(at + new Vector2(-6 * i, 5 * i), true); await Frames(1); }
            Button(at + new Vector2(-60, 50), false);
            await Frames(1);
            var r = R();
            if (!Near(r.X, start.X - 60) || !Near(r.Y, start.Y + 50) || !Near(r.W, start.W) || !Near(r.H, start.H))
                fails.Add($"title bar: moved by (-60, 50) from {start}, the window is {r}");
        }

        // the close button closes on the first click anywhere on its drawn icon (its middle and the icon's four corners, 14 units square),
        // straight after a resize
        const float icon = 6.5f;
        var closeSpots = new (string Name, Func<Rect2, Vector2> Spot)[]
        {
            ("centre", b => b.GetCenter()),
            ("icon top-right", b => b.GetCenter() + new Vector2(icon, -icon)),
            ("icon top-left", b => b.GetCenter() + new Vector2(-icon, -icon)),
            ("icon bottom-right", b => b.GetCenter() + new Vector2(icon, icon)),
            ("icon bottom-left", b => b.GetCenter() + new Vector2(-icon, icon)),
        };
        foreach (var (spotName, spot) in closeSpots)
        {
            await Reset();
            // a resize first, released, and the click in the same frame
            var grip = (frame.FindChild("resize_bottom_right", true, false) as Control)!.GetGlobalRect().GetCenter();
            Button(grip, true);
            await Frames(1);
            MoveTo(grip + new Vector2(30, 30), true);
            await Frames(1);
            Button(grip + new Vector2(30, 30), false);
            var b = frame.CloseButton.GetGlobalRect();
            var p = spot(b);
            MoveTo(p, false);
            await Frames(1);
            Button(p, true);
            await Frames(1);
            Button(p, false);
            await Frames(2);
            if (floaters.IsOpen(id))
                fails.Add($"close button ({spotName}, at {p} in {b}): one click did not close the window (it is {R()})");
            steps.Add($"close {spotName}: {(floaters.IsOpen(id) ? "still open" : "closed")}");
        }

        _result["floater_drag"] = new Dictionary<string, object?>
        {
            ["percent"] = pct, ["window"] = $"{w}x{h}", ["main"] = $"{main}", ["steps"] = steps, ["problems"] = fails,
        };
        foreach (var f in fails)
            _failures.Add("floater: " + f);
        shell.QueueFree();
        UiTheme.Apply(ThemePresets.Get(ThemePresets.Classic), 100);
        return _failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// --tr-colourwindow=PCT:WxH: the Build window's colour picker window opened over the example Build window (no session or grid) in a window of
    /// W x H pixels at an interface size of PCT %, as the live checks meet it at the test window's size. The run FAILS unless its OK and Cancel are
    /// each wholly in view (UiReach: no scroll area and no window edge cuts them), and a press and release of the pointer, pushed into the viewport
    /// in window pixels as the operating system delivers it, on Cancel and then (opened again) on OK closes the window each time.
    /// </summary>
    private async Task<int> ColourWindowRun(string spec)
    {
        var parts = spec.Split(':', 'x');
        if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pct)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int w) || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int h))
            throw new ArgumentException("--tr-colourwindow must be PCT:WxH, e.g. 100:1152x648");
        float s = pct / 100f;
        var strings = StringTable.English();
        UiTheme.Apply(ThemePresets.Get(ThemePresets.Classic), pct);
        var rig = MakeRig(new Vector2I(w, h));
        rig.Viewport.Size2DOverride = new Vector2I((int)MathF.Round(w / s), (int)MathF.Round(h / s));
        rig.Viewport.Size2DOverrideStretch = true;
        var shell = new ShellRoot(new ShellController(strings, () => { })) { ScaleSink = _ => { }, FixedClockText = TestClockText };
        rig.Viewport.AddChild(shell);
        shell.ShowWorldRows(true);
        shell.SetConnected(true);
        var edit = ExampleBuild(shell, strings);
        shell.Shell.Floaters.Open(EditPanel.FloaterId);
        var pickers = new FacePickerWindows(strings, shell, () => edit);
        await Frames(6);

        var fails = new List<string>();
        var steps = new List<string>();
        void Push(InputEvent e) => rig.Viewport.PushInput(e, false);
        async Task Press(Vector2 logical)
        {
            var at = logical * s;
            Push(new InputEventMouseMotion { Position = at, GlobalPosition = at });
            await Frames(1);
            Push(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = at, GlobalPosition = at, ButtonMask = MouseButtonMask.Left });
            await Frames(1);
            Push(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = at, GlobalPosition = at });
            await Frames(2);
        }
        async Task PressButton(string name)
        {
            pickers.Colours.Open(new ColourRequest("edit.picker.colour.title", new UiColour(0.8f, 0.3f, 0.2f), false, _ => { }));
            await Frames(4);
            if (shell.FindChild(name, true, false) is not Control button || !button.IsVisibleInTree())
            {
                fails.Add($"{name}: not found or not shown");
                return;
            }
            var reach = Main.ReachOf(button);
            var frame = shell.Shell.Floaters.RectOf(FacePickerWindows.ColourPickerId);
            steps.Add($"{name}: {reach.Kind} at {reach.Shown} (button {button.GetGlobalRect()}, window {frame})");
            if (reach.Kind != UiReachKind.Visible || reach.Point is not { } p)
            {
                fails.Add($"{name}: {reach.Kind.ToString().ToLowerInvariant()} (shown {reach.Shown} of {button.GetGlobalRect()}, window {frame}): a person cannot press it");
                shell.Shell.Floaters.Close(FacePickerWindows.ColourPickerId);
                await Frames(2);
                return;
            }
            await Press(new Vector2(p.X, p.Y));
            if (pickers.ColoursOpen)
            {
                fails.Add($"{name}: pressed at {p} and the window stayed open");
                shell.Shell.Floaters.Close(FacePickerWindows.ColourPickerId);
                await Frames(2);
            }
        }
        await PressButton("colour_cancel");
        await PressButton("colour_ok");

        _result["colour_window"] = new Dictionary<string, object?>
        {
            ["percent"] = pct, ["window"] = $"{w}x{h}", ["main"] = $"{shell.MainArea}", ["steps"] = steps, ["problems"] = fails,
        };
        foreach (var f in fails)
            _failures.Add("colour window: " + f);
        shell.QueueFree();
        UiTheme.Apply(ThemePresets.Get(ThemePresets.Classic), 100);
        return _failures.Count == 0 ? 0 : 1;
    }
}
