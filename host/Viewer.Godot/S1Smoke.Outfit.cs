// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Actions;
using Viewer.Core.Inventory;
using Viewer.Shell;

namespace Viewer.Godot;

public partial class S1Smoke
{
    // The outfit window and the wear buttons of the inventory window, with no grid: both features install into one shell already
    // drawn (the Content menu holds both entries), the outfit floater opens inside the window with every control named, the
    // current outfit lists attachments, body parts and clothing under their headings with names shown as typed, taking off
    // clothing submits a change that removes its link, a body part cannot be taken off (a notice, nothing submitted), wearing a
    // saved outfit submits a replacement, saving submits the cleaned name, the inventory's Wear button submits and a worn
    // item shows its label.
    private async Task OutfitCheck()
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
            var invPanel = InventoryFeature.Install(shell, redrawGapMs: 0);
            var panel = OutfitFeature.Install(shell, redrawGapMs: 0);
            var content = shell.Shell.Menus.Menus.Where(m => m.Id == "content").ToList();
            if (content.Count != 1 || content[0].Entries.Count != 2) problems.Add("the Content menu does not hold exactly the inventory and outfit entries");

            Guid Id(int n) => Guid.Parse($"00000000-0000-4000-8000-{n:x12}");
            Guid root = Id(1), cof = Id(2), mine = Id(3), cloth = Id(4), body = Id(5), outfit = Id(6), libRoot = Id(7), shirt = Id(100), shape = Id(101), eyes = Id(102), spare = Id(103);
            var asked = new List<Guid>();
            var model = new InventoryModel((f, _) => asked.Add(f));
            var answers = new Dictionary<Guid, InventoryFolderFetched>();   // what the pretend grid answers when a folder is read again
            model.Apply(new InventorySkeletonReceived(Id(8), root, Id(9), libRoot,
                new[]
                {
                    new InventoryFolderData(root, Guid.Empty, "", FolderKind.Root, 1), new InventoryFolderData(cof, root, "Current Outfit", FolderKind.CurrentOutfit, 3),
                    new InventoryFolderData(mine, root, "My Outfits", FolderKind.MyOutfits, 1), new InventoryFolderData(cloth, root, "Clothing", FolderKind.Clothing, 1),
                    new InventoryFolderData(body, root, "Body Parts", FolderKind.Bodypart, 1), new InventoryFolderData(outfit, mine, "[b]Evening[/b]", FolderKind.Outfit, 1),
                },
                new[] { new InventoryFolderData(libRoot, Guid.Empty, "", FolderKind.Root, 1) }));
            InventoryItemData Thing(Guid id, Guid parent, string n, ItemKind k, int asset, uint flags) => new(id, parent, n, k, asset, 18, flags, DateTimeOffset.UtcNow);
            InventoryItemData Link(Guid id, Guid parent, Guid target, string n, string desc) => new(id, parent, n, ItemKind.Link, 24, 18, 0, null, target, desc);
            model.Apply(new InventoryFolderFetched(cloth, true, 2, 2, new List<InventoryFolderData>(), new[]
            {
                Thing(shirt, cloth, "[b]Red shirt[/b]", ItemKind.Clothing, 5, (uint)WearableType.Shirt), Thing(spare, cloth, "Blue shirt", ItemKind.Clothing, 5, (uint)WearableType.Shirt),
            }));
            model.Apply(new InventoryFolderFetched(body, true, 2, 2, new List<InventoryFolderData>(), new[]
            {
                Thing(shape, body, "Shape one", ItemKind.Bodypart, 13, (uint)WearableType.Shape), Thing(eyes, body, "Eyes one", ItemKind.Bodypart, 13, (uint)WearableType.Eyes),
            }));
            model.Apply(new InventoryFolderFetched(cof, true, 3, 3, new List<InventoryFolderData>(), new[]
            {
                Link(Id(200), cof, shirt, "[b]Red shirt[/b]", "@400"), Link(Id(201), cof, shape, "Shape one", ""), Link(Id(202), cof, eyes, "Eyes one", ""),
            }));
            model.Apply(new InventoryFolderFetched(mine, true, 2, 0, new[] { new InventoryFolderData(outfit, mine, "[b]Evening[/b]", FolderKind.Outfit, 1) }, new List<InventoryItemData>()));
            model.Apply(new InventoryFolderFetched(outfit, true, 2, 1, new List<InventoryFolderData>(), new[] { Link(Id(300), outfit, spare, "Blue shirt", "@400") }));

