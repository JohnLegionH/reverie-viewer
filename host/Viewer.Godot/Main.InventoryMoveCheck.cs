// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

#if NV_AUTOMATION
using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Actions;
using Viewer.Core.Inventory;
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
}
#endif
