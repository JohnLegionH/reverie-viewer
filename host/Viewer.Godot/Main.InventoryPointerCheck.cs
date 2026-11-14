// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

#if NV_AUTOMATION
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Actions;
using Viewer.Core.Inventory;
using Viewer.Shell;
using Viewer.Shell.Inventory;

namespace Viewer.Godot;

public partial class Main
{
    /// <summary>
    /// The inventory's pointer check (development builds only): real mouse and key events sent through the engine's input path, as a
    /// person's arrive, to the inventory window: the pointer moved over five closed folders (none may open), a click on one folder's
    /// arrow (it opens), a click on a name (selects, toggles nothing), a double-click on a name (toggles), a right-click on the suitcase
    /// folder (its menu shows, nothing opens or closes), Right and Left on the keyboard, an item dragged over a closed folder and held
    /// there for 2 s (it stays closed; the folder shows the drop-target outline and its text, the dragged row the dashed outline), the
    /// drop (the item moves into the closed folder), then Esc. Offline it binds a made-up inventory; in world it uses the real one and
    /// moves one item into a closed folder and back again. Answers flags, counts and times only.
    /// </summary>
    private async Task<object> PointerCheck(InventoryPanel panel, bool live)
    {
        InventoryModel model;
        Guid suitcase, dragItem;
        List<Guid> folders;
        var moves = new List<ActionIntent>();
        if (live)
        {
            if (panel.Model is not { } real)
                return new { refused = "not_in_world" };
            model = real;
            var top = model.ChildFolders(model.RootId);
            suitcase = top.FirstOrDefault(f => f.Name == "My Suitcase")?.Id ?? Guid.Empty;
            // folders with contents first: an empty folder that is open has no arrow to close it with
            folders = top.Where(f => f.Id != suitcase).OrderByDescending(f => f.DescendentCount).Select(f => f.Id).Take(5).ToList();
            dragItem = Guid.Empty;   // chosen below, from a folder's contents once they have arrived
        }
        else
        {
            if (_inventoryFor is not null)
                return new { refused = "in_world" };
            Guid owner = Guid.NewGuid(), root = Guid.NewGuid(), libraryOwner = Guid.NewGuid(), libraryRoot = Guid.NewGuid();
            suitcase = Guid.NewGuid();
            folders = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
            dragItem = Guid.NewGuid();
            model = new InventoryModel((_, _) => { });
            InventoryFolderData F(Guid id, Guid parent, string n, FolderKind kind = FolderKind.None) => new(id, parent, n, kind, 1, 1);
            var tops = new List<InventoryFolderData> { F(suitcase, root, "My Suitcase", InventoryKinds.FolderKindOf(100)) };
            tops.AddRange(folders.Select((f, i) => F(f, root, "Folder " + (i + 1))));
            model.Apply(new InventorySkeletonReceived(owner, root, libraryOwner, libraryRoot, new[] { F(root, Guid.Empty, "My Inventory", FolderKind.Root) }.Concat(tops).ToArray(),
                new[] { F(libraryRoot, Guid.Empty, "Library", FolderKind.Root) }));
            model.Apply(new InventoryFolderFetched(root, true, 2, tops.Count + 1, tops.ToArray(),
                new[] { new InventoryItemData(dragItem, root, "A note", ItemKind.Notecard, 7, 7, 0, null) }));
            foreach (var f in folders.Append(suitcase))
                model.Apply(new InventoryFolderFetched(f, true, 2, 1, Array.Empty<InventoryFolderData>(),
                    new[] { new InventoryItemData(Guid.NewGuid(), f, "Inside", ItemKind.Notecard, 7, 7, 0, null) }));
            panel.Bind(model, intent => { moves.Add(intent); return new ActionResult(ActionStatus.Sent); });
        }

        var floaters = _shell!.Shell.Floaters;
        floaters.Open(InventoryFeature.FloaterId);
        var tree = panel.TreeView;
        async Task Frames(int n) { for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        async Task Wait(double s) { await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout); await Frames(2); }
        TreeItem? Row(Guid id)
        {
            for (var item = tree.GetRoot(); item is not null; item = item.GetNextInTree())
                if (InventoryTree.IdOf(item) == id)
                    return item;
            return null;
        }
        bool Expanded(Guid id) => panel.View?.IsExpanded(id) ?? false;
        // a row scrolled into view first (a real inventory is longer than the window), and a folder's contents arrived (a real
        // inventory reads a folder from the grid when it opens, and the list is drawn again when they arrive)
        async Task<TreeItem?> Shown(Guid id)
        {
            if (Row(id) is not { } r)
                return null;
            tree.ScrollToItem(r, false);
            await Frames(3);
            return Row(id);
        }
        async Task Loaded(Guid id)
        {
            for (int i = 0; i < 40 && model.LoadState(id) is FolderLoad.Loading or FolderLoad.NotLoaded && Expanded(id); i++)
                await Wait(0.25);
            await Wait(0.4);
        }
        Vector2 ToWindow(Vector2 viewport)
        {
            var win = GetWindow().Size; var vis = GetViewport().GetVisibleRect().Size;
            return new Vector2(viewport.X * win.X / vis.X, viewport.Y * win.Y / vis.Y);
        }
        // a row's rectangle in the viewport, and where its name is
        Rect2 RowRect(TreeItem item)
        {
            var r = tree.GetItemAreaRect(item, 0);
            var xf = tree.GetGlobalTransformWithCanvas();
            return new Rect2(xf * r.Position, r.Size);
        }
        Vector2 NameAt(TreeItem item) { var r = RowRect(item); return new Vector2(r.Position.X + r.Size.X * 0.6f, r.Position.Y + r.Size.Y / 2); }
        int Depth(TreeItem item) { int d = 0; for (var p = item.GetParent(); p is not null && p != tree.GetRoot(); p = p.GetParent()) d++; return d; }
        Vector2 last = Vector2.Zero;
        void Move(Vector2 at, bool leftHeld = false)
        {
            var w = ToWindow(at);
            Input.ParseInputEvent(new InputEventMouseMotion { Position = w, GlobalPosition = w, Relative = w - last, ButtonMask = leftHeld ? MouseButtonMask.Left : 0 });
            last = w;
        }
        void Button(Vector2 at, MouseButton b, bool down, bool doubleClick = false)
        {
            var w = ToWindow(at);
            Input.ParseInputEvent(new InputEventMouseButton { Position = w, GlobalPosition = w, ButtonIndex = b, Pressed = down, DoubleClick = doubleClick,
                ButtonMask = down ? (b == MouseButton.Right ? MouseButtonMask.Right : MouseButtonMask.Left) : 0 });
            last = w;
        }
        void Key(Key k) { Input.ParseInputEvent(new InputEventKey { Keycode = k, PhysicalKeycode = k, Pressed = true }); Input.ParseInputEvent(new InputEventKey { Keycode = k, PhysicalKeycode = k, Pressed = false }); }
        async Task Click(Vector2 at, MouseButton b = MouseButton.Left) { Move(at); await Frames(1); Button(at, b, true); await Frames(1); Button(at, b, false); await Wait(0.45); }

        try
        {
            await Wait(0.5);
            foreach (var f in folders.Append(suitcase))
                if (Row(f) is { Collapsed: false } open) { open.Collapsed = true; await Frames(2); }
            await Wait(0.3);
            var present = folders.Where(f => Row(f) is not null).ToList();
            if (present.Count == 0)
                return new { refused = "no_folders_drawn" };

            // 1. the pointer passes over the folders (no button): nothing opens
            foreach (var f in present)
                for (int step = 0; step < 4; step++)
                {
                    if (Row(f) is { } r) Move(NameAt(r) + new Vector2(step * 6 - 9, 0));
                    await Wait(0.1);
                }
            await Wait(0.5);
            int openedByHover = present.Count(Expanded);

            // 2. a click on a name selects it and toggles nothing; the folder used for this and for the arrow is one with contents,
            // because an empty folder that is open has no arrow to close it with
            Guid first = present.FirstOrDefault(f => model.Folder(f)?.DescendentCount > 0);
            if (first == Guid.Empty) first = present[0];
            await Click(NameAt((await Shown(first))!));
            bool nameSelects = panel.SelectedItem == first && !Expanded(first);

            // 3. a click on the arrow opens the folder: the arrow is drawn in the indent just left of the row's icon
            float margin = tree.GetThemeConstant("item_margin");
            var arrowRow = Row(first)!;
            var rr = RowRect(arrowRow);
            var tried = new List<float>();
            bool arrowOpens = false;
            float arrowX = 0;
            foreach (float k in new[] { 0.5f, -0.5f, 1.5f })
            {
                float x = rr.Position.X + Depth(arrowRow) * margin + margin * k;
                tried.Add(MathF.Round(x - rr.Position.X));
                await Click(new Vector2(x, rr.Position.Y + rr.Size.Y / 2));
                if (Expanded(first)) { arrowOpens = true; arrowX = x - rr.Position.X; break; }
            }
            if (arrowOpens)
                await Loaded(first);
            // and a second click on the arrow closes it again
            bool arrowCloses = false;
            if (arrowOpens && await Shown(first) is { } again)
            {
                var ar = RowRect(again);
                await Wait(0.3);
                await Click(new Vector2(ar.Position.X + arrowX, ar.Position.Y + ar.Size.Y / 2));
                arrowCloses = !Expanded(first);
            }

            // 4. a double-click on a name toggles it
            bool doubleToggles = false;
            if (present.Count > 1 && await Shown(present[1]) is { } second)
            {
                var at = NameAt(second);
                Move(at); await Frames(1);
                Button(at, MouseButton.Left, true); await Frames(1); Button(at, MouseButton.Left, false); await Frames(1);
                Button(at, MouseButton.Left, true, doubleClick: true); await Frames(1); Button(at, MouseButton.Left, false);
                await Wait(0.45);
                doubleToggles = Expanded(present[1]);
                if (doubleToggles) { await Loaded(present[1]); panel.Activate(present[1]); await Wait(0.3); }
            }

            // 5. a right-click on the suitcase folder: its menu shows, it is selected, nothing opens or closes
            bool? menuShows = null, rightToggled = null, rightSelects = null;
            if (suitcase != Guid.Empty && await Shown(suitcase) is { } sc)
            {
                bool before = Expanded(suitcase);
                await Click(NameAt(sc), MouseButton.Right);
                menuShows = panel.Context.Visible;
                rightSelects = panel.SelectedItem == suitcase;
                rightToggled = Expanded(suitcase) != before;
                Key(global::Godot.Key.Escape);
                await Wait(0.3);
                if (panel.Context.Visible) { panel.Context.Hide(); await Frames(2); }
                // and a right press on its arrow toggles nothing either
                if (await Shown(suitcase) is { } sc2)
                {
                    var sr = RowRect(sc2);
                    bool b2 = Expanded(suitcase);
                    await Click(new Vector2(sr.Position.X + (arrowOpens ? arrowX : margin * 0.5f), sr.Position.Y + sr.Size.Y / 2), MouseButton.Right);
                    rightToggled |= Expanded(suitcase) != b2;
                    panel.Context.Hide();
                    await Frames(2);
                }
            }

            // 6. keys: Right opens the selected folder, Left closes it, Left again moves to its parent
            await Click(NameAt((await Shown(first))!));
            tree.GrabFocus();
            Key(global::Godot.Key.Right); await Wait(0.3);
            bool rightKeyOpens = Expanded(first);
            await Loaded(first);
            if (await Shown(first) is { } kept) { kept.Select(0); tree.GrabFocus(); await Frames(2); }
            Key(global::Godot.Key.Left); await Wait(0.3);
            bool leftKeyCloses = !Expanded(first);
            Key(global::Godot.Key.Left); await Wait(0.3);
            bool leftKeyToParent = panel.SelectedItem == model.RootId;

            // 7. an item dragged over a closed folder and held there 2 s: the folder stays closed, shows the drop-target outline and its
            // text, the dragged row the dashed outline; the drop moves the item into the closed folder
            bool? openWhileHeld = null, outlineShown = null, sourceDashed = null, dragStarted = null, heldAfterPress = null, droppedIn = null, movedBack = null;
            string? seenMid = null, dropText = null;
            int dropsMoved = 0;
            Guid target = present.Where(f => f != first).Skip(1).FirstOrDefault();
            if (target == Guid.Empty) target = present[^1];
            Guid home = Guid.Empty;
            if (live)
            {
                // an item from the first folder's contents (its row drawn), never one from the target folder
                if (await Shown(first) is { } fr && fr.Collapsed) { panel.Activate(first); await Loaded(first); }
                dragItem = model.ChildItems(first).Select(i => i.Id).FirstOrDefault(id => Row(id) is not null);
                home = dragItem != Guid.Empty ? model.Item(dragItem)?.ParentId ?? Guid.Empty : Guid.Empty;
            }
            else if (dragItem != Guid.Empty)
                home = model.Item(dragItem)?.ParentId ?? Guid.Empty;
            if (Row(target) is { Collapsed: false } openTarget) { openTarget.Collapsed = true; await Frames(2); }
            TreeItem? source = dragItem != Guid.Empty ? await Shown(dragItem) : null;
            if (source is not null && Row(target) is not null)
            {
                var from = NameAt(source);
                Move(from); await Frames(1);
                Button(from, MouseButton.Left, true); await Frames(1);
                heldAfterPress = Input.IsMouseButtonPressed(MouseButton.Left);
                for (int i = 1; i <= 6; i++) { Move(from + new Vector2(i * 4, i * 4), leftHeld: true); await Frames(1); }
                dragStarted = GetViewport().GuiIsDragging();
                var over = NameAt(Row(target)!);
                Move(over, leftHeld: true);
                // the engine places an injected pointer elsewhere while it drags, so the tree is told where the pointer is held
                tree.HoldDragAt(tree.GetGlobalTransformWithCanvas().AffineInverse() * over);
                var t0 = Time.GetTicksMsec();
                bool everOpen = false;
                while (Time.GetTicksMsec() - t0 < 2000)
                {
                    Move(over + new Vector2((Time.GetTicksMsec() % 2) * 2, 0), leftHeld: true);
                    await Frames(1);
                    everOpen |= Expanded(target) || Row(target) is { Collapsed: false };
                }
                await Frames(2);
                openWhileHeld = everOpen || Expanded(target);
                outlineShown = tree.Cue.Target == target;
                sourceDashed = tree.Cue.Source == dragItem;
                dropText = tree.DropText;
                seenMid = tree.DragSeen.ToString();
                tree.HoldDragAt(null);
                GetViewport().GuiCancelDrag();   // the drop is made below as the tree's drop hands it on
                Button(over, MouseButton.Left, false);
                await Wait(0.3);
                int before = moves.Count;
                panel.Drop(dragItem, target);   // into the closed folder
                if (live)
                {
                    for (int i = 0; i < 20 && model.Item(dragItem)?.ParentId != target; i++) await Wait(0.25);
                    droppedIn = model.Item(dragItem)?.ParentId == target && !Expanded(target);
                    dropsMoved = droppedIn == true ? 1 : 0;
                }
                else
                {
                    await Wait(0.3);
                    dropsMoved = moves.Count - before;   // the made-up inventory records the move it was asked for
                    droppedIn = dropsMoved == 1 && !Expanded(target);
                }
                if (live && droppedIn == true && home != Guid.Empty)
                {
                    // the test account's inventory is left as it was: the item goes back where it came from
                    panel.Drop(dragItem, home);
                    for (int i = 0; i < 20 && model.Item(dragItem)?.ParentId != home; i++) await Wait(0.25);
                    movedBack = model.Item(dragItem)?.ParentId == home;
                }
            }

            // 8. Esc closes the focused window
            tree.GrabFocus();
            Key(global::Godot.Key.Escape); await Wait(0.2);
            Key(global::Godot.Key.Escape); await Wait(0.3);
            bool closedByEsc = !floaters.IsOpen(InventoryFeature.FloaterId);

            return new
            {
                live, folders_hovered = present.Count, opened_by_hover = openedByHover,
                name_click_selects_without_toggle = nameSelects,
                arrow_click_opens = arrowOpens, arrow_click_closes = arrowCloses, arrow_folder_contents = model.Folder(present[0])?.DescendentCount, arrow_x_tried = tried,
                double_click_toggles = doubleToggles,
                suitcase = suitcase != Guid.Empty, right_click_menu = menuShows, right_click_selects = rightSelects, right_click_toggled = rightToggled,
                key_right_opens = rightKeyOpens, key_left_closes = leftKeyCloses, key_left_to_parent = leftKeyToParent,
                drag_open_while_held = openWhileHeld, drag_outline_on_target = outlineShown, drag_source_dashed = sourceDashed, drag_text = dropText,
                drag_started = dragStarted, drag_seen = seenMid, dropped_into_closed_folder = droppedIn, moved_back = movedBack, target_row = Row(target) is { } tr ? RowRect(tr).ToString() : null, tree_rect = tree.GetGlobalRect().ToString(), held_after_press = heldAfterPress,
                drops_moved = dropsMoved, drag_still_running = GetViewport().GuiIsDragging(),
                esc_closed_window = closedByEsc,
            };
        }
        finally
        {
            if (!live)
                panel.Bind(null, null);
            floaters.Close(InventoryFeature.FloaterId);
        }
    }
}
#endif
