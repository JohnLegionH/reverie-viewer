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

        // a typed entry that is not a number puts the box back and says why; nothing is told to the owner
        int told = 0;
        var changed = hover.Changed;
        hover.Changed = (_, _) => told++;
        hover.ShowHeight(0.25f);
        await Frames(2);
        string good = line.Text;
        foreach (var (word, byLeaving) in new[] { ("abc", false), ("1.2.3", false), ("abc", true) })
        {
            hover.Slider.Value = 0.25;
            hover.ForgetEntry();
            hover.Slider.GrabFocus();   // away and back, as clicking into the box again: after Enter the box has stopped editing
            await Frames(2);
            line.GrabFocus();
            await Frames(2);
            if (status.Visible)
                Fail($"a message already showed before \"{word}\" was typed");
            told = 0;
            line.SelectAll();
            Type(view, word);
            await Frames(3);
            if (line.Text != word)
                Fail($"the typing did not reach the box: it holds \"{line.Text}\", not \"{word}\"");
            if (byLeaving)
                hover.Slider.GrabFocus();
            else
                Press(view, Key.Enter);
            await Frames(3);
            string how = $"\"{word}\" " + (byLeaving ? "by leaving the box" : "by Enter");
            if (line.Text != good || Math.Abs(hover.Entry.Value - 0.25) > 0.0005 || told != 0)
                Fail($"{how} left \"{line.Text}\" in the box, value {hover.Entry.Value}, {told} change(s) told (want \"{good}\")");
            if (!status.Visible || status.Text != strings.Get("hover.not_a_number"))
                Fail($"{how} was dropped with no message");
        }
        hover.Slider.GrabFocus();
        await Frames(2);
        line.GrabFocus();
        await Frames(2);
        line.SelectAll();
        Type(view, "0.5");
        await Frames(3);
        Press(view, Key.Enter);
        await Frames(3);
        if (Math.Abs(hover.Entry.Value - 0.5) > 0.0005 || told == 0 || status.Visible)
            Fail($"a number typed after a word: value {hover.Entry.Value}, {told} change(s) told, message shown {status.Visible}");
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
