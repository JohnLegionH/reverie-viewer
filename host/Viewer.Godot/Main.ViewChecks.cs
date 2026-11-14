// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

#if NV_AUTOMATION
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Inventory;
using Viewer.Shell;
using Viewer.Shell.Inventory;

namespace Viewer.Godot;

public partial class Main
{
    /// <summary>The windows command (development builds only): every open window's id and rectangle, and a window opened, moved, resized
    /// or closed as the user's drags and its close button do. It answers ids and numbers, never text.</summary>
    public object? WindowsCall(string action, string? name, float x, float y, float w, float h)
    {
        if (_shell is null)
            return null;
        var floaters = _shell.Shell.Floaters;
        string? id = name is null ? null : floaters.Specs.FirstOrDefault(s => string.Equals(s.Id, name, StringComparison.OrdinalIgnoreCase))?.Id;
        bool done = action switch
        {
            "open" => id is not null && floaters.Open(id),
            "move" => id is not null && floaters.Move(id, x, y),
            "resize" => id is not null && floaters.Resize(id, w, h),
            "close" => id is not null && floaters.Close(id),
            _ => true,
        };
        if (action == "fit" && id is not null && _shell.Frames.TryGetValue(id, out var frame))
        {
            var needed = frame.NeededSize();
            var rect = floaters.RectOf(id);
            return new
            {
                id, problems = FitProblems(frame), rect = rect is { } at ? Rect(at) : null, needed = new[] { MathF.Round(needed.X), MathF.Round(needed.Y) },
                spec_default = floaters.SpecOf(id) is { } spec ? new[] { spec.DefaultSize.W, spec.DefaultSize.H } : null,
                official = OfficialFloaterSizes.Of(id) is { } o ? new[] { o.W, o.H } : null,
                first_open = floaters.SpecOf(id) is { } s2 ? new[] { floaters.FirstOpenSize(s2).W, floaters.FirstOpenSize(s2).H } : null,
                scrolls = rect is { } r && (r.W + 0.5f < needed.X || r.H + 0.5f < needed.Y),
            };
        }
        return new
        {
            done,
            bounds = Rect(floaters.Bounds),
            open = floaters.OpenFloaters.Select(f => new { id = f.Id, rect = Rect(f.Rect) }).ToArray(),
        };

        static float[] Rect(UiRect r) => [r.X, r.Y, r.W, r.H];
    }

    /// <summary>Every visible control in a window that is narrower than its content needs (its text is cut) or reaches past the window's
    /// right edge, and every two controls side by side in a row that overlap. Answers node names and numbers, never text.</summary>
    private static string[] FitProblems(Control window)
    {
        var problems = new List<string>();
        var edge = window.GetGlobalRect();
        void Walk(Control c)
        {
            if (!c.IsVisibleInTree())
                return;
            var r = c.GetGlobalRect();
            var min = c.GetCombinedMinimumSize();
            if (c is Button or Label or OptionButton or LineEdit && r.Size.X + 0.5f < min.X)
                problems.Add($"{c.Name}: {r.Size.X:0} wide, needs {min.X:0}");
            if (r.End.X > edge.End.X + 0.5f && c is not ScrollBar)
                problems.Add($"{c.Name}: right edge {r.End.X - edge.Position.X:0} past the window's {edge.Size.X:0}");
            if (c is HBoxContainer row)
            {
                var kids = row.GetChildren().OfType<Control>().Where(k => k.Visible).Select(k => k.GetGlobalRect()).ToList();
                for (int i = 1; i < kids.Count; i++)
                    if (kids[i].Position.X + 0.5f < kids[i - 1].End.X)
                        problems.Add($"{row.Name}: items {i - 1} and {i} overlap");
            }
            foreach (var child in c.GetChildren().OfType<Control>())
                Walk(child);
        }
        Walk(window);
        return problems.ToArray();
    }

