// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Linq;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot.Presentation;

/// <summary>
/// Every engine resource the viewer makes directly (meshes, instances, lights, skeletons) is created and freed here, so
/// <see cref="Ledger"/> knows what is live. The engine corrupts its heap when it quits while resources we made are
/// still live; a test or S-1 run lists them just before quitting and fails with that list
/// (<see cref="CheckAtQuit"/>), and the shipping viewer frees any stragglers at quit instead of crashing.
/// Main thread only, like the engine calls it wraps.
/// </summary>
internal static class Rids
{
    public static readonly LiveRidLedger Ledger = new();

    // the resource values by id, so a straggler can be freed at quit (the engine's id alone cannot be turned back into one)
    private static readonly System.Collections.Generic.Dictionary<ulong, Rid> Made = new();

    public static Rid MeshCreate(string owner)
    {
        var rid = RenderingServer.MeshCreate();
        Record(rid, owner);
        return rid;
    }

    public static Rid InstanceCreate2(Rid mesh, Rid scenario, string owner)
    {
        var rid = RenderingServer.InstanceCreate2(mesh, scenario);
        Record(rid, owner);
        return rid;
    }

    public static Rid LightCreate(bool spot, string owner)
    {
        var rid = spot ? RenderingServer.SpotLightCreate() : RenderingServer.OmniLightCreate();
        Record(rid, owner);
        return rid;
    }

    public static Rid SkeletonCreate(string owner)
    {
        var rid = RenderingServer.SkeletonCreate();
        Record(rid, owner);
        return rid;
    }

    private static void Record(Rid rid, string owner)
    {
        Ledger.Created(rid.Id, owner);
        Made[rid.Id] = rid;
    }

    public static void Free(Rid rid)
    {
        RenderingServer.FreeRid(rid);
        Ledger.Freed(rid.Id);
        Made.Remove(rid.Id);
    }

    /// <summary>What is still live, as one line ("none live" when nothing is).</summary>
    public static string Summary() => Ledger.Summary();

    /// <summary>
    /// The check at quit: returns what is still live (empty when nothing is), then frees it, instances before
    /// meshes and skeletons (the order the layers use), so the engine's own quit does not meet it.
    /// </summary>
    public static string CheckAtQuit(out int leaked)
    {
        string summary = Ledger.Summary();
        var live = Ledger.Live();
        leaked = live.Count;
        // the engine's ids grow with creation order and an instance is made after its mesh, so newest first frees instances first
        foreach (var e in live.OrderByDescending(e => e.Id))
        {
            if (Made.TryGetValue(e.Id, out var rid) && rid.IsValid) Free(rid);
            else Ledger.Freed(e.Id);
        }
        return summary;
    }
}
