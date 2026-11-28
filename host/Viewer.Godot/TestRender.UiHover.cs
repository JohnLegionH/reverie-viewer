// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>What a person does in the hover height window, played into the render case's viewport once its picture is taken: keys are pushed
/// to the viewport as the engine would deliver them, and each check reads what the person would see.</summary>
public partial class TestRender
{
    private async Task CheckHoverBehaviour(ShellRoot shell, HoverHeightWindow hover, SubViewport view, StringTable strings, bool takes)
    {
        var line = hover.Entry.GetLineEdit();
        var status = (Label)hover.FindChild("hover_status", true, false);
        void Fail(string what) => _failures.Add("hover: " + what);

        // the filled part of the slider: its look with the control off differs from its look with the control on
        Color Fill(string item) => hover.Slider.GetThemeStylebox(item) is StyleBoxFlat f ? f.BgColor : default;
        hover.ShowSupport(true);
        await Frames(2);
        var (on, onLit) = (Fill("grabber_area"), Fill("grabber_area_highlight"));
        hover.ShowSupport(false);
        await Frames(2);
        if (Fill("grabber_area") == on || Fill("grabber_area_highlight") == onLit)
            Fail("the greyed slider's filled part looks the same as the working one's");
        hover.ShowSupport(true);
        await Frames(2);
        if (Fill("grabber_area") != on || Fill("grabber_area_highlight") != onLit)
            Fail("the slider's filled part did not come back when the control did");

        // the arrows beside the value say what they do
        var (upper, lower) = (new Vector2(hover.Entry.Size.X - 4, 3), new Vector2(hover.Entry.Size.X - 4, hover.Entry.Size.Y - 3));
        string up = hover.Entry.GetTooltip(upper), down = hover.Entry.GetTooltip(lower);
        if (up != strings.Get("hover.arrow_up") || down != strings.Get("hover.arrow_down"))
            Fail($"the arrows' tooltips are \"{up}\" and \"{down}\"");
        hover.ShowSupport(false);
        if (hover.Entry.GetTooltip(upper) != strings.Get("hover.unsupported"))
            Fail("a greyed arrow's tooltip does not say why");
        hover.ShowSupport(true);

        // a refusal stays told when the window's state is shown again (as reopening does), until the person changes the height
        hover.ShowRefused();
        hover.ShowSupport(true);
        if (!status.Visible || status.Text != strings.Get("hover.refused"))
            Fail("the refusal message was lost when the window's state was shown again");
        hover.Slider.Value = 0.3;
        if (status.Visible)
            Fail("the refusal message stayed after the person changed the height");

        // a typed entry that is not a number puts the box back and says why; nothing is told to the owner. This is a person's route: the
        // slider is dragged with the pointer, the box is clicked, Ctrl+A and the letters arrive as key events, and the box is left by a key or
        // by a press elsewhere. Nothing here moves the keyboard or sets the height by a call: a press on a place that cannot take the keyboard
        // leaves it in the box, which only the pointer shows.
        int told = 0;
        var changed = hover.Changed;
        hover.Changed = (_, _) => told++;
        var frame = shell.Frames[HoverHeightFeature.FloaterId];
        var heightLabel = hover.FindChild("hover_row", true, false).GetChild<Control>(0);
        Vector2 In(Control c, float x, float y) => c.GetGlobalTransformWithCanvas() * new Vector2(x, y);
        Vector2 pointer = Vector2.Zero;
        async Task Mouse(Vector2 at, bool? press, bool held = false)
        {
            var to = view.GetFinalTransform() * at;
            if (press is { } down)
                view.PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = down, Position = to, GlobalPosition = to, ButtonMask = down ? MouseButtonMask.Left : 0 });
            else
                view.PushInput(new InputEventMouseMotion { Position = to, GlobalPosition = to, Relative = to - pointer, ButtonMask = held ? MouseButtonMask.Left : 0 });
            pointer = to;
            await Frames(1);
        }
        async Task Click(Vector2 at)
        {
            await Mouse(at, null);
            await Mouse(at, true);
            await Mouse(at, false);
            await Frames(3);
        }
        async Task Drag(float fraction)
        {
            var size = hover.Slider.Size;
            var from = In(hover.Slider, size.X / 2, size.Y / 2);
            var to = In(hover.Slider, fraction <= 0 ? -30 : fraction >= 1 ? size.X + 30 : size.X * fraction, size.Y / 2);
            await Mouse(from, null);
            await Mouse(from, true);
            for (int i = 1; i <= 6; i++)
                await Mouse(from.Lerp(to, i / 6f), null, held: true);
            await Mouse(to, false);
            await Frames(3);
        }
        async Task TypeIntoBox(string text)
        {
            await Click(In(line, line.Size.X / 2, line.Size.Y / 2));
            view.PushInput(new InputEventKey { Keycode = Key.A, PhysicalKeycode = Key.A, CtrlPressed = true, Pressed = true });
            view.PushInput(new InputEventKey { Keycode = Key.A, PhysicalKeycode = Key.A, CtrlPressed = true, Pressed = false });
            await Frames(2);
            foreach (char c in text)
            {
                var code = char.IsAsciiLetterOrDigit(c) ? (Key)char.ToUpperInvariant(c) : c == '-' ? Key.Minus : Key.Period;
                view.PushInput(new InputEventKey { Keycode = code, PhysicalKeycode = code, Unicode = c, Pressed = true });
                view.PushInput(new InputEventKey { Keycode = code, PhysicalKeycode = code, Pressed = false });
                await Frames(1);
            }
            await Frames(2);
            if (line.Text != text)
                Fail($"the typing did not reach the box: it holds \"{line.Text}\", not \"{text}\"");
        }
        bool Readable() => status.IsVisibleInTree() && status.Text == strings.Get("hover.not_a_number") && status.Size.Y > 0
            && frame.GetGlobalRect().Encloses(status.GetGlobalRect());
        (string Name, Func<Task> Leave)[] ways =
        [
            ("Enter", () => { Press(view, Key.Enter); return Task.CompletedTask; }),
            ("Tab", () => { Press(view, Key.Tab); return Task.CompletedTask; }),
            ("a press on the window's empty body", () => Click(In(hover, hover.Size.X / 2, hover.Size.Y - 3))),
            ("a press on the title bar", () => Click(In(frame, frame.Size.X / 2, 10))),
            ("a press on the Height label", () => Click(In(heightLabel, heightLabel.Size.X / 2, heightLabel.Size.Y / 2))),
            ("a press outside the window", () => Click(In(frame, frame.Size.X / 2, frame.Size.Y + 40))),
        ];
        foreach (var (end, fraction, want) in new[] { ("left end", 0f, -2.0), ("right end", 1f, 2.0), ("middle", 0.625f, double.NaN) })
        {
            foreach (var (way, leave) in ways)
            {
                foreach (string word in way == "Enter" ? new[] { "abc", "1.2.3" } : new[] { "abc" })
                {
                    hover.ForgetEntry();
                    await Drag(fraction);
                    double at = hover.Entry.Value;
                    if (double.IsNaN(want) ? Math.Abs(at) > 1.9 : Math.Abs(at - want) > 0.0005)
                        Fail($"the drag to the {end} left the value at {at}");
                    string good = line.Text;
                    if (status.Visible)
                        Fail($"a message already showed before \"{word}\" was typed");
                    await TypeIntoBox(word);
                    told = 0;
                    await leave();
                    await Frames(4);
                    string how = $"\"{word}\" at the {end}, left by {way},";
                    if (line.Text != good || Math.Abs(hover.Entry.Value - at) > 0.0005 || told != 0)
                        Fail($"{how} left \"{line.Text}\" in the box, value {hover.Entry.Value}, {told} change(s) told (want \"{good}\")");
                    if (!Readable())
                        Fail($"{how} was dropped with no message");
                }
            }
            // it stays said, also over a close and reopen, until a valid entry; the same number typed again is one
            hover.ForgetEntry();
            await Drag(fraction);
            string same = line.Text;
            await TypeIntoBox("abc");
            Press(view, Key.Enter);
            await Frames(30);
            shell.Shell.Floaters.Close(HoverHeightFeature.FloaterId);
            await Frames(3);
            shell.Shell.Floaters.Open(HoverHeightFeature.FloaterId);
            await Frames(4);
            if (!Readable())
                Fail($"the message at the {end} did not stay until the next valid entry");
            await TypeIntoBox(same);
            Press(view, Key.Enter);
            await Frames(4);
            if (status.Visible)
                Fail($"the message stayed after \"{same}\" was entered at the {end}");
        }
        hover.ForgetEntry();
        await Drag(0f);
        await TypeIntoBox("abc");
        Press(view, Key.Enter);
        await Frames(4);
        told = 0;
        await TypeIntoBox("0.5");
        Press(view, Key.Enter);
        await Frames(4);
        if (Math.Abs(hover.Entry.Value - 0.5) > 0.0005 || told == 0 || status.Visible)
            Fail($"a number typed after a word: value {hover.Entry.Value}, {told} change(s) told, message shown {status.Visible}");

        // while the grid's refusal shows, a typed word is still answered, and a refusal that arrives after it does not take its place
        hover.ForgetEntry();
        await Drag(0f);
        hover.ShowRefused();
        await TypeIntoBox("abc");
        Press(view, Key.Enter);
        await Frames(4);
        if (!Readable())
            Fail($"a word entered while a refusal showed got no message of its own: \"{status.Text}\"");
        hover.ShowRefused();
        await Frames(4);
        if (!Readable())
            Fail($"a later refusal replaced the not-a-number message with \"{status.Text}\"");
        told = 0;
        await TypeIntoBox("0.5");
        Press(view, Key.Enter);
        await Frames(4);
        if (status.Visible || told == 0)
            Fail($"a number entered after a refusal and a word left \"{status.Text}\" showing, {told} change(s) told");
        hover.Changed = changed;

        // Tab and Shift+Tab go round the window's controls and never out of it
        foreach (bool back in new[] { false, true })
        {
            hover.Slider.GrabFocus();
            await Frames(2);
            var seen = new List<string>();
            for (int i = 0; i < 4; i++)
            {
                Press(view, Key.Tab, back);
                await Frames(2);
                var at = view.GuiGetFocusOwner();
                seen.Add(at == hover.Slider ? "slider" : at == line ? "value" : at?.Name.ToString() ?? "nothing");
            }
            if (string.Join(",", seen) != "value,slider,value,slider")
                Fail($"{(back ? "Shift+Tab" : "Tab")} went {string.Join(",", seen)}");
        }

        // closing gives the keyboard back to the chat field that had it before the window opened
        var floaters = shell.Shell.Floaters;
        floaters.Close(HoverHeightFeature.FloaterId);
        shell.ShowWorldRows(true);
        await Frames(4);
        shell.Chat.Input.GrabFocus();
        await Frames(2);
        floaters.Open(HoverHeightFeature.FloaterId);
        await Frames(4);
        hover.Slider.GrabFocus();
        await Frames(2);
        floaters.Close(HoverHeightFeature.FloaterId);
        await Frames(4);
        if (view.GuiGetFocusOwner() != shell.Chat.Input)
            Fail($"after closing, the keyboard is on {view.GuiGetFocusOwner()?.Name.ToString() ?? "nothing"}, not the chat field");

        // with nothing focused before it opened, closing gives the chat field nothing (the movement keys stay the avatar's)
        view.GuiReleaseFocus();
        shell.Shell.Focus.OnWorldPointerDown();
        await Frames(2);
        floaters.Open(HoverHeightFeature.FloaterId);
        await Frames(4);
        hover.Slider.GrabFocus();
        await Frames(2);
        floaters.Close(HoverHeightFeature.FloaterId);
        await Frames(4);
        if (view.GuiGetFocusOwner() is { } stray)
            Fail($"after closing with nothing focused before, the keyboard is on {stray.Name}");
        var chat = shell.Chat.Input;
        chat.Editable = true;   // as in the world, where it types: Esc then lets go of it as a text field
        string Where() => view.GuiGetFocusOwner()?.Name.ToString() ?? "nothing";
        async Task Journey(string name, Control? want, Func<Task> walk)
        {
            floaters.Close(HoverHeightFeature.FloaterId);
            view.GuiReleaseFocus();
            await Frames(2);
            await walk();
            await Frames(4);
            if (floaters.IsOpen(HoverHeightFeature.FloaterId) || view.GuiGetFocusOwner() != want)
                Fail($"{name}: window open {floaters.IsOpen(HoverHeightFeature.FloaterId)}, the keyboard is on {Where()}");
        }
        async Task Step(Action act) { act(); await Frames(3); }
        void Open() => floaters.Open(HoverHeightFeature.FloaterId);
        void Close() => floaters.Close(HoverHeightFeature.FloaterId);
        var title = FirstFocusable(shell.TopBar.Menus);   // a press on a menu title gives it the keyboard before its entry runs

        await Journey("from the menu bar, closed with the window's button", chat, async () =>
        {
            await Step(() => chat.GrabFocus());
            await Step(() => title?.GrabFocus());
            await Step(Open);
            await Step(() => hover.Slider.GrabFocus());
            await Step(Close);
        });
        await Journey("opened and closed from the menu bar", chat, async () =>
        {
            await Step(() => chat.GrabFocus());
            await Step(Open);
            await Step(() => title?.GrabFocus());
            await Step(Close);
        });
        await Journey("Esc out of the chat field, then Esc to close", null, async () =>
        {
            await Step(() => chat.GrabFocus());
            await Step(Open);
            await Step(() => Press(view, Key.Escape));
            await Step(() => Press(view, Key.Escape));
        });
        await Journey("Esc out of the value box, then Esc to close", chat, async () =>
        {
            await Step(() => chat.GrabFocus());
            await Step(Open);
            await Step(() => line.GrabFocus());
            await Step(() => Press(view, Key.Escape));
            await Step(() => Press(view, Key.Escape));
        });
        await Journey("the chat field used after the window opened", chat, async () =>
        {
            await Step(Open);
            await Step(() => chat.GrabFocus());
            await Step(() => hover.Slider.GrabFocus());
            await Step(Close);
        });
        _result["hover_checks"] = takes ? "on" : "off";
    }

    private static Control? FirstFocusable(Node node)
    {
        foreach (var child in node.GetChildren(true))
        {
            if (child is Control { FocusMode: not Control.FocusModeEnum.None } c && c.IsVisibleInTree())
                return c;
            if (FirstFocusable(child) is { } below)
                return below;
        }
        return null;
    }

    private static void Press(Viewport view, Key key, bool shift = false)
    {
        view.PushInput(new InputEventKey { Keycode = key, PhysicalKeycode = key, ShiftPressed = shift, Pressed = true });
        view.PushInput(new InputEventKey { Keycode = key, PhysicalKeycode = key, ShiftPressed = shift, Pressed = false });
    }

    private static void Type(Viewport view, string text)
    {
        foreach (char c in text)
            view.PushInput(new InputEventKey { Unicode = c, Pressed = true });
    }
}