    /// <summary>The inventory window's view (development builds only): the sort shown, a sort chosen as a click on the sort list does
    /// (so it is kept as the user's choice is), and an offline check of a hypergrid suitcase folder opened and closed with its arrow while
    /// the grid's answer for it arrives again and again. The window is made if no login has made it yet. Answers counts and flags only.</summary>
    public async Task<object?> InventoryViewCall(string action, string? sort)
    {
        if (_shell is null)
            return null;
        var panel = _inventoryPanel ??= InventoryFeature.Install(_shell, settings: UserSettings(null));
        switch (action)
        {
            case "sort":
            {
                var wanted = string.Equals(sort, "date", StringComparison.OrdinalIgnoreCase) ? InventorySort.ByDate : InventorySort.ByName;
                int index = panel.SortChoice.GetItemIndex((int)wanted);
                panel.SortChoice.Select(index);
                panel.SortChoice.EmitSignal(OptionButton.SignalName.ItemSelected, index);
                break;
            }
            case "suitcase":
                if (_inventoryFor is not null)
                    return new { refused = "in_world" };   // the check binds a made-up inventory: only before a login
                return await SuitcaseCheck(panel);
            case "pointer":
                return await PointerCheck(panel, live: false);
            case "pointer_live":
                if (_inventoryFor is null)
                    return new { refused = "not_in_world" };
                return await PointerCheck(panel, live: true);
            case "toggle_live":
                if (_inventoryFor is null || panel.Model is not { } live)
                    return new { refused = "not_in_world" };
                return await LiveToggleCheck(panel, live);
        }
        return new { sort = ((InventorySort)panel.SortChoice.GetSelectedId()).ToString(), stored = UserSettings(null).Get(Settings.InventorySortOrder) };
    }

    /// <summary>In world: the hypergrid suitcase folder under My Inventory (the Trash when the account has none) opened with its arrow,
    /// read again from the grid three times while open and three times while closed, and closed again. Reading only.</summary>
    private async Task<object> LiveToggleCheck(InventoryPanel panel, InventoryModel model)
    {
        var top = model.ChildFolders(model.RootId);
        var suitcase = top.FirstOrDefault(f => f.Name == "My Suitcase");
        Guid folder = suitcase?.Id ?? model.FolderOfKind(FolderKind.Trash);
        if (folder == Guid.Empty)
            return new { refused = "no_folder" };
        _shell!.Shell.Floaters.Open(InventoryFeature.FloaterId);
        async Task Frames(int n) { for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        async Task Wait(double s) { await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout); await Frames(2); }
        async Task Loaded() { for (int i = 0; i < 40 && model.LoadState(folder) != FolderLoad.Loaded; i++) await Wait(0.25); }
        TreeItem? Row(Guid id)
        {
            for (var item = panel.TreeView.GetRoot(); item is not null; item = item.GetNextInTree())
                if (InventoryTree.IdOf(item) == id)
                    return item;
            return null;
        }
        async Task<(int Draws, bool StayedPut)> Refresh(bool open)
        {
            int before = panel.TreeDraws;
            bool put = true;
            for (int i = 0; i < 3; i++)
            {
                model.Invalidate(folder);
                await Wait(0.3);
                put &= Row(folder) is { } r && r.Collapsed == !open && panel.View!.IsExpanded(folder) == open;
                await Loaded();
                await Wait(0.3);
                put &= Row(folder) is { } r2 && r2.Collapsed == !open && panel.View!.IsExpanded(folder) == open;
            }
            return (panel.TreeDraws - before, put);
        }
        await Wait(0.5);
        if (Row(folder) is not { } row)
            return new { refused = "row_not_drawn" };
        if (!row.Collapsed)
        {
            row.Collapsed = true;
            await Wait(0.4);
        }
        Row(folder)!.Collapsed = false;   // the arrow, as a click on it does
        await Loaded();
        await Wait(0.4);
        bool opened = Row(folder) is { Collapsed: false };
        var whileOpen = await Refresh(open: true);
        Row(folder)!.Collapsed = true;
        await Wait(0.4);
        bool closed = Row(folder) is { Collapsed: true } && !panel.View!.IsExpanded(folder);
        var whileClosed = await Refresh(open: false);
        return new
        {
            folder = suitcase is not null ? "suitcase" : "trash",
            opened, closed,
            draws_while_open = whileOpen.Draws, stayed_open = whileOpen.StayedPut,
            draws_while_closed = whileClosed.Draws, stayed_closed = whileClosed.StayedPut,
            own_outfit_folder_top_level = model.Folder(model.FolderOfKind(FolderKind.CurrentOutfit))?.ParentId == model.RootId,
        };
    }

