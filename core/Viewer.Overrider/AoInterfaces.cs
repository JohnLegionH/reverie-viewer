// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Overrider;

/// <summary>One inventory folder as the overrider reads it.</summary>
public readonly record struct AoFolder(Guid Id, string Name);

/// <summary>One inventory item as the overrider reads it.</summary>
/// <param name="Description">The item's own description (on a link in a set it holds the sort order).</param>
/// <param name="IsLink">Whether the item is a link to another item.</param>
/// <param name="IsAnimation">Whether the item, or the item a link points to, is an animation.</param>
/// <param name="LinkedId">For a link, the item it points to.</param>
/// <param name="AssetId">The animation's asset; empty while the item a link points to is not known.</param>
public readonly record struct AoItem(Guid Id, string Name, string Description, bool IsLink, bool IsAnimation, Guid LinkedId, Guid AssetId);

/// <summary>The user's inventory as the overrider needs it. Read only: the overrider never changes the inventory.</summary>
public interface IAoInventory
{
    /// <summary>The inventory's root folder; empty before the inventory is known.</summary>
    Guid RootId { get; }

    /// <summary>Whether the folder's contents are known.</summary>
    bool IsComplete(Guid folder);

    /// <summary>Asks for the folder's contents.</summary>
    void Fetch(Guid folder);

    IReadOnlyList<AoFolder> Folders(Guid folder);

    IReadOnlyList<AoItem> Items(Guid folder);

    /// <summary>The asset of the animation a link points to, as known now; empty while it is not known.</summary>
    Guid AssetOf(Guid linkItemId);
}

/// <summary>The user's own avatar as the overrider needs it.</summary>
public interface IAoAgent
{
    /// <summary>Asks the region to start or stop an animation on the user's avatar.</summary>
    void SendAnimation(Guid animation, bool start);

    /// <summary>Whether the avatar sits on an object (not on the ground, not standing).</summary>
    bool SittingOnObject { get; }

    /// <summary>Whether the object the avatar sits on plays an animation of its own on the avatar.</summary>
    bool ForeignAnimationOnSeat { get; }

    /// <summary>Whether the region now lists the animation as playing on the avatar.</summary>
    bool IsSignalled(Guid animation);
}
