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

/// <summary>
/// The live check that an inventory move stays moved (development builds only): an item is dropped on a closed folder as a drop in
/// the tree does, and after the viewer is started again and logged in the folders are read fresh from the grid to see where the
/// item is; the same drop then moves it back. Answers ids and flags only; never names.
/// </summary>
public partial class Main
{
    private async Task<object> MoveLiveCall(InventoryPanel panel, InventoryModel model, string action, string? itemText, string? folderText)
    {
        async Task<bool> Loaded(Guid folder)
        {
            model.EnsureLoaded(folder);
            for (int i = 0; i < 120 && model.LoadState(folder) != FolderLoad.Loaded; i++)
                await ToSignal(GetTree().CreateTimer(0.25), SceneTreeTimer.SignalName.Timeout);
            return model.LoadState(folder) == FolderLoad.Loaded;
        }
        Guid.TryParse(itemText, out var item);
        Guid.TryParse(folderText, out var folder);
        var rules = new InventoryActions(model, _ => new ActionResult(ActionStatus.Failed, 0, null));
        switch (action)
        {
            case "pick":
            {
                // an item that is not worn and not a link, in a top-level folder; a closed top-level folder the rules let it go into
                var top = model.ChildFolders(model.RootId)
                    .Where(f => f.Kind is not (FolderKind.Trash or FolderKind.CurrentOutfit or FolderKind.Outfit or FolderKind.LostAndFound or FolderKind.Favorites or FolderKind.Inbox or FolderKind.MyOutfits))
                    .Where(f => f.Name != "My Suitcase")
                    .OrderByDescending(f => f.DescendentCount).ToList();
                foreach (var from in top)
                {
                    if (!await Loaded(from.Id))
                        continue;
                    var candidate = model.ChildItems(from.Id).FirstOrDefault(i => i.LinkedId is null && !OutfitPlanner.IsWorn(model, i.Id)
                        && i.Kind is ItemKind.Notecard or ItemKind.Texture or ItemKind.Landmark or ItemKind.Script or ItemKind.Object);
                    if (candidate is null)
                        continue;
                    var to = top.Where(f => f.Id != from.Id && rules.WhyNotMove(candidate.Id, f.Id) is null)
                        .OrderBy(f => model.LoadState(f.Id) == FolderLoad.NotLoaded ? 0 : 1).FirstOrDefault();
                    if (to is not null)
                        return new { item = candidate.Id.ToString(), home = from.Id.ToString(), target = to.Id.ToString(), target_closed = !model.WasLoaded(to.Id), kind = candidate.Kind.ToString() };
                }
                return new { refused = "no_item" };
            }
            case "drop":
            {
                if (model.Item(item) is null || model.Folder(folder) is null)
                    return new { refused = "unknown" };
                panel.Drop(item, folder);
                bool shown = model.Item(item)?.ParentId == folder;
                return new { shown_at_once = shown, parent = model.Item(item)?.ParentId.ToString(), notice = panel.NoticeLabel.Text.Length > 0 && !shown };
            }
            case "where":
            {
                // the folder read from the grid in this session: does it hold the item?
                bool loaded = await Loaded(folder);
                return new { loaded, holds = model.ChildItems(folder).Any(i => i.Id == item), parent = model.Item(item)?.ParentId.ToString() };
            }
        }
        return new { refused = "unknown_action" };
    }

    /// <summary>
    /// The inventory check for a grid without the inventory API (development builds only), in world, by real mouse and key events sent
    /// through the engine's input path as a person's arrive: the inventory opened with its key, every folder opened by a double-click
    /// on its name, a wearable worn and taken off with the window's buttons, a new folder made with its button, an item dragged into
    /// it, the folder renamed by typing, the look saved as an outfit in the outfit window and that outfit worn, then everything put
    /// back (the item dragged home, the made folders moved to the Trash with the window's own confirmation; nothing is purged).
    /// Each result is read back from the grid (the folder read again) before it counts. Answers flags, counts and reasons only: the
    /// names it types are the caller's own test names.
    /// </summary>
    private Task<object>? _noAisRun;

