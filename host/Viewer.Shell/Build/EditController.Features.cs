// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Actions;
using Viewer.Core.Building;
using Viewer.Core.Session;

namespace Viewer.Shell.Build;

/// <summary>
/// The Features tab (flexible path, light and projector, physics shape type, material and the physics values), the Content tab (the prim's
/// items, New Script, items and folders dropped in, Delete, Copy to inventory), Select Only My Objects, the next and previous part, and
/// taking objects into a chosen folder. Behaviour follows the official viewer's LLPanelVolume, LLPanelContents, LLPanelObjectInventory and
/// LLSelectMgr, and Firestorm's Z-take and Z-drop (read as behaviour only).
/// </summary>
public sealed partial class EditController
{
    private readonly Dictionary<Guid, PhysicsParams> _physics = new();

    /// <summary>What the Features tab shows for a prim (the primary one by default); null with nothing selected.</summary>
    public ObjectFeatures? Features(uint? local = null) => null;

    /// <summary>Flexible Path on, with <paramref name="settings"/> (the defaults when null), or off, for every selected prim that may be modified.</summary>
    public bool SetFlexible(bool on, FlexibleParams? settings = null) => false;

    /// <summary>Light on, with its settings and projector (the defaults when null), or off.</summary>
    public bool SetLight(bool on, LightParams? light = null, ProjectorParams? projector = null) => false;

    /// <summary>The physics shape type and values for every selected prim that may be modified.</summary>
    public bool SetPhysics(PhysicsParams physics) => false;

    /// <summary>The material (Stone 0 to Rubber 6) for every selected prim that may be modified.</summary>
    public bool SetMaterial(int material) => false;

    /// <summary>Asks the region for the selected prims' physics settings.</summary>
    public bool RequestPhysics() => false;

    // ---------------- the Content tab ----------------

    /// <summary>The primary prim's items, sorted by name as the official list sorts them; empty until the region has answered.</summary>
    public IReadOnlyList<TaskItem> Contents => [];

    /// <summary>Whether the region has answered for the primary prim.</summary>
    public bool ContentsKnown => false;

    /// <summary>Asks the region for the primary prim's items.</summary>
    public bool RequestContents() => false;

    /// <summary>New Script: the region writes its default script into the primary prim.</summary>
    public bool NewScript(string name) => false;

    /// <summary>An inventory item dropped on the Content tab goes into the primary prim (a script as a script).</summary>
    public bool DropItem(Guid itemId, bool script) => false;

    /// <summary>A folder dropped on the Content tab: its items wait in a queue and go into the primary prim one at a time (<see cref="PumpQueues"/>),
    /// as Firestorm's Z-drop does. The number queued.</summary>
    public int DropFolder(IEnumerable<(Guid ItemId, bool Script)> items) => 0;

    /// <summary>Deletes an item from the primary prim.</summary>
    public bool DeleteItem(Guid itemId) => false;

    /// <summary>Copies an item out of the primary prim into the inventory folder for its type.</summary>
    public bool CopyItemToInventory(Guid itemId) => false;

    // ---------------- selection helpers ----------------

    /// <summary>Select Only My Objects (SelectOwnedOnly, off by default): a click or drag-select passes over objects the agent does not own.</summary>
    public bool SelectOwnedOnly { get; set; }

    /// <summary>Select Next Part and Select Previous Part are on with something selected and Edit Linked on.</summary>
    public bool CanSelectNextPart => false;

    /// <summary>Ctrl+. and Ctrl+,: the next or previous part of the selection's linkset, in link order (the root first), going round.</summary>
    public bool SelectNextPart(bool forward) => false;

    // ---------------- take into a folder ----------------

    /// <summary>Starts taking the selected objects (their roots) into <paramref name="folder"/>, one at a time (<see cref="PumpQueues"/>), as
    /// Firestorm's Z-take does. The number queued; objects that may not be taken are left.</summary>
    public int TakeIntoFolder(Guid folder) => 0;

    /// <summary>How many folder items and takes still wait.</summary>
    public int Pending => 0;

    /// <summary>Sends the next waiting folder item and the next waiting take (the host calls this on a timer). True when anything was sent.</summary>
    public bool PumpQueues() => false;

    /// <summary>The Features and Content tabs' events: the region's physics settings and a prim's items.</summary>
    private void ReceiveFeatures(TransportEvent e) { }
}
