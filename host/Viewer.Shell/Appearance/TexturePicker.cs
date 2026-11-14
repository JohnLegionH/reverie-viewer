// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Inventory;

namespace Viewer.Shell.Appearance;

/// <summary>One line of the texture picker: an inventory texture.</summary>
public sealed record PickRow(Guid ItemId, string Name, bool InLibrary);

/// <summary>What the texture picker lists: the account's inventory textures (and the Library's), by name, narrowed by a search and by <c>usable</c> (the grid refuses a wearable that uses a texture the agent lacks any right to, so the window lists only textures it has every right to).</summary>
public static class TexturePicker
{
    /// <summary>The most rows the list holds; a longer list is cut and the search narrows it.</summary>
    public const int MaxRows = 500;

    /// <summary>Asks for the folders that hold textures when they are not here yet. True when they are all loaded.</summary>
    public static bool Prepare(InventoryModel model)
    {
        bool ready = true;
        foreach (var f in model.AllFolders())
        {
            if (f.Kind != FolderKind.Texture || model.LoadState(f.Id) == FolderLoad.Loaded)
                continue;
            ready = false;
            if (!model.EnsureLoaded(f.Id))
                model.Retry(f.Id);
        }
        return ready;
    }

    public static IReadOnlyList<PickRow> Rows(InventoryModel model, string search, Func<Guid, bool>? usable = null)
    {
        string needle = (search ?? "").Trim();
        var trash = model.FolderOfKind(FolderKind.Trash);
        return model.AllItems()
            .Where(i => i.Kind == ItemKind.Texture && (needle.Length == 0 || i.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                && !(trash != Guid.Empty && model.IsDescendantOf(i.Id, trash)) && (usable is null || usable(i.Id)))
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxRows)
            .Select(i => new PickRow(i.Id, i.Name, model.IsInLibrary(i.ParentId)))
            .ToList();
    }
}
