// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Addons;

namespace Viewer.Shell.Addons;

/// <summary>
/// An add-on's inventory writer, kept inside the one folder its manifest names ("inventory_folder", a path of folder names
/// from the inventory's top). The folders of the path may be made, one after another; every other change must fall inside
/// the folder (the folder itself is never renamed or moved to the Trash). Whether something is inside is read from the
/// inventory as it is now, by walking down from the folder. A link may point at any of the user's items: making it changes
/// nothing of the item.
/// </summary>
internal sealed class AddonInventoryScope(IAddonInventory inventory, IAddonInventoryWriter writer, IReadOnlyList<string> path) : IAddonInventoryWriter
{
    /// <summary>The folders of the path that exist now, from the inventory's top down (the top itself first).</summary>
    private List<Guid> PathFolders()
    {
        var found = new List<Guid> { inventory.RootId };
        foreach (string name in path)
        {
            Guid next = inventory.Folders(found[^1]).FirstOrDefault(f => f.Name == name).Id;
            if (next == Guid.Empty)
                break;
            found.Add(next);
        }
        return found;
    }

    /// <summary>The add-on's folder, or empty while its path is not all there.</summary>
    private Guid Scope()
    {
        var folders = PathFolders();
        return folders.Count == path.Count + 1 ? folders[^1] : Guid.Empty;
    }

    /// <summary>The folders inside the add-on's folder, itself included.</summary>
    private HashSet<Guid> Inside()
    {
        var inside = new HashSet<Guid>();
        Guid scope = Scope();
        if (scope == Guid.Empty)
            return inside;
        var stack = new Stack<Guid>();
        stack.Push(scope);
        while (stack.Count > 0)
        {
            Guid at = stack.Pop();
            if (!inside.Add(at))
                continue;
            foreach (var f in inventory.Folders(at))
                stack.Push(f.Id);
        }
        return inside;
    }

    public Task<Guid> CreateFolderAsync(Guid parent, string name)
    {
        if (Inside().Contains(parent))
            return writer.CreateFolderAsync(parent, name);
        // the next folder of the path, under the last one there
        var folders = PathFolders();
        if (folders.Count <= path.Count && folders[^1] == parent && path[folders.Count - 1] == name)
            return writer.CreateFolderAsync(parent, name);
        return Task.FromResult(Guid.Empty);
    }

    public Task<bool> LinkAsync(Guid folder, Guid target, string description) =>
        Inside().Contains(folder) ? writer.LinkAsync(folder, target, description) : Task.FromResult(false);

    public Task<bool> RenameFolderAsync(Guid folder, string name)
    {
        var inside = Inside();
        return inside.Contains(folder) && folder != Scope() ? writer.RenameFolderAsync(folder, name) : Task.FromResult(false);
    }

    public Task<bool> MoveToTrashAsync(Guid id, bool isFolder)
    {
        var inside = Inside();
        bool allowed = isFolder
            ? inside.Contains(id) && id != Scope()
            : inside.Any(f => inventory.Items(f).Any(i => i.Id == id));
        return allowed ? writer.MoveToTrashAsync(id, isFolder) : Task.FromResult(false);
    }
}
