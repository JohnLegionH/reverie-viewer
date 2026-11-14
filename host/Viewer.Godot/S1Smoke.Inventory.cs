// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Actions;
using Viewer.Core.Inventory;
using Viewer.Shell;
using Viewer.Shell.Inventory;

namespace Viewer.Godot;

public partial class S1Smoke
{
    // The inventory window, with no grid: the feature installs itself into a shell already drawn (menu and toolbar drawn
    // again), the floater opens inside the window with every control named, the tree shows My Inventory open and the Library
    // closed, a name that tries to add formatting shows as typed, opening a folder asks for it, search narrows the tree, the
    // sort and tab choices change what is shown, the buttons submit the folder actions (the Trash only after a confirmation),
    // a drop moves, a system folder cannot be renamed, the search takes the keyboard from the avatar, and every row has an icon.
    // It also times a redraw of a folder of 5,000 items (reported, not judged).
    private async Task InventoryCheck()
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
            var model = shell.Shell;
            int menusBefore = shell.Menu.Titles.Count;
            var panel = InventoryFeature.Install(shell, redrawGapMs: 0);   // the redraw gap has its own unit tests; here every change shows at once

            Guid Id(int n) => Guid.Parse($"00000000-0000-4000-8000-{n:x12}");
            Guid root = Id(1), textures = Id(2), trash = Id(3), mine = Id(4), sub = Id(5), libRoot = Id(6), libObjects = Id(7), agent = Id(8), libOwner = Id(9), note = Id(100), tex = Id(101);
            var requests = new List<Guid>();
            var inv = new InventoryModel((f, _) => requests.Add(f));
            inv.Apply(new InventorySkeletonReceived(agent, root, libOwner, libRoot,
                new[]
                {
                    new InventoryFolderData(root, Guid.Empty, "", FolderKind.Root, 1), new InventoryFolderData(textures, root, "Textures", FolderKind.Texture, 1),
                    new InventoryFolderData(trash, root, "Trash", FolderKind.Trash, 1), new InventoryFolderData(mine, root, "[b]Mine[/b]", FolderKind.None, 1),
                    new InventoryFolderData(sub, mine, "Sub", FolderKind.None, 1),
                },
                new[] { new InventoryFolderData(libRoot, Guid.Empty, "", FolderKind.Root, 1), new InventoryFolderData(libObjects, libRoot, "Objects", FolderKind.Object, 1) }));
            var submitted = new List<ActionIntent>();
            panel.Bind(inv, i => { submitted.Add(i); return new ActionResult(ActionStatus.Sent, submitted.Count); });
            for (int i = 0; i < 2; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            // the first frame opened My Inventory, which asked for it
            if (!requests.Contains(root)) problems.Add("My Inventory was not asked for when the window first drew");
            inv.Apply(new InventoryFolderFetched(root, true, 2, 4, new[]
            {
                new InventoryFolderData(textures, root, "Textures", FolderKind.Texture, 1), new InventoryFolderData(trash, root, "Trash", FolderKind.Trash, 1),
                new InventoryFolderData(mine, root, "[b]Mine[/b]", FolderKind.None, 1),
            }, new[]
            {
                new InventoryItemData(note, root, "Alpha note", ItemKind.Notecard, 7, 7, 0, DateTimeOffset.UtcNow.AddMinutes(-5)),
                new InventoryItemData(tex, root, "Beta picture", ItemKind.Texture, 0, 0, 0, DateTimeOffset.UtcNow),
            }));
            model.Commands.Execute("floater.inventory");
            for (int i = 0; i < 3; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

            if (shell.Menu.Titles.Count != menusBefore + 1) problems.Add($"menu bar holds {shell.Menu.Titles.Count} menus after the feature added one (was {menusBefore})");
            if (shell.Dock.Buttons.Keys.All(b => b.Id != "inventory")) problems.Add("the dock has no Inventory button");
            var window = GetViewport().GetVisibleRect();
            var frame = shell.Frames[InventoryFeature.FloaterId];
            if (!frame.Visible) problems.Add("the inventory did not open");
            if (!window.Encloses(frame.GetGlobalRect())) problems.Add($"the inventory is outside the window ({frame.GetGlobalRect()})");
            foreach (var c in Descendants(frame).OfType<Control>().Where(c => c is Label or Button or LineEdit or OptionButton or TabBar or global::Godot.Tree))
                if (string.IsNullOrWhiteSpace(c.AccessibilityName)) problems.Add($"inventory {c.Name}: no accessible name");

            var top = panel.TreeView.GetRoot();
            string[] topNames = top.GetChildren().Select(t => t.GetText(0)).ToArray();
            if (topNames.Length != 2) problems.Add($"{topNames.Length} top rows, expected My Inventory and Library");
            var mineRow = top.GetChildren().FirstOrDefault();
            if (mineRow is null || mineRow.Collapsed) problems.Add("My Inventory is not open");
            var libRow = top.GetChildren().Skip(1).FirstOrDefault();
            if (libRow is null || !libRow.Collapsed) problems.Add("the Library is not closed");
            var texts = Flatten(top).Select(t => t.GetText(0)).ToList();
            if (!texts.Contains("[b]Mine[/b]")) problems.Add("a name with formatting marks is not shown as typed");
            if (!texts.Contains("Alpha note")) problems.Add("an item of My Inventory is missing");
            int firstFolder = texts.FindIndex(t => t == "Textures"), trashAt = texts.FindIndex(t => t == "Trash"), mineAt = texts.FindIndex(t => t == "[b]Mine[/b]");
            if (!(firstFolder >= 0 && firstFolder < trashAt && trashAt < mineAt)) problems.Add($"folder order: {string.Join(", ", texts.Take(6))}");
            foreach (var row in Flatten(top).Where(t => InventoryTree.IdOf(t) != Guid.Empty))
                if (row.GetIcon(0) is null) problems.Add($"row '{row.GetText(0)}' has no icon");

            // opening the library asks for it
            if (libRow is not null)
            {
                libRow.Collapsed = false;
                for (int i = 0; i < 2; i++)
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (!requests.Contains(libRoot)) problems.Add("opening the Library did not ask for it");
            }

            // search
            string query = "alpha";
            panel.Search.Text = query;
            panel.Search.EmitSignal(LineEdit.SignalName.TextChanged, query);
            for (int i = 0; i < 2; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var found = Flatten(panel.TreeView.GetRoot()).Select(t => t.GetText(0)).ToList();
            if (!found.Contains("Alpha note") || found.Contains("Beta picture")) problems.Add($"search 'alpha' shows {string.Join(", ", found)}");
            if (!panel.StatusLabel.Text.Contains(inv.UnloadedFolderCount.ToString()) && inv.UnloadedFolderCount > 0) problems.Add($"search status '{panel.StatusLabel.Text}'");
            panel.Search.Text = string.Empty;
            panel.Search.EmitSignal(LineEdit.SignalName.TextChanged, string.Empty);

            // sort by date and the recent tab
            panel.SortChoice.Select(1);
            panel.SortChoice.EmitSignal(OptionButton.SignalName.ItemSelected, 1);
            for (int i = 0; i < 2; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var byDate = Flatten(panel.TreeView.GetRoot()).Select(t => t.GetText(0)).ToList();
            if (byDate.IndexOf("Beta picture") > byDate.IndexOf("Alpha note")) problems.Add("sorting by date did not put the newest item first");
            panel.Tabs.CurrentTab = 1;
            for (int i = 0; i < 2; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var recent = panel.TreeView.GetRoot().GetChildren().Select(t => t.GetText(0)).ToList();
            if (!recent.SequenceEqual(new[] { "Beta picture", "Alpha note" })) problems.Add($"recent shows {string.Join(", ", recent)}");
            panel.Tabs.CurrentTab = 0;
            for (int i = 0; i < 2; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

            // the buttons
            SelectRow(panel, mine);
            panel.NewFolderButton.EmitSignal(Button.SignalName.Pressed);
            if (!submitted.OfType<InventoryFolderCreateIntent>().Any(c => c.ParentId == mine)) problems.Add("New folder did not submit a create under the selected folder");
            SelectRow(panel, textures);
            if (!panel.RenameButton.Disabled || !panel.TrashButton.Disabled) problems.Add("a system folder can be renamed or trashed");
            SelectRow(panel, mine);
            panel.TrashButton.EmitSignal(Button.SignalName.Pressed);
            if (!panel.ConfirmBar.Visible) problems.Add("Move to Trash did not ask first");
            if (submitted.Any(s => s is InventoryFolderTrashIntent)) problems.Add("the Trash was used before the user confirmed");
            panel.ConfirmYes.EmitSignal(Button.SignalName.Pressed);
            if (!submitted.OfType<InventoryFolderTrashIntent>().Any(t => t.FolderId == mine && t.TrashFolderId == trash && t.Confirmed)) problems.Add("confirming did not move the folder to the Trash");
            if (panel.ConfirmBar.Visible) problems.Add("the confirmation stayed open");
            if (inv.Folder(mine)?.ParentId != trash) problems.Add("the folder moved to the Trash did not show there at once");
            panel.Drop(note, mine);
            if (!submitted.OfType<InventoryMoveIntent>().Any(m => m.ItemId == note && m.FolderId == mine)) problems.Add("a drop did not submit a move");
            panel.Drop(textures, mine);
            if (submitted.OfType<InventoryFolderMoveIntent>().Any()) problems.Add("a system folder was moved by a drop");
            if (string.IsNullOrEmpty(panel.NoticeLabel.Text)) problems.Add("a refused drop showed no notice");

            // the search takes the keyboard from the avatar
            panel.Search.GrabFocus();
            for (int i = 0; i < 3; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (model.Focus.MovementAllowed) problems.Add("movement stayed allowed while typing in the search");
            model.Focus.OnEscape();

            // a folder of 5,000 items: one redraw, timed (reported, not judged)
            if (name == "en")
            {
                var big = Enumerable.Range(0, 5000).Select(n => new InventoryItemData(Id(1000 + n), sub, "Item " + n, ItemKind.Object, 6, 6, 0, DateTimeOffset.UtcNow.AddSeconds(-n))).ToList();
                inv.Apply(new InventoryFolderFetched(sub, true, 2, 5000, new List<InventoryFolderData>(), big));
                panel.Expand(trash);   // the folder was moved to the Trash above, and shows there
                panel.Expand(mine);
                panel.Expand(sub);
                var sw = Stopwatch.StartNew();
                panel.Rebuild();
                sw.Stop();
                int rows = Flatten(panel.TreeView.GetRoot()).Count();
                results["rebuild_5000_ms"] = sw.Elapsed.TotalMilliseconds;
                results["rebuild_5000_rows"] = rows;
                Log($"inventory redraw of {rows} rows: {sw.Elapsed.TotalMilliseconds:F1} ms");
                if (rows < 5000) problems.Add($"the big folder showed {rows} rows");
            }

            results[name] = problems;
            Log($"inventory {name}: {problems.Count} problems{(problems.Count > 0 ? ": " + string.Join(" | ", problems) : "")}");
            pass &= problems.Count == 0;
            panel.Bind(null, null);
            RemoveChild(shell);
            shell.QueueFree();
        }
        results["pass"] = pass;
        _result["inventory"] = results;
        if (!pass)
            _failures.Add("inventory floater (see inventory)");
    }

    private static IEnumerable<TreeItem> Flatten(TreeItem parent)
    {
        foreach (var c in parent.GetChildren())
        {
            if (InventoryTree.IdOf(c) != Guid.Empty)
                yield return c;
            foreach (var d in Flatten(c))
                yield return d;
        }
    }

    private static void SelectRow(InventoryPanel panel, Guid id)
    {
        foreach (var row in Flatten(panel.TreeView.GetRoot()))
            if (InventoryTree.IdOf(row) == id)
            {
                row.Select(0);
                return;
            }
    }
}