    private async Task<object> NoAisCheck(InventoryPanel panel, string testName)
    {
        if (_inventoryFor is not { } session || panel.Model is not { } model)
            return new { refused = "not_in_world" };
        var tree = panel.TreeView;
        var steps = new List<object>();
        bool allOk = true;
        void Step(string name, bool ok, object detail) { steps.Add(new { name, ok, detail }); allOk &= ok; }

        async Task Frames(int n) { for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        async Task Wait(double s) { await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout); await Frames(2); }
        async Task<bool> Until(Func<bool> test, double seconds) { for (double t = 0; t < seconds; t += 0.25) { if (test()) return true; await Wait(0.25); } return test(); }
        Vector2 ToWindow(Vector2 viewport)
        {
            var win = GetWindow().Size; var vis = GetViewport().GetVisibleRect().Size;
            return new Vector2(viewport.X * win.X / vis.X, viewport.Y * win.Y / vis.Y);
        }
        Vector2 last = Vector2.Zero;
        void Move(Vector2 at, bool leftHeld = false)
        {
            var w = ToWindow(at);
            Input.ParseInputEvent(new InputEventMouseMotion { Position = w, GlobalPosition = w, Relative = w - last, ButtonMask = leftHeld ? MouseButtonMask.Left : 0 });
            last = w;
        }
        void Press(Vector2 at, bool down, bool doubleClick = false)
        {
            var w = ToWindow(at);
            Input.ParseInputEvent(new InputEventMouseButton { Position = w, GlobalPosition = w, ButtonIndex = MouseButton.Left, Pressed = down, DoubleClick = doubleClick, ButtonMask = down ? MouseButtonMask.Left : 0 });
            last = w;
        }
        async Task Click(Vector2 at) { Move(at); await Frames(1); Press(at, true); await Frames(1); Press(at, false); await Wait(0.4); }
        async Task DoubleClick(Vector2 at) { await Click(at); Press(at, true, doubleClick: true); await Frames(1); Press(at, false); await Wait(0.4); }
        void KeyTap(Key k, bool ctrl = false, char unicode = '\0')
        {
            Input.ParseInputEvent(new InputEventKey { Keycode = k, PhysicalKeycode = k, Unicode = unicode, CtrlPressed = ctrl, Pressed = true });
            Input.ParseInputEvent(new InputEventKey { Keycode = k, PhysicalKeycode = k, Unicode = unicode, CtrlPressed = ctrl, Pressed = false });
        }
        async Task Type(string text)
        {
            foreach (char ch in text)
            {
                Key k = ch == ' ' ? Key.Space : char.IsDigit(ch) ? Key.Key0 + (ch - '0') : Key.A + (char.ToUpperInvariant(ch) - 'A');
                KeyTap(k, unicode: ch);
                await Frames(1);
            }
        }
        async Task<Vector2?> RowAt(Guid id)
        {
            for (int i = 0; i < 20; i++)
            {
                if (tree.PointOf(id) is { } p) { await Frames(2); return tree.PointOf(id) ?? p; }
                await Wait(0.25);
            }
            return null;
        }
        Vector2? ControlAt(Node root, string name)
        {
            if (root.FindChildren(name, "", true, false).OfType<Control>().FirstOrDefault(c => c.IsVisibleInTree() && !(c is BaseButton { Disabled: true })) is not { } c)
                return null;
            var g = c.GetGlobalRect().GetCenter();
            if (c.GetWindow() is { } win && win != GetWindow())
                g += new Vector2(win.Position.X, win.Position.Y);
            return g;
        }
        async Task<bool> ClickControl(Node root, string name)
        {
            for (int i = 0; i < 12; i++)
            {
                if (ControlAt(root, name) is { } at) { await Click(at); return true; }
                await Wait(0.25);
            }
            return false;
        }
        // the grid's own answer: the folder is read again and the test looks at what came back
        async Task<bool> Reread(Guid folder)
        {
            model.Invalidate(folder);
            model.EnsureLoaded(folder);
            return await Until(() => model.LoadState(folder) == FolderLoad.Loaded, 20);
        }
        bool IsTrash(Guid id) => model.FolderOfKind(FolderKind.Trash) is var t && t != Guid.Empty && (id == t || model.IsDescendantOf(id, t));
        Guid Cof() => model.FolderOfKind(FolderKind.CurrentOutfit);
        HashSet<Guid> WornTargets() => model.ChildItems(Cof()).Where(i => i.LinkedId is { } t && t != Guid.Empty).Select(i => i.LinkedId!.Value).ToHashSet();
        async Task<OutfitCommandFinished?> OutfitAfter(OutfitCommandFinished? before, double seconds)
        {
            await Until(() => !ReferenceEquals(session.LastOutfitResult, before) && session.LastOutfitResult is not null, seconds);
            return ReferenceEquals(session.LastOutfitResult, before) ? null : session.LastOutfitResult;
        }

        try
        {
            // 0. the region's capabilities: a region with the inventory API is not the case this check is for
            bool ais = session.RegionHas("InventoryAPIv3");
            Step("capabilities", !ais, new { inventory_api = ais, library_api = session.RegionHas("LibraryAPIv3"), fetch = session.RegionHas("FetchInventoryDescendents2") });

            // 1. the inventory opens with its key
            var floaters = _shell!.Shell.Floaters;
            if (!panel.IsVisibleInTree())
            {
                KeyTap(Key.I, ctrl: true);
                await Wait(0.6);
            }
            Step("open_with_key", panel.IsVisibleInTree(), new { shown = panel.IsVisibleInTree() });
            if (!panel.IsVisibleInTree())
                return new { ok = false, steps };
            panel.Search.Clear();

            // 2. every folder opened by a double-click on its name, the agent's and the library's, until none is left closed
            var opened = new HashSet<Guid>();
            for (int pass = 0; pass < 40; pass++)
            {
                Guid next = Guid.Empty;
                for (var row = tree.GetRoot(); row is not null; row = row.GetNextVisible())
                {
                    var id = InventoryTree.IdOf(row);
                    if (model.Folder(id) is not null && row.Collapsed && !opened.Contains(id) && !IsTrash(id))
                    { next = id; break; }
                }
                if (next == Guid.Empty)
                    break;
                opened.Add(next);
                if (await RowAt(next) is { } at)
                {
                    await DoubleClick(at);
                    await Until(() => model.LoadState(next) == FolderLoad.Loaded, 15);
                }
            }
            var mine = model.AllFolders().Where(f => !IsTrash(f.Id)).ToList();
            int notLoaded = mine.Count(f => model.LoadState(f.Id) != FolderLoad.Loaded);
            Step("open_every_folder", notLoaded == 0 && opened.Count > 0, new { folders = mine.Count, opened_by_double_click = opened.Count, not_loaded = notLoaded });
            var wornAtStart = WornTargets();

            // 3. a wearable worn with the Wear button, then taken off with Take Off (a library one is copied into the inventory first)
            var wearable = model.AllItems().Where(i => i.LinkedId is null && i.Kind == ItemKind.Clothing && !OutfitPlanner.IsWorn(model, i.Id) && !IsTrash(i.ParentId))
                .OrderBy(i => model.IsInLibrary(i.Id) ? 1 : 0).FirstOrDefault();
            Guid wornItem = Guid.Empty;
            if (wearable is null)
                Step("wear", false, new { reason = "no_clothing_item" });
            else
            {
                var before = session.LastOutfitResult;
                bool clicked = await RowAt(wearable.Id) is { } at && await ClickThen(at, "wear");
                var done = await OutfitAfter(before, 60);
                await Reread(Cof());
                var nowWorn = WornTargets();
                wornItem = nowWorn.Except(wornAtStart).FirstOrDefault();
                Step("wear", clicked && done is { Ok: true } && wornItem != Guid.Empty, new { clicked, ok = done?.Ok, reason = done?.Reason, from_library = model.IsInLibrary(wearable.Id), cof_links = nowWorn.Count });
                if (wornItem != Guid.Empty)
                {
                    before = session.LastOutfitResult;
                    bool offClicked = await RowAt(wornItem) is { } wat && await ClickThen(wat, "take_off");
                    done = await OutfitAfter(before, 60);
                    await Reread(Cof());
                    Step("take_off", offClicked && done is { Ok: true } && !WornTargets().Contains(wornItem), new { clicked = offClicked, ok = done?.Ok, reason = done?.Reason });
                }
            }

            // 4. a new folder under My Inventory with the New Folder button, read back from the grid
            var root = model.RootId;
            var foldersBefore = model.ChildFolders(root).Select(f => f.Id).ToHashSet();
            bool made = await RowAt(root) is { } rootAt && await ClickThen(rootAt, "new_folder");
            await Until(() => model.ChildFolders(root).Any(f => !foldersBefore.Contains(f.Id)), 25);
            Guid testFolder = model.ChildFolders(root).Select(f => f.Id).FirstOrDefault(f => !foldersBefore.Contains(f));
            bool onGrid = testFolder != Guid.Empty && await Reread(root) && model.Folder(testFolder) is not null;
            Step("create_folder", made && onGrid, new { clicked = made, on_grid = onGrid });

            // 5. an item dragged into it (the item the wear step used, else any loose item), read back from the grid
            var moving = (wornItem != Guid.Empty ? model.Item(wornItem) : null)
                ?? model.AllItems().FirstOrDefault(i => i.LinkedId is null && !model.IsInLibrary(i.Id) && !IsTrash(i.ParentId) && i.ParentId != Cof() && !OutfitPlanner.IsWorn(model, i.Id));
            Guid home = moving?.ParentId ?? Guid.Empty;
            bool movedOnGrid = false;
            (bool Started, bool Outlined) drag = (false, false);
            if (testFolder != Guid.Empty && moving is not null && await RowAt(moving.Id) is { } from && await RowAt(testFolder) is { } to)
            {
                drag = await Drag(moving.Id, from, testFolder, to);
                await Until(() => model.Item(moving.Id)?.ParentId == testFolder, 10);
                movedOnGrid = await Reread(testFolder) && model.ChildItems(testFolder).Any(i => i.Id == moving.Id);
            }
            Step("move_item", movedOnGrid && drag.Started, new { has_item = moving is not null, drag_started = drag.Started, drop_outline = drag.Outlined, on_grid = movedOnGrid });

            // 6. the folder renamed by typing in place, read back from the grid
            bool renamed = false;
            if (testFolder != Guid.Empty && await RowAt(testFolder) is { } tat && await ClickThen(tat, "rename"))
            {
                await Wait(0.3);
                KeyTap(Key.A, ctrl: true);
                await Type(testName);
                KeyTap(Key.Enter);
                await Wait(0.5);
                await Until(() => model.Folder(testFolder)?.Name == testName, 10);
                renamed = await Reread(root) && model.Folder(testFolder)?.Name == testName;
            }
            Step("rename_folder", renamed, new { on_grid = renamed });

            // 7. the look saved as an outfit in the outfit window (name typed, Save clicked), then that outfit worn from the inventory
            var outfits = model.FolderOfKind(FolderKind.MyOutfits);
            var outfitsBefore = outfits == Guid.Empty ? new HashSet<Guid>() : model.ChildFolders(outfits).Select(f => f.Id).ToHashSet();
            Guid savedOutfit = Guid.Empty;
            {
                var before = session.LastOutfitResult;
                bool windowOpened = await ClickControl(_shell!, "toolbar_outfits");
                if (!windowOpened)
                    floaters.Open(OutfitFeature.FloaterId);   // no toolbar button shown: said in the answer
                await Wait(0.5);
                bool typed = false, saved = false;
                if (_outfitPanel is { } op && ControlAt(op, "outfit_name") is { } nameAt)
                {
                    await Click(nameAt);
                    KeyTap(Key.A, ctrl: true);
                    await Type(testName + " Look");
                    typed = op.NameField.Text == testName + " Look";
                    saved = await ClickControl(op, "save_outfit");
                }
                var done = await OutfitAfter(before, 90);
                if (outfits != Guid.Empty && await Reread(outfits))
                    savedOutfit = model.ChildFolders(outfits).Select(f => f.Id).FirstOrDefault(f => !outfitsBefore.Contains(f));
                floaters.Close(OutfitFeature.FloaterId);   // the outfit window would cover the inventory's rows
                await Wait(0.3);
                Step("save_outfit", typed && saved && done is { Save: true, Ok: true } && savedOutfit != Guid.Empty,
                    new { outfit_window_by_click = windowOpened, typed, clicked = saved, ok = done?.Ok, reason = done?.Reason, on_grid = savedOutfit != Guid.Empty });
            }
            if (savedOutfit != Guid.Empty)
            {
                var before = session.LastOutfitResult;
                if (model.Folder(outfits) is not null && await RowAt(outfits) is { } oat && RowCollapsed(outfits))
                    await DoubleClick(oat);
                // a worn piece of clothing taken off first (Take Off), so the saved outfit differs from what is worn and has something to put back
                bool tookOff = false;
                var piece = WornTargets().Select(id => model.Item(id)).FirstOrDefault(i => i is { Kind: ItemKind.Clothing });
                if (piece is not null)
                {
                    var b0 = session.LastOutfitResult;
                    if (await RowAt(piece.Id) is { } pat && await ClickThen(pat, "take_off"))
                        tookOff = await OutfitAfter(b0, 60) is { Ok: true };
                    before = session.LastOutfitResult;
                }
                // the saved outfit's folder opened (its links read from the grid), then chosen and worn with the Wear button
                if (await RowAt(savedOutfit) is { } sat0 && RowCollapsed(savedOutfit))
                    await DoubleClick(sat0);
                await Until(() => model.LoadState(savedOutfit) == FolderLoad.Loaded, 15);
                bool clicked = await RowAt(savedOutfit) is { } sat && await ClickThen(sat, "wear");
                var done = await OutfitAfter(before, 60);
                await Reread(Cof());
                bool putBack = piece is null || WornTargets().Contains(piece.Id);
                Step("wear_outfit", tookOff && clicked && done is { Save: false, Ok: true } && putBack, new { took_a_piece_off = tookOff, clicked, ok = done?.Ok, reason = done?.Reason, piece_back_on = putBack });
            }

            // 8. back as it was: the item dragged home, the made folders to the Trash (the window's own question answered Yes), and the
            //    worn set as it was at the start (anything this check put on taken off). Nothing is purged.
            bool itemHome = true;
            if (moving is not null && home != Guid.Empty && model.Item(moving.Id)?.ParentId != home)
            {
                itemHome = false;
                if (testFolder != Guid.Empty && await RowAt(testFolder) is { } tf2 && RowCollapsed(testFolder))
                    await DoubleClick(tf2);
                if (await RowAt(moving.Id) is { } from2 && await RowAt(home) is { } to2)
                {
                    await Drag(moving.Id, from2, home, to2);
                    await Until(() => model.Item(moving.Id)?.ParentId == home, 10);
                    itemHome = await Reread(home) && model.ChildItems(home).Any(i => i.Id == moving.Id);
                }
            }
            int rescued = 0;
            var trash = model.FolderOfKind(FolderKind.Trash);
            if (trash != Guid.Empty && await Reread(trash))
                foreach (var old in model.ChildFolders(trash).Where(f => f.Name == testName).ToList())
                {
                    await Reread(old.Id);
                    foreach (var it in model.ChildItems(old.Id).Where(i => i.LinkedId is null).ToList())
                    {
                        var kindFolder = model.FolderOfKind(it.Kind switch { ItemKind.Clothing => FolderKind.Clothing, ItemKind.Bodypart => FolderKind.Bodypart, ItemKind.Object => FolderKind.Object, _ => FolderKind.None });
                        if (kindFolder != Guid.Empty) { panel.Drop(it.Id, kindFolder); rescued++; }
                    }
                }
            int trashed = 0;
            // folders an earlier, unfinished run of this check left (its test names) go too
            var leftovers = model.ChildFolders(root).Where(f => f.Name == testName && f.Id != testFolder).Select(f => f.Id)
                .Concat(outfits == Guid.Empty ? Enumerable.Empty<Guid>() : model.ChildFolders(outfits).Where(f => f.Name == testName + " Look" && f.Id != savedOutfit).Select(f => f.Id)).ToList();
            foreach (var f in new[] { testFolder, savedOutfit }.Where(f => f != Guid.Empty).Concat(leftovers))
            {
                if (await RowAt(f) is { } fat && await ClickThen(fat, "trash") && await ClickControl(panel, "confirm_yes"))
                    if (await Until(() => IsTrash(f), 15))
                        trashed++;
            }
            foreach (var extra in WornTargets().Except(wornAtStart).ToList())
            {
                var before = session.LastOutfitResult;
                if (model.Item(extra) is not null && await RowAt(extra) is { } eat && await ClickThen(eat, "take_off"))
                    await OutfitAfter(before, 60);
            }
            await Reread(Cof());
            bool wornAsBefore = WornTargets().SetEquals(wornAtStart);
            Step("put_back", itemHome && trashed == new[] { testFolder, savedOutfit }.Count(f => f != Guid.Empty) + leftovers.Count && wornAsBefore,
                new { item_home = itemHome, folders_to_trash = trashed, earlier_leftovers = leftovers.Count, earlier_items_back_from_trash = rescued, worn_as_at_start = wornAsBefore });
        }
        catch (Exception ex)
        {
            Step("error", false, new { error = ex.GetType().Name });
        }
        return new { ok = allOk, steps };

        bool RowCollapsed(Guid id)
        {
            for (var row = tree.GetRoot(); row is not null; row = row.GetNextInTree())
                if (InventoryTree.IdOf(row) == id)
                    return row.Collapsed;
            return false;
        }
        // a click on the row selects it; then the window's button (found by its name) is clicked
        async Task<bool> ClickThen(Vector2 row, string button)
        {
            await Click(row);
            return await ClickControl(panel, button);
        }
        // a drag with the button held: pressed on the item's row, moved until the engine drags, then over the target and held there.
        // The engine places an injected pointer elsewhere while it drags, so (as the pointer check does) the tree is told where the
        // pointer is held, and the drop is handed on as the tree's own drop hands it on. Answers whether the drag started and the
        // target showed the drop outline.
        async Task<(bool Started, bool Outlined)> Drag(Guid item, Vector2 from, Guid target, Vector2 to)
        {
            Move(from);
            await Frames(1);
            Press(from, true);
            await Frames(1);
            for (int i = 1; i <= 6; i++) { Move(from + new Vector2(i * 4, i * 4), leftHeld: true); await Frames(1); }
            bool started = GetViewport().GuiIsDragging();
            Move(to, leftHeld: true);
            tree.HoldDragAt(tree.GetGlobalTransformWithCanvas().AffineInverse() * to);
            bool outlined = false;
            var held = Time.GetTicksMsec();
            while (Time.GetTicksMsec() - held < 1200) { Move(to + new Vector2(Time.GetTicksMsec() % 2 * 2, 0), leftHeld: true); await Frames(1); outlined |= tree.Cue.Target == target; }
            tree.HoldDragAt(null);
            GetViewport().GuiCancelDrag();
            Press(to, false);
            await Wait(0.3);
            panel.Drop(item, target);
            await Wait(0.4);
            return (started, outlined);
        }
    }
}
#endif
