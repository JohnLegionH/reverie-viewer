// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Actions;
using Viewer.Core.Appearance;
using Viewer.Core.Inventory;
using Viewer.Shell;
using Viewer.Shell.Appearance;

namespace Viewer.Godot;

public partial class S1Smoke
{
    // The Appearance window, with no grid, in both languages: it is entered from the Me menu and the toolbar and opens inside the window with every
    // control named and no missing string; the Outfit tab is the outfit window's panel; the Edit tab lists what is worn and opens the shape (one
    // slider per setting of the open group, the Height slider among them) and the shirt (a colour, a texture slot, two sliders and the note that the
    // look shows after saving); a moved slider turns Save on and shows unsaved changes; Save submits one save with the moved value and, when the grid
    // answers, the window shows it saved and clean; closing the window or opening something else with unsaved changes asks first, Cancel keeps
    // everything, Don't save puts the value back and submits nothing; Save As submits a copy into the folder of the original; a texture picked in the
    // picker lands in the slot; an item that may not be modified opens read only.
    private async Task AppearanceCheck()
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
            var panel = AppearanceFeature.Install(shell, redrawGapMs: 0);
            var submitted = new List<ActionIntent>();
            ActionResult Submit(ActionIntent i) { submitted.Add(i); return new ActionResult(ActionStatus.Sent, submitted.Count); }
            var ids = AppearanceExample.Bind(panel, Submit);

