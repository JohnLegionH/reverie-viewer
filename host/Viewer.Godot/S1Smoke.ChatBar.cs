// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Shell;
using Viewer.Shell.Sound;

namespace Viewer.Godot;

/// <summary>
/// S-1 (j): what the chat bar's box does that only the engine can show: undo, a single caret, the refused-key sound, a held Tab,
/// the newest nearby lines staying above a grown box, the gesture offers' padding, and no part of a scrolled-out line showing.
/// Keys go through the engine's own input.
/// </summary>
public partial class S1Smoke
{
    private sealed class ChatBarNoSound : IUiSoundOutput
    {
        public bool PlayGrid(UiSoundSlot slot, Guid id, byte[] ogg, float gain) => true;
    }

    private async Task ChatBarCheck()
    {
        var problems = new List<string>();
        var table = StringTable.English();
        var shell = new ShellRoot(new ShellController(table, () => { }));
        AddChild(shell);
        async Task Frames(int n) { for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        async Task Press(Key key, bool ctrl = false, bool shift = false, bool echo = false)
        {
            long unicode = ctrl ? 0 : key is >= Key.A and <= Key.Z ? (shift ? (long)key : char.ToLowerInvariant((char)key)) : key is Key.Slash or Key.Space ? (long)key : 0;
            global::Godot.Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Unicode = (char)unicode, Pressed = true, Echo = echo, CtrlPressed = ctrl, ShiftPressed = shift });
            global::Godot.Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false, CtrlPressed = ctrl, ShiftPressed = shift });
            await Frames(3);
        }
        await Frames(2);
        var sent = new List<Viewer.Core.Actions.ActionIntent>();
        var settings = new SettingsService(null);
        var hub = new Viewer.Shell.Chat.CommsHub(Guid.NewGuid(), new Viewer.Core.Social.NameCache(),
            i => { sent.Add(i); return new Viewer.Core.Actions.ActionResult(Viewer.Core.Actions.ActionStatus.Sent, sent.Count); }, settings, () => DateTimeOffset.Now);
        var sounds = new UiSounds(settings, new ChatBarNoSound(), null, () => false, () => 0);
        var refused = 0;
        sounds.Decided += e => { if (e.Slot == UiSoundSlot.BadKeystroke) refused++; };
        var clicks = new UiSoundClicks(() => sounds);
        AddChild(clicks);
        var nearbyLines = new NearbyLines(table, settings);
        shell.MainView.AddChild(nearbyLines);
        nearbyLines.KeepAbove(shell.Chat);
        var bar = shell.Chat;
        var box = bar.Input;
        shell.ShowWorldRows(true);
        bar.Bind(hub);
        await Frames(4);

        // undo: what was typed is undone
        bar.Start("");
        await Frames(3);
        await Press(Key.H);
        await Press(Key.I);
        if (box.Text != "hi") problems.Add($"typing: the box holds '{box.Text}'");
        await Press(Key.Z, ctrl: true);
        if (box.Text == "hi") problems.Add("undo: Ctrl+Z did not undo typing");
        // undo after text had a line break left out: undo must reach an end, not bring the left-out text back again and again
        bar.Start("ab");
        await Frames(3);
        box.InsertTextAtCaret("c\nd");
        await Frames(3);
        if (box.Text != "abcd") problems.Add($"a line break put in: the box holds '{box.Text}'");
        for (int i = 0; i < 4; i++)
            await Press(Key.Z, ctrl: true);
        if (box.HasUndo()) problems.Add($"undo: after four Ctrl+Z over cleaned text there is still more to undo (box '{box.Text}')");
        // a sent line is not brought back by undo
        bar.Start("");
        await Frames(3);
        await Press(Key.H);
        await Press(Key.I);
        await Press(Key.Enter);
        if (box.Text != "") problems.Add($"send: the box still holds '{box.Text}'");
        await Press(Key.Z, ctrl: true);
        if (box.Text != "") problems.Add($"undo: Ctrl+Z after a send brought back '{box.Text}'");

        // one caret only: neither the engine's add-caret key nor an added caret gives a second
        bar.Start(string.Join(' ', Enumerable.Repeat("word", 80)));
        await Frames(4);
        box.SetCaretLine(0);
        box.SetCaretColumn(2);
        await Press(Key.Down, ctrl: true, shift: true);
        if (box.GetCaretCount() != 1) problems.Add($"carets: Ctrl+Shift+Down gave {box.GetCaretCount()}");
        box.AddCaret(0, 7);
        if (box.GetCaretCount() != 1) problems.Add($"carets: an added caret gave {box.GetCaretCount()}");
        box.RemoveSecondaryCarets();

        // the refused-key sound: a full box, and either end of recall; a refused key is not typing
        bar.Bind(null);
        bar.Bind(hub);   // a new binding empties the recall list
        bar.Start("");
        await Frames(3);
        refused = 0;
        await Press(Key.Up, ctrl: true);
        if (refused != 1) problems.Add($"sound: recall with nothing earlier played it {refused} times");
        refused = 0;
        await Press(Key.Down, ctrl: true);
        if (refused != 1) problems.Add($"sound: recall with nothing later played it {refused} times");
        bar.Start(new string('a', Viewer.Shell.Chat.ChatBoxRules.MaxCharacters));
        await Frames(4);
        refused = 0;
        hub.Nearby.InputLostFocus();   // the typing signal is off, so a key that counted as typing would send it again
        sent.Clear();
        await Press(Key.B);
        if (box.Text.Length != Viewer.Shell.Chat.ChatBoxRules.MaxCharacters || box.Text.Contains('b')) problems.Add("full box: it took another character");
        if (refused != 1) problems.Add($"sound: a key into a full box played it {refused} times");
        if (sent.Count != 0) problems.Add($"full box: a refused key counted as typing ({string.Join(",", sent)})");

        // a held Tab: the first press acts, the repeats do nothing
        bar.Start("");
        await Frames(3);
        // (the control for the check above: a key the box takes does send the typing signal)
        hub.Nearby.InputLostFocus();
        sent.Clear();
        await Press(Key.B);
        if (sent.Count == 0) problems.Add("typing: a key the box took sent no typing signal, so the refused-key check proves nothing");
        bar.Start("");
        await Frames(3);
        await Press(Key.Tab, echo: true);
        if (!box.HasFocus()) problems.Add("held Tab: a repeat left the box");
        bar.Triggers = () => ["/wavehello"];
        box.GrabFocus();
        await Press(Key.Slash);
        await Press(Key.W);
        if (!bar.CompletionOpen) problems.Add($"offers: not open for '{box.Text}'");
        else
        {
            // the offers' content stands the shared padding in from the pop-up's edges (checked while it is open)
            var popup = bar.GetNode<PanelContainer>("gesture_completion");
            var outer = popup.GetGlobalRect();
            var inner = popup.GetChild<Control>(0).GetGlobalRect();
            float least = new[] { inner.Position.X - outer.Position.X, inner.Position.Y - outer.Position.Y, outer.End.X - inner.End.X, outer.End.Y - inner.End.Y }.Min();
            if (least + 0.5f < PanelPadding.Inner) problems.Add($"offers: padding {least}, expected {PanelPadding.Inner}");
            // the pop-up keeps a solid, square face: nothing behind it shows through
            if (popup.GetThemeStylebox("panel") is not StyleBoxFlat { BgColor.A: >= 0.999f, CornerRadiusTopLeft: 0 }) problems.Add("offers: the pop-up's face is not solid and square");
            if (outer.End.Y > box.GetGlobalRect().Position.Y) problems.Add("offers: the pop-up covers the box");
        }
        await Press(Key.Tab);
        if (box.Text != "/wavehello") problems.Add($"Tab with an offer: the box holds '{box.Text}'");
        await Press(Key.Tab, echo: true);
        if (!box.HasFocus()) problems.Add("held Tab after completing: a repeat left the box");
        await Press(Key.Tab);
        if (box.HasFocus()) problems.Add("a second Tab press did not move on from the box");
        bar.Triggers = null;

        // the newest nearby lines stay above the box as it grows, and go back
        float lowest = nearbyLines.OffsetBottom;
        bar.Start("");
        await Frames(4);
        float oneLine = bar.GetGlobalRect().Position.Y;
        bar.Start(string.Join(' ', Enumerable.Range(1, 90).Select(i => "word" + i)));
        await Frames(6);
        if (bar.LinesShown != Viewer.Shell.Chat.ChatBoxRules.MaxVisibleLines) problems.Add($"grown box: {bar.LinesShown} lines");
        float rose = oneLine - bar.GetGlobalRect().Position.Y;
        float linesRose = lowest - nearbyLines.OffsetBottom;
        if (rose <= 0 || Math.Abs(rose - linesRose) > 1) problems.Add($"nearby lines: the bar rose {rose}, the lines {linesRose}");
        if (nearbyLines.GetGlobalRect().End.Y > bar.GetGlobalRect().Position.Y + 0.5f) problems.Add("nearby lines: they end below the top of the grown bar");
        if (Math.Abs(box.Size.Y - box.CustomMinimumSize.Y) > 0.5f) problems.Add($"grown box: {box.Size.Y} tall, its lines take {box.CustomMinimumSize.Y}");

        // a scrolled box shows whole lines only: the strip above the first shown line is the box's face. The text goes in as a paste does.
        bar.Start("");
        await Frames(4);
        box.InsertTextAtCaret(string.Join(' ', Enumerable.Range(1, 130).Select(i => "word" + i)));
        await Frames(8);
        int rows = 1 + box.GetLineWrapCount(0);
        if (rows <= Viewer.Shell.Chat.ChatBoxRules.MaxVisibleLines) problems.Add($"scrolled box: the text makes only {rows} rows, so nothing scrolls");
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = new WindowPicture(GetViewport()).Take();
        var face = (StyleBoxFlat)box.GetThemeStylebox("focus");
        var rect = box.GetGlobalRect();
        var toPixels = GetViewport().GetStretchTransform();
        if (box.ScrollVertical <= 0) problems.Add("scrolled box: it did not scroll, so the strip check proves nothing");
        // from just inside the border, whatever its width is in this theme
        var from = toPixels * (rect.Position + new Vector2(face.ContentMarginLeft, face.BorderWidthTop));
        var to = toPixels * new Vector2(rect.End.X - 24, rect.Position.Y + face.ContentMarginTop - 1);
        var ground = image.GetPixel((int)from.X - 3, (int)from.Y + 2);   // in the left padding, where no text is
        int marked = 0;
        for (int y = (int)from.Y; y < (int)to.Y; y++)
            for (int x = (int)from.X; x < (int)to.X; x++)
            {
                var p = image.GetPixel(x, y);
                if (Math.Abs(p.R - ground.R) + Math.Abs(p.G - ground.G) + Math.Abs(p.B - ground.B) > 0.12f)
                    marked++;
            }
        // (the control: the same test of a pixel finds the text of the first shown line)
        var lineFrom = toPixels * (rect.Position + new Vector2(face.ContentMarginLeft, face.ContentMarginTop));
        var lineTo = toPixels * new Vector2(rect.End.X - 24, rect.Position.Y + face.ContentMarginTop + box.GetLineHeight());
        int inked = 0;
        for (int y = (int)lineFrom.Y; y < (int)lineTo.Y; y++)
            for (int x = (int)lineFrom.X; x < (int)lineTo.X; x++)
            {
                var p = image.GetPixel(x, y);
                if (Math.Abs(p.R - ground.R) + Math.Abs(p.G - ground.G) + Math.Abs(p.B - ground.B) > 0.12f)
                    inked++;
            }
        if (inked == 0) problems.Add("scrolled box: no text found in the first shown line, so the strip check proves nothing");
        _result["chat_bar_scroll"] = new { inked, border = face.BorderWidthTop, rows, scroll = box.ScrollVertical, size = box.Size.Y, min = box.CustomMinimumSize.Y, line = box.GetLineHeight(), style = box.GetThemeStylebox("normal").GetMinimumSize().Y, bar = bar.Size.Y, visible = box.GetVisibleLineCount(), marked, from = from.ToString(), to = to.ToString() };
        if (marked != 0) problems.Add($"scrolled box: {marked} pixels of text above the first shown line (scroll {box.ScrollVertical})");

        bar.Start("");
        await Frames(6);
        if (Math.Abs(nearbyLines.OffsetBottom - lowest) > 0.1f) problems.Add("nearby lines: not back down after the box shrank");

        RemoveChild(clicks);
        clicks.QueueFree();
        RemoveChild(shell);
        shell.QueueFree();
        await Frames(2);
        _result["chat_bar"] = new { pass = problems.Count == 0, problems };
        Log($"chat bar check: {problems.Count} problems{(problems.Count > 0 ? ": " + string.Join(" | ", problems) : "")}");
        foreach (var p in problems)
            _failures.Add("chat bar: " + p);
    }
}
