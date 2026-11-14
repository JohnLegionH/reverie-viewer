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
    private sealed class CountingStore : ISettingsStore
    {
        public string? Text;
        public int Writes;
        public string? Read() => Text;
        public bool Write(string text) { Text = text; Writes++; return true; }
    }

    // The Preferences window, with no grid: it opens inside the window with every control named; each category shows its own page and
    // nothing else; an edit on the Graphics page is seen at once by the settings (nothing written), choosing a preset sets the quality
    // values, editing one of them makes the preset Custom, Cancel puts the saved values back, and OK writes every page and closes the window; a
    // setting that needs a restart carries its note; the setting kept per account is dimmed until a login; and the two confirmed actions
    // (Clear cache, Clear saved login names) do nothing until the question is answered yes.
    private async Task PreferencesCheck()
    {
        var results = new Dictionary<string, object?>();
        bool pass = true;
        foreach (var (name, table) in new[] { ("en", StringTable.English()), ("pseudo", StringTable.Pseudo()) })
        {
            var problems = new List<string>();
            var shell = new ShellRoot(new ShellController(table, () => { }));
            AddChild(shell);
            for (int i = 0; i < 2; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var store = new CountingStore();
            var settings = new SettingsService(store);
            int cacheCleared = 0, namesCleared = 0;
            var panel = PreferencesFeature.Install(shell, settings, () => ++cacheCleared * 3, () => { namesCleared++; return true; });
            shell.Shell.Commands.Execute("floater.preferences");
            for (int i = 0; i < 3; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

            var frame = shell.Frames[PreferencesFeature.FloaterId];
            if (!frame.Visible) problems.Add("the window did not open");
            if (!GetViewport().GetVisibleRect().Encloses(frame.GetGlobalRect())) problems.Add($"the window is outside the screen ({frame.GetGlobalRect()})");
            // every control with something to read or use has a name for assistive technology (an empty message line has nothing to name)
            foreach (var c in Descendants(frame).OfType<Control>().Where(c => c is Label or Button or CheckBox or SpinBox or OptionButton or LineEdit))
                if (string.IsNullOrWhiteSpace(c.AccessibilityName) && c.GetParent() is not SpinBox && !(c is Label { Text.Length: 0 })) problems.Add($"{c.Name}: no accessible name");

            T? Find<T>(string nodeName) where T : Node => Descendants(frame).OfType<T>().FirstOrDefault(n => n.Name == nodeName);

            // each category shows its own page
            foreach (var id in PreferencesPanel.Categories)
            {
                panel.ShowCategory(id);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                var visiblePages = Descendants(frame).OfType<Control>().Where(n => (n.Name.ToString().StartsWith("page_", StringComparison.Ordinal) || n.Name == "scroll") && n.Visible).Select(n => n.Name.ToString()).ToList();
                string expected = id == "interface" ? "scroll" : "page_" + id;
                if (visiblePages.Count != 1 || visiblePages[0] != expected) problems.Add($"category {id} shows {string.Join(",", visiblePages)} (expected {expected})");
                if (Find<Label>("category")?.Text != table.Get("prefs.category." + id)) problems.Add($"category {id}: the title does not name it");
            }

            // Graphics: previews, presets, Custom, Cancel, Save
            panel.ShowCategory("graphics");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var preset = (OptionButton?)panel.ControlFor("graphics_preset");
            var draw = (SpinBox?)panel.ControlFor("draw_distance");
            var clouds = (CheckBox?)panel.ControlFor("clouds_enabled");
            var backendNote = Find<Label>("note_graphics_backend");
            if (preset is null || draw is null || clouds is null) problems.Add("the Graphics page lacks its quality controls");
            else
            {
                if (backendNote?.Text != table.Get("prefs.backend.notice")) problems.Add("the backend carries no restart notice");
                int low = Array.IndexOf(PreferenceCatalog.Presets.ToArray(), GraphicsPresets.Low);
                preset.Select(low);
                preset.EmitSignal(OptionButton.SignalName.ItemSelected, low);
                if (settings.Get(Settings.DrawDistance) != 64 || settings.Get(Settings.MaxTextureResolution) != 512 || !settings.Get(Settings.CloudsEnabled)) problems.Add("the Low level did not set its values (and leave the clouds alone)");
                if (store.Writes != 0) problems.Add("choosing a preset wrote the settings file before Save");
                if (draw.Value != 64 || !clouds.ButtonPressed) problems.Add("the controls do not show the Low level's values");
                draw.Value = 300;
                if (settings.Get(Settings.DrawDistance) != 300) problems.Add("editing the draw distance was not seen by the settings");
                if (preset.Selected != Array.IndexOf(PreferenceCatalog.Presets.ToArray(), GraphicsPresets.Custom)) problems.Add("editing one value did not make the preset Custom");
                panel.CancelButton.EmitSignal(BaseButton.SignalName.Pressed);
                if (settings.Get(Settings.DrawDistance) != 128 || settings.Get(Settings.CloudsEnabled) != true) problems.Add("Cancel did not put the saved values back");
                if (draw.Value != 128 || preset.Selected != Array.IndexOf(PreferenceCatalog.Presets.ToArray(), GraphicsPresets.HighUltra)) problems.Add("after Cancel the controls do not show the saved values");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (frame.Visible) problems.Add("Cancel did not close the window");
                shell.Shell.Commands.Execute("floater.preferences");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (!frame.Visible) problems.Add("the window did not open again");
                draw.Value = 500;
            }

            // Chat: OK writes every page (this one and the Graphics edit) and closes the window
            panel.ShowCategory("chat");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var font = (SpinBox?)panel.ControlFor("chat_font_size");
            if (font is null) problems.Add("the Chat page lacks the text size");
            else
            {
                font.Value = 22;
                panel.SaveButton.EmitSignal(BaseButton.SignalName.Pressed);
                var reread = new SettingsService(new CountingStore { Text = store.Text });
                if (reread.Get(Settings.ChatFontSize) != 22) problems.Add("OK did not write the text size");
                if (reread.Get(Settings.DrawDistance) != 500) problems.Add("OK did not write the Graphics edit of another page");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (frame.Visible) problems.Add("OK did not close the window");
                shell.Shell.Commands.Execute("floater.preferences");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                panel.ResetButton.EmitSignal(BaseButton.SignalName.Pressed);
                if (settings.Get(Settings.ChatFontSize) != 15) problems.Add("Reset to defaults did not preview the default text size");
                panel.CancelButton.EmitSignal(BaseButton.SignalName.Pressed);
                if (settings.Get(Settings.ChatFontSize) != 22) problems.Add("Cancel after Reset did not bring back the saved text size");
            }

            // Privacy: the per-account switch waits for a login
            panel.ShowCategory("privacy");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var share = (CheckBox?)panel.ControlFor("share_online_status");
            var accountNote = Find<Label>("account_share_online_status");
            if (share is null || accountNote is null) problems.Add("the Privacy page lacks the online status switch or its note");
            else
            {
                if (!share.Disabled || !accountNote.Visible) problems.Add("the online status switch is usable before any login");
                panel.Bind("0123456789abcdef", null);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (share.Disabled || accountNote.Visible) problems.Add("the online status switch stays dimmed after a login");
                share.ButtonPressed = false;
                if (settings.Get(Settings.ShareOnlineStatusOf("0123456789abcdef"))) problems.Add("turning the online status off was not seen by the settings");
            }
            var namesButton = Find<Button>("clear_names_button");
            var namesAsk = Find<HBoxContainer>("clear_names_ask");
            var namesYes = Find<Button>("clear_names_yes");
            var namesNo = Find<Button>("clear_names_no");
            if (namesButton is null || namesAsk is null || namesYes is null || namesNo is null) problems.Add("the Privacy page lacks the clear-names button");
            else
            {
                namesButton.EmitSignal(BaseButton.SignalName.Pressed);
                if (!namesAsk.Visible || namesCleared != 0) problems.Add("Clear saved login names did not ask first");
                namesNo.EmitSignal(BaseButton.SignalName.Pressed);
                if (namesAsk.Visible || namesCleared != 0) problems.Add("answering no cleared the names or kept the question");
                namesButton.EmitSignal(BaseButton.SignalName.Pressed);
                namesYes.EmitSignal(BaseButton.SignalName.Pressed);
                if (namesCleared != 1) problems.Add("answering yes did not clear the names once");
            }

            // Cache: the restart note and the confirmed clear
            panel.ShowCategory("cache");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (Find<Label>("note_cache_folder")?.Text != table.Get("prefs.note.restart")) problems.Add("the cache folder carries no restart note");
            var cacheButton = Find<Button>("clear_cache_button");
            var cacheAsk = Find<HBoxContainer>("clear_cache_ask");
            var cacheYes = Find<Button>("clear_cache_yes");
            if (cacheButton is null || cacheAsk is null || cacheYes is null) problems.Add("the Cache page lacks the clear button");
            else
            {
                cacheButton.EmitSignal(BaseButton.SignalName.Pressed);
                if (!cacheAsk.Visible || cacheCleared != 0) problems.Add("Clear cache did not ask first");
                cacheYes.EmitSignal(BaseButton.SignalName.Pressed);
                if (cacheCleared != 1) problems.Add("answering yes did not clear the cache once");
            }

            // the chat window's text follows the text size while it is up (it was read once, when the text was made, before)
            var history = ChatFeature.History(table.Get("chat.history"), settings);
            AddChild(history);
            int sizeBefore = history.GetThemeFontSize("normal_font_size");
            settings.SetPreview(Settings.ChatFontSize, 30);
            int sizeDuring = history.GetThemeFontSize("normal_font_size");
            settings.Rollback([Settings.ChatFontSize.Key]);
            int sizeAfter = history.GetThemeFontSize("normal_font_size");
            if (sizeBefore != settings.Get(Settings.ChatFontSize) || sizeDuring != 30 || sizeAfter != sizeBefore) problems.Add($"the chat text size does not follow the setting ({sizeBefore}, {sizeDuring}, {sizeAfter})");
            RemoveChild(history);
            history.QueueFree();

            // window opacity: the window that has the focus is solid and the other lets 5 % through (the official viewer's two values);
            // the sliders preview at once, Cancel puts back what was saved and Save writes both
            panel.ShowCategory("interface");
            if (!frame.Visible)
                shell.Shell.Commands.Execute("floater.preferences");   // OK closed it above
            shell.Shell.Commands.Execute("floater.about");
            for (int i = 0; i < 3; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var aboutFrame = shell.Frames[ShellController.AboutFloaterId];
            float PanelAlpha(Control c) => c.GetThemeStylebox("panel") is StyleBoxFlat flat ? flat.BgColor.A : -1f;
            if (Math.Abs(PanelAlpha(aboutFrame) - 1f) > 0.002f) problems.Add($"the window that has the focus is not solid ({PanelAlpha(aboutFrame):0.###})");
            if (Math.Abs(PanelAlpha(frame) - 0.95f) > 0.002f) problems.Add($"the other window is not at 95 % ({PanelAlpha(frame):0.###})");
            int writesBefore = store.Writes;
            panel.ActiveOpacitySlider.Value = 60;
            panel.InactiveOpacitySlider.Value = 30;
            for (int i = 0; i < 2; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (settings.Get(Settings.ActiveFloaterTransparency) != 0.6f || settings.Get(Settings.InactiveFloaterTransparency) != 0.3f) problems.Add("the opacity sliders were not seen by the settings");
            if (Math.Abs(PanelAlpha(aboutFrame) - 0.6f) > 0.002f || Math.Abs(PanelAlpha(frame) - 0.3f) > 0.002f) problems.Add($"the windows do not follow the sliders ({PanelAlpha(aboutFrame):0.###}, {PanelAlpha(frame):0.###})");
            if (store.Writes != writesBefore) problems.Add("moving an opacity slider wrote the settings file before Save");
            panel.Opacity.Cancel();
            for (int i = 0; i < 2; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (Math.Abs(PanelAlpha(aboutFrame) - 1f) > 0.002f || Math.Abs(PanelAlpha(frame) - 0.95f) > 0.002f || panel.ActiveOpacitySlider.Value != 100 || panel.InactiveOpacitySlider.Value != 95)
                problems.Add("Cancel did not put the saved opacities back");
            panel.ActiveOpacitySlider.Value = 80;
            panel.SaveButton.EmitSignal(BaseButton.SignalName.Pressed);
            if (new SettingsService(new CountingStore { Text = store.Text }).Get(Settings.ActiveFloaterTransparency) != 0.8f) problems.Add("Save did not write the window opacity");
            panel.ResetButton.EmitSignal(BaseButton.SignalName.Pressed);
            if (settings.Get(Settings.ActiveFloaterTransparency) != 1.0f || settings.Get(Settings.InactiveFloaterTransparency) != 0.95f) problems.Add("Reset did not preview the official opacities");
            panel.Opacity.Cancel();
            shell.Shell.Commands.Execute("floater.about");   // closes it again

            // closing the window puts back what was not saved
            settings.SetPreview(Settings.DrawDistance, 200);
            panel.CancelEdits();
            if (settings.Get(Settings.DrawDistance) != 500) problems.Add("closing the window kept an unsaved edit");   // 500 is what OK saved above

            results[name] = problems;
            Log($"preferences {name}: {problems.Count} problems{(problems.Count > 0 ? ": " + string.Join(" | ", problems) : "")}");
            pass &= problems.Count == 0;
            RemoveChild(shell);
            shell.QueueFree();
        }
        results["pass"] = pass;
        _result["preferences"] = results;
        if (!pass)
            _failures.Add("preferences window (see preferences)");
    }
}