    private async Task<object> SuitcaseCheck(InventoryPanel panel)
    {
        Guid owner = Guid.NewGuid(), root = Guid.NewGuid(), suitcase = Guid.NewGuid(), copyCof = Guid.NewGuid(), copyTrash = Guid.NewGuid();
        var asked = new List<Guid>();
        var model = new InventoryModel((folder, _) => { lock (asked) asked.Add(folder); });
        InventoryFolderData F(Guid id, Guid parent, string n, FolderKind kind) => new(id, parent, n, kind, 1, 0);
        model.Apply(new InventorySkeletonReceived(owner, root, Guid.Empty, Guid.Empty, new[]
        {
            F(root, Guid.Empty, "My Inventory", FolderKind.Root), F(suitcase, root, "My Suitcase", InventoryKinds.FolderKindOf(100)),
            F(copyCof, suitcase, "Current Outfit", FolderKind.CurrentOutfit), F(copyTrash, suitcase, "Trash", FolderKind.Trash),
        }, Array.Empty<InventoryFolderData>()));
        model.Apply(new InventoryFolderFetched(root, true, 2, 1, new[] { F(suitcase, root, "My Suitcase", InventoryKinds.FolderKindOf(100)) }, Array.Empty<InventoryItemData>()));
        var answer = new InventoryFolderFetched(suitcase, true, 3, 2,
            new[] { F(copyCof, suitcase, "Current Outfit", FolderKind.CurrentOutfit), F(copyTrash, suitcase, "Trash", FolderKind.Trash) }, Array.Empty<InventoryItemData>());
        _shell!.Shell.Floaters.Open(InventoryFeature.FloaterId);
        panel.Bind(model, null);
        async Task Frames(int n) { for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        async Task Settle() { await ToSignal(GetTree().CreateTimer(0.35), SceneTreeTimer.SignalName.Timeout); await Frames(2); }
        TreeItem? Row(Guid id)
        {
            for (var item = panel.TreeView.GetRoot(); item is not null; item = item.GetNextInTree())
                if (InventoryTree.IdOf(item) == id)
                    return item;
            return null;
        }
        async Task<(int Draws, bool StayedPut)> Refresh(int rounds, bool open)
        {
            int before = panel.TreeDraws;
            bool put = true;
            for (int i = 0; i < rounds; i++)
            {
                model.Invalidate(suitcase);
                model.Apply(answer);
                await Settle();
                put &= Row(suitcase) is { } r && r.Collapsed == !open && panel.View!.IsExpanded(suitcase) == open;
            }
            return (panel.TreeDraws - before, put);
        }
        try
        {
            await Settle();
            Row(suitcase)!.Collapsed = false;   // the arrow, as a click on it does
            await Frames(1);
            model.Apply(answer);
            await Settle();
            bool opened = Row(suitcase) is { Collapsed: false } && Row(copyCof) is not null;
            var whileOpen = await Refresh(8, open: true);
            int beforeClose = panel.TreeDraws;
            Row(suitcase)!.Collapsed = true;
            await Settle();
            bool closed = Row(suitcase) is { Collapsed: true } && Row(copyCof) is null;
            int closeDraws = panel.TreeDraws - beforeClose;
            var whileClosed = await Refresh(8, open: false);
            return new
            {
                opened, closed, close_draws = closeDraws,
                refresh_rounds = 8,
                draws_while_open = whileOpen.Draws, stayed_open = whileOpen.StayedPut,
                draws_while_closed = whileClosed.Draws, stayed_closed = whileClosed.StayedPut,
            };
        }
        finally
        {
            panel.Bind(null, null);
            _shell.Shell.Floaters.Close(InventoryFeature.FloaterId);
        }
    }
}
#endif