            if (!shell.Shell.Menus.Menus.Any(m => m.Id == "me" && m.Entries.Any(e => e.Id == "me.appearance"))) problems.Add("the Me menu has no Appearance entry");
            if (!shell.Shell.Toolbar.Buttons.Any(b => b.Id == "toolbar_appearance")) problems.Add("the toolbar has no Appearance button");
            shell.Shell.Commands.Execute("floater.appearance");
            for (int i = 0; i < 3; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var frame = shell.Frames[AppearanceFeature.FloaterId];
            if (!frame.Visible) problems.Add("the Appearance window did not open");
            if (!GetViewport().GetVisibleRect().Encloses(frame.GetGlobalRect())) problems.Add($"the Appearance window is outside the window ({frame.GetGlobalRect()})");
            if (panel.Tabs.GetTabTitle(0) != table.Get("appearance.tab.outfit") || panel.Tabs.GetTabTitle(1) != table.Get("appearance.tab.edit")) problems.Add("the tabs are not titled from the string table");
            if (panel.Outfit.CurrentTree.GetRoot().GetChildren().Count != 2) problems.Add("the Outfit tab does not show body parts and clothing");

            // the Edit tab lists what is worn
            panel.Tabs.CurrentTab = 1;
            panel.Rebuild();
            if (panel.WornList.ItemCount != 3) problems.Add($"the Edit tab lists {panel.WornList.ItemCount} worn items, expected 3");
            if (panel.SaveButton.Disabled != true) problems.Add("Save is on before anything was opened");

            // the shape: sliders, a moved slider, Save
            await panel.OpenAsync(ids.Shape);
            panel.Rebuild();
            var heightSlider = Descendants(panel.Body).OfType<HSlider>().FirstOrDefault(s => s.Name == "slider_33");
            if (heightSlider is null) problems.Add("the shape's first group has no Height slider");
            else
            {
                if (Math.Abs(heightSlider.Value - 0.25) > 0.002) problems.Add($"the Height slider shows {heightSlider.Value}, the file says 0.25");
                if (Descendants(panel.Body).OfType<HSlider>().Count() != 3) problems.Add("the open group does not show exactly its 3 sliders");
                if (!panel.SaveButton.Disabled) problems.Add("Save is on for a clean wearable");
                heightSlider.Value = 0.8;
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                panel.Rebuild();
                if (Math.Abs(panel.Editing!.Editor!.Value(33) - 0.8f) > 0.002) problems.Add("a moved slider did not change the editor");
                if (panel.SaveButton.Disabled) problems.Add("Save is off after a slider moved");
                if (panel.StatusLabel.Text != table.Get("appearance.status.dirty")) problems.Add("the status does not say there are unsaved changes");
            }
            // the other groups are opened on demand
            var headToggle = Descendants(panel.Body).OfType<Button>().FirstOrDefault(b => b.Name == "group_shape_head");
            if (headToggle is null) problems.Add("the Head group has no header");
            else
            {
                headToggle.ButtonPressed = true;
                panel.Rebuild();
                if (Descendants(panel.Body).OfType<HSlider>().Count() != 5) problems.Add("opening the Head group did not add its 2 sliders");
            }
            int before = submitted.Count;
            panel.Editing!.Save();
            if (submitted.Count != before + 1 || submitted[^1] is not WearableSaveIntent save || save.ItemId != ids.Shape || save.Type != WearableType.Shape)
                problems.Add("Save did not submit one save of the shape");
            else if (!WearableAsset.TryParse(save.Data, out var parsed, out _) || Math.Abs(parsed.Params[33] - 0.8f) > 0.01f)
                problems.Add("the saved file does not carry the moved Height");
            panel.Rebuild();
            if (panel.StatusLabel.Text != table.Get("appearance.status.saving")) problems.Add("the status does not say Saving");
            panel.SaveFinished(new WearableSaveFinished(false, true, true, "", ids.Shape));
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            panel.Rebuild();
            if (panel.Editing.HasUnsavedChanges) problems.Add("the window is still dirty after the save finished");
            if (panel.StatusLabel.Text != table.Get("appearance.status.saved")) problems.Add("the status does not say saved");

            // closing with unsaved changes asks first
            panel.Editing.Editor!.SetValue(33, 0.1f);
            panel.Rebuild();
            if (shell.Shell.Floaters.Close(AppearanceFeature.FloaterId)) problems.Add("the window closed with unsaved changes");
            if (!panel.UnsavedPrompt.Visible) problems.Add("closing with unsaved changes did not ask");
            panel.PromptCancel.EmitSignal(BaseButton.SignalName.Pressed);
            if (panel.UnsavedPrompt.Visible || !shell.Shell.Floaters.IsOpen(AppearanceFeature.FloaterId) || !panel.Editing.HasUnsavedChanges) problems.Add("Cancel did not keep the window and the changes");
            shell.Shell.Floaters.Close(AppearanceFeature.FloaterId);
            before = submitted.Count;
            panel.PromptDiscard.EmitSignal(BaseButton.SignalName.Pressed);
            if (submitted.Count != before) problems.Add("Don't save submitted something");
            if (shell.Shell.Floaters.IsOpen(AppearanceFeature.FloaterId)) problems.Add("Don't save did not close the window");
            if (panel.Editing.State != EditState.Closed) problems.Add("Don't save did not close the editor");

            // opening something else with unsaved changes asks too; the picker and the colour on the shirt
            shell.Shell.Commands.Execute("floater.appearance");
            await panel.OpenAsync(ids.Shirt);
            panel.Tabs.CurrentTab = 1;
            panel.Rebuild();
            var colour = Descendants(panel.Body).OfType<ColourButton>().FirstOrDefault();
            if (colour is null) problems.Add("the shirt has no colour button");
            else
            {
                colour.Open();
                if (ColourPickerWindow.For(colour) is { } shared) { shared.Set(new UiColour(0.1f, 0.2f, 0.3f)); shared.Session?.Ok(); shell.Shell.Floaters.Close(ColourPickerWindow.FloaterId); }
                var (r, g, b) = panel.Editing!.Editor!.Colour(WearableKinds.Swatches(WearableType.Shirt)[0]);
                if (Math.Abs(r - 0.1f) > 0.01f || Math.Abs(g - 0.2f) > 0.01f || Math.Abs(b - 0.3f) > 0.01f) problems.Add("a picked colour did not reach the editor");
            }
            if (Descendants(panel.Body).OfType<Label>().All(l => l.Name != "preview_note")) problems.Add("the shirt panel does not say that colours and textures show after saving");
            panel.PickTexture(1, ids.Weave);
            if (panel.Editing!.Editor!.Texture(1) != ids.WeaveAsset) problems.Add("a picked texture did not land in the slot");
            panel.Rebuild();
            var slotButton = Descendants(panel.Body).OfType<Button>().FirstOrDefault(x => x.Name == "slot_1");
            // a chosen texture is a picture beside a Change button, never its id as text
            if (slotButton is null || slotButton.Text != table.Get("appearance.texture.change")) problems.Add("the texture slot does not offer Change for the picked texture");
            if (Descendants(panel.Body).OfType<Control>().All(x => x.Name != "swatch_1")) problems.Add("the texture slot does not show the picked texture as a picture");
            panel.WornList.Select(0);
            panel.WornList.EmitSignal(ItemList.SignalName.ItemSelected, 0);
            if (!panel.UnsavedPrompt.Visible) problems.Add("opening another item with unsaved changes did not ask");
            panel.PromptCancel.EmitSignal(BaseButton.SignalName.Pressed);
            if (panel.Editing.ItemId != ids.Shirt) problems.Add("Cancel did not stay on the shirt");

            // Save As
            string typedCopy = "Copy of shirt";
            panel.SaveAsName.Text = typedCopy;
            panel.SaveAsName.EmitSignal(LineEdit.SignalName.TextChanged, panel.SaveAsName.Text);
            panel.Rebuild();
            if (panel.SaveAsButton.Disabled) problems.Add("Save as is off with a name typed");
            before = submitted.Count;
            panel.Editing.SaveAs(panel.SaveAsName.Text);
            if (submitted.Count != before + 1 || submitted[^1] is not WearableSaveAsIntent copy || copy.SourceItemId != ids.Shirt || copy.FolderId != ids.ClothFolder || copy.Name != "Copy of shirt")
                problems.Add("Save as did not submit a copy into the folder of the original");

            // an item that may not be modified opens read only
            var ro = AppearanceExample.Bind(panel, Submit, canModify: false);
            await panel.OpenAsync(ro.Shirt);
            panel.Rebuild();
            if (panel.Editing!.Editor!.CanModify || !panel.SaveButton.Disabled) problems.Add("an item that may not be modified is not read only");
            if (Descendants(panel.Body).OfType<HSlider>().Any(s => s.Editable)) problems.Add("a slider is editable on an item that may not be modified");

            // every control has a name, and no string is missing
            foreach (var c in Descendants(frame).OfType<Control>().Where(c => c is Label or Button or LineEdit or ItemList or HSlider or ColourButton or TabContainer))
                if (string.IsNullOrWhiteSpace(c.AccessibilityName)) problems.Add($"appearance {c.Name}: no accessible name");
            foreach (var t in Descendants(frame).OfType<Control>().Select(c => c is Label l ? l.Text : c is Button b ? b.Text : "").Where(s => s.Contains("[[")))
                problems.Add("a missing string is shown: " + t);

            results[name] = problems;
            Log($"appearance {name}: {problems.Count} problems{(problems.Count > 0 ? ": " + string.Join(" | ", problems) : "")}");
            pass &= problems.Count == 0;
            panel.Bind(null, null);
            RemoveChild(shell);
            shell.QueueFree();
        }
        results["pass"] = pass;
        _result["appearance"] = results;
        if (!pass)
            _failures.Add("appearance window (see appearance)");
    }
}
