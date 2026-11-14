// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Interaction;

namespace Viewer.Core.Building;

/// <summary>Where a new object goes: the ray the region casts to find the spot, and what it should hit.</summary>
/// <param name="BypassRaycast">True when the end is the spot itself (the ground was hit); false when the region casts the ray to find it.</param>
public readonly record struct Placement(Vector3 RayStart, Vector3 RayEnd, Guid RayTargetId, bool BypassRaycast);

/// <summary>The default object the create tool makes (lltoolplacer.cpp, read as behaviour): a half-metre box of wood.</summary>
public static class DefaultBox
{
    public const byte PCodeVolume = 9, MaterialWood = 3, ProfileSquare = 1, PathLine = 16;

    /// <summary>The object is made selected (the protocol's create-selected flag).</summary>
    public const uint AddFlagCreateSelected = 1u << 1;

    public static readonly Vector3 Scale = new(0.5f, 0.5f, 0.5f);

    /// <summary>The path scale as the message packs it: 200 minus the ratio in hundredths, so 100 for a ratio of 1.</summary>
    public const byte PathScalePacked = 100;
}

/// <summary>What the menus and tools may do with an object or an item, written from the official viewer's enable rules (llviewermenu.cpp
/// enable_take, llselectmgr.cpp selectDelete; behaviour only).</summary>
public static class BuildRules
{
    /// <summary>The farthest a click may place an object from the camera, as the official create tool limits it (MaxSelectDistance 128, less one).</summary>
    public const float MaxPlaceDistance = 127f;

    /// <summary>The protocol's derez destinations used here.</summary>
    public const byte DeRezTakeIntoInventory = 4, DeRezTrash = 6;

    /// <summary>Take: an object on the ground that the agent owns, or one that may be modified and transferred; not one worn or sat on.</summary>
    public static bool CanTake(uint flags, bool attachment, bool sittingOnIt) =>
        !attachment && !sittingOnIt &&
        ((flags & ObjectFlagBits.YouOwner) != 0 || (flags & (ObjectFlagBits.Modify | ObjectFlagBits.Transfer)) == (ObjectFlagBits.Modify | ObjectFlagBits.Transfer));

    /// <summary>Delete: an object on the ground that the agent owns or may modify.</summary>
    public static bool CanDelete(uint flags, bool attachment) =>
        !attachment && (flags & (ObjectFlagBits.YouOwner | ObjectFlagBits.Modify)) != 0;

    /// <summary>The question Delete asks first (a notification's name), or null when it deletes at once: only for an object that is locked,
    /// that cannot be copied, or that the agent does not own.</summary>
    public static string? DeleteConfirmation(bool locked, bool copyable, bool owned) =>
        !locked && copyable && owned ? null
            : "ConfirmObjectDelete" + (locked ? "Lock" : "") + (copyable ? "" : "NoCopy") + (owned ? "" : "NoOwn");

    /// <summary>Where Create puts the box for a click: from the camera through the click; on the ground the spot itself, on an object the region
    /// casts the ray on past it. Null for a click on nothing, an avatar, or too far away.</summary>
    public static Placement? CreatePlacement(PickResult pick, Vector3 cameraPosition, Vector3 cameraAt, Vector3 rayDirection, float nearClip)
    {
        if (pick.Kind is not (PickKind.Land or PickKind.Object) || Vector3.Distance(cameraPosition, pick.Point) > MaxPlaceDistance)
            return null;
        var start = cameraPosition + cameraAt * (nearClip + 0.01f);
        if (pick.Kind == PickKind.Land)
            return new Placement(start, pick.Point, Guid.Empty, true);
        var dir = rayDirection.LengthSquared() > 0 ? Vector3.Normalize(rayDirection) : cameraAt;
        return new Placement(start, cameraPosition + dir * (1f + MaxPlaceDistance), pick.Hit?.FullId ?? Guid.Empty, false);
    }

    /// <summary>Where a dropped inventory object is rezzed: from the camera to the spot; on an object, the ray goes on as far again past it.</summary>
    public static Placement? RezPlacement(PickResult pick, Vector3 cameraPosition) => pick.Kind switch
    {
        PickKind.Land => new Placement(cameraPosition, pick.Point, Guid.Empty, true),
        PickKind.Object => new Placement(cameraPosition, pick.Point - (cameraPosition - pick.Point), pick.Hit?.FullId ?? Guid.Empty, false),
        _ => null,
    };

    /// <summary>Whether a dropped item leaves the inventory when it is rezzed: one that cannot be copied, or one in the Trash.</summary>
    public static bool RezRemovesItem(bool itemCopyable, bool inTrash) => !itemCopyable || inTrash;
}