            foreach (var f in new[] { cloth, body, cof, mine, outfit })
            {
                var items = model.ChildItems(f);
                answers[f] = new InventoryFolderFetched(f, true, 5, items.Count, model.ChildFolders(f).ToList(), items.ToList());
            }
            void Settle()
            {
                // a folder read again is answered with what it held: the pretend grid changes nothing
                foreach (var f in asked.Distinct().ToList())
                    if (answers.TryGetValue(f, out var a))
                        model.Apply(a);
                asked.Clear();
            }
            var submitted = new List<ActionIntent>();
            ActionResult Submit(ActionIntent i) { submitted.Add(i); return new ActionResult(ActionStatus.Sent, submitted.Count); }
            panel.Bind(model, Submit);
            invPanel.Bind(model, Submit);
            shell.Shell.Commands.Execute("floater.outfits");
            for (int i = 0; i < 3; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

            var frame = shell.Frames[OutfitFeature.FloaterId];
            if (!frame.Visible) problems.Add("the outfit window did not open");
            if (!GetViewport().GetVisibleRect().Encloses(frame.GetGlobalRect())) problems.Add($"the outfit window is outside the window ({frame.GetGlobalRect()})");
            foreach (var c in Descendants(frame).OfType<Control>().Where(c => c is Label or Button or LineEdit or global::Godot.Tree))
                if (string.IsNullOrWhiteSpace(c.AccessibilityName)) problems.Add($"outfit {c.Name}: no accessible name");

            var heads = panel.CurrentTree.GetRoot().GetChildren().ToList();
            if (heads.Count != 2) problems.Add($"the current outfit shows {heads.Count} headings, expected body parts and clothing");
            var rows = heads.SelectMany(h => h.GetChildren()).Select(r => r.GetText(0)).ToList();
            if (!rows.Contains("[b]Red shirt[/b]")) problems.Add("the clothing name is not shown as typed");
            if (rows.Count != 3) problems.Add($"the current outfit shows {rows.Count} rows, expected 3");
            if (panel.SavedTree.GetRoot().GetChildren().Select(r => r.GetText(0)).SingleOrDefault() != "[b]Evening[/b]") problems.Add("the saved outfit is not listed as typed");

            // taking off clothing submits a change that removes its link and makes none
            panel.SelectWorn(shirt);
            panel.Rebuild();
            if (panel.TakeOffButton.Disabled) problems.Add("Take off is disabled for clothing");
            panel.OnTakeOff();
            Settle();
            if (submitted.LastOrDefault() is not OutfitChangeIntent off || !off.Change.RemoveLinks.SequenceEqual(new[] { Id(200) }) || off.Change.Create.Count != 0)
                problems.Add("taking off the shirt did not submit the removal of its link");

            // a body part cannot be taken off
            int before = submitted.Count;
            panel.SelectWorn(eyes);
            panel.Rebuild();
            if (!panel.TakeOffButton.Disabled) problems.Add("Take off is enabled for a body part");
            panel.OnTakeOff();
            if (submitted.Count != before) problems.Add("a body part was submitted for take off");
            if (string.IsNullOrEmpty(panel.NoticeLabel.Text)) problems.Add("no notice for a body part that cannot be taken off");

            // wearing a saved outfit
            panel.SelectOutfit(outfit);
            panel.Rebuild();
            if (panel.WearOutfitButton.Disabled) problems.Add("Wear outfit is disabled for a saved outfit");
            panel.OnWearOutfit();
            Settle();
            if (submitted.LastOrDefault() is not OutfitChangeIntent wear || !wear.Change.Create.Any(l => l.IsFolder && l.TargetId == outfit) || !wear.Change.RemoveLinks.Contains(Id(200)))
                problems.Add("wearing the saved outfit did not submit its replacement");

            // saving the look
            string typedName = "  Day wear ";
            panel.NameField.Text = typedName;
            panel.OnSave();
            Settle();
            if (submitted.LastOrDefault() is not OutfitSaveIntent save || save.Name != "Day wear" || save.CofId != cof || save.MyOutfitsId != mine)
                problems.Add("saving did not submit the outfit save with the cleaned name");

            // the inventory window: Wear on an item, the worn label, and a worn item is not trashed
            shell.Shell.Commands.Execute("floater.inventory");   // a window that is not open is not drawn
            invPanel.Expand(root);
            invPanel.Expand(cloth);
            for (int i = 0; i < 2; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            string[] texts = Flatten(invPanel.TreeView.GetRoot()).Select(t => t.GetText(0)).ToArray();
            if (!texts.Any(t => t.Contains("[b]Red shirt[/b]") && t.Contains(table.Get("inventory.worn")))) problems.Add("the worn shirt does not carry the worn label");
            if (texts.Any(t => t.Contains("Blue shirt") && t.Contains(table.Get("inventory.worn")))) problems.Add("an item that is not worn carries the worn label");
            SelectRow(invPanel, spare);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (invPanel.WearButton.Disabled) problems.Add("Wear is disabled for a shirt that is not worn");
            if (!invPanel.TakeOffButton.Disabled) problems.Add("Take off is enabled for an item that is not worn");
            int beforeWear = submitted.Count;
            invPanel.OnWear();
            if (submitted.Count != beforeWear + 1 || submitted[^1] is not OutfitChangeIntent w || w.Change.Create.Single().TargetId != spare)
                problems.Add("Wear in the inventory did not submit one change for the shirt");
            invPanel.OnAttach(5);   // a shirt is not an object: refused with a notice, nothing sent
            if (submitted.Count != beforeWear + 1) problems.Add("an attach of clothing was submitted");

            results[name] = problems;
            Log($"outfit {name}: {problems.Count} problems{(problems.Count > 0 ? ": " + string.Join(" | ", problems) : "")}");
            pass &= problems.Count == 0;
            panel.Bind(null, null);
            invPanel.Bind(null, null);
            RemoveChild(shell);
            shell.QueueFree();
        }
        results["pass"] = pass;
        _result["outfit"] = results;
        if (!pass)
            _failures.Add("outfit floater (see outfit)");
    }
}
