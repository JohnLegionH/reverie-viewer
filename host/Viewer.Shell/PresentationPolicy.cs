// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.World;

namespace Viewer.Shell;

/// <summary>
/// How the presentation shares each frame between a region's changes. Terrain patches are not made to wait behind the region's objects
/// (a region with a hundred thousand objects would show no land until all of them were taken in), and the deferred work (building
/// terrain chunks, presenting objects) keeps a share of every frame whatever the backlog of changes is.
/// The official viewer unpacks received land patches every frame apart from its object messages (llappviewer.cpp, llvlmanager.cpp unpackData)
/// and gives geometry its own time budget (llviewerdisplay.cpp, pipeline.cpp createObjects and updateGeom).
/// </summary>
public static class PresentationPolicy
{
    /// <summary>The share of a frame's time the queued changes may take before the deferred work gets its turn.</summary>
    public const double ChangesShare = 0.5;

    /// <summary>Changes the prompt lane takes: a terrain patch, and a region reset (which also clears the terrain).</summary>
    public static bool IsPrompt(WorldChange change) => change.Kind is WorldChangeKind.TerrainPatch or WorldChangeKind.RegionReset;

    /// <summary>Prompt-lane changes that also wait their turn in the queue: a reset clears the objects and avatars there, in order.</summary>
    public static bool AlsoQueued(WorldChange change) => change.Kind != WorldChangeKind.TerrainPatch;

    /// <summary>Changes taken at once whatever waits ahead of them: an avatar's arrival, look and movement, and an object it wears (the wearer is
    /// waiting to see it; <paramref name="isWorn"/> says whether the object of a change hangs on an avatar). </summary>
    public static bool IsUrgent(WorldChange change, bool hasPrims, Func<ObjectId, bool> isWorn) =>
        change.Kind is WorldChangeKind.AvatarAdded or WorldChangeKind.AvatarChanged or WorldChangeKind.AvatarMoved or WorldChangeKind.AvatarRemoved
            or WorldChangeKind.AppearanceChanged or WorldChangeKind.AnimationsChanged
        || (hasPrims && change.Kind is WorldChangeKind.ObjectAdded or WorldChangeKind.ObjectChanged && isWorn(change.Id));
    /// <summary>Applies one urgent change: an object change goes to the prims and is also noted by the avatars as a change of what is worn
    /// (<paramref name="wornOnly"/>: it marks the wearer, it does not apply the change to the avatar's own state, which the queue does);
    /// every other urgent change goes to the avatars.</summary>
    public static void ApplyUrgent(WorldChange change, Action<WorldChange> prims, Action<WorldChange> avatars, Action<WorldChange> wornOnly)
    {
        if (change.Kind is WorldChangeKind.ObjectAdded or WorldChangeKind.ObjectChanged) { prims(change); wornOnly(change); }
        else avatars(change);
    }

    /// <summary>The changes that present what the world already holds when the presentation attaches.</summary>
    public static IEnumerable<WorldChange> CatchUp(WorldSnapshot world) =>
        world.Avatars.Keys.Order().Select(local => new WorldChange(WorldChangeKind.AvatarAdded, new ObjectId(world.Epoch, local)));

    /// <summary>Sets the lanes on <paramref name="coordinator"/>; the caller sets what applies a prompt change.</summary>
    public static void Configure(PresentationCoordinator coordinator)
    {
        coordinator.IsPrompt = IsPrompt;
        coordinator.PromptAlsoQueued = AlsoQueued;
        coordinator.ApplyShare = ChangesShare;
    }
}
