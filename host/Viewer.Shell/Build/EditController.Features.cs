// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Building;
using Viewer.Core.Interaction;
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
    private (Guid ObjectId, IReadOnlyList<TaskItem> Items)? _contents;
    private readonly Queue<(uint Local, Guid ItemId, bool Script)> _drops = new();
    private readonly Queue<(uint Local, Guid Folder)> _takes = new();

    /// <summary>What the Features tab shows for a prim (the primary one by default); null with nothing selected.</summary>
    public ObjectFeatures? Features(uint? local = null)
    {
        if (Object(local) is not { } o)
            return null;
        LightParams? light = o.Light is { } l ? new LightParams(l.Color, l.Intensity, l.Radius, l.Falloff) : null;
        ProjectorParams? projector = o.Projector is { } p ? new ProjectorParams(p.Texture, p.Fov, p.Focus, p.Ambiance) : null;
        return new ObjectFeatures(o.Flexible, light, projector, _physics.TryGetValue(o.FullId, out var physics) ? physics : PhysicsParams.Default, o.Material,
            FeatureRules.CanFlex(o.Shape), ((o.Interaction?.Flags ?? 0) & ObjectFlagBits.Physics) != 0);
    }

    /// <summary>Flexible Path on, with <paramref name="settings"/> (the prim's own or the defaults when null), or off, for the primary prim
    /// (the official tab edits the one object it shows). Turning it on or off also sends the shape with the flexible or straight path, as
    /// the official viewer's setIsFlexible does.</summary>
    public bool SetFlexible(bool on, FlexibleParams? settings = null)
    {
        if (Reshapable() is not { } id || Find(id) is not { } o || !FeatureRules.CanFlex(o.Shape))
            return false;
        int wanted = on ? FeatureRules.PathFlexible : FeatureRules.PathLine;
        if (o.Shape is { } shape && (shape.PathCurve & 0xF0) != wanted)
            Send(new SetObjectShapeIntent(id, ShapeRules.Encode(shape with { PathCurve = wanted })));
        return Send(new SetFlexibleIntent(id, on ? FeatureRules.Clamp(settings ?? o.Flexible ?? FlexibleParams.Default) : null));
    }

    /// <summary>Light on, with its settings and projector (the prim's own or the defaults when null), or off, for the primary prim.</summary>
    public bool SetLight(bool on, LightParams? light = null, ProjectorParams? projector = null)
    {
        if (Reshapable() is not { } id || Find(id) is not { } o)
            return false;
        if (!on)
            return Send(new SetLightIntent(id, null, null));
        var current = Features(id);
        var l = FeatureRules.Clamp(light ?? current?.Light ?? LightParams.Default);
        ProjectorParams? p = (projector ?? current?.Projector) is { } pr ? FeatureRules.Clamp(pr) : null;
        return Send(new SetLightIntent(id, l, p));
    }

    /// <summary>The prims a selection-wide change goes to: every selected prim, and with Edit Linked off every prim of each selected linkset
    /// (the official selection holds the whole family of a selected root); only those that may be modified.</summary>
    private List<uint> ModifiablePrims()
    {
        var prims = new List<uint>();
        foreach (var l in _selection)
        {
            if (!prims.Contains(l))
                prims.Add(l);
            if (!_editLinked)
                foreach (var child in _world.Snapshot().Objects.Values.Where(o => o.ParentLocalId == l).Select(o => o.Id.LocalId).OrderBy(x => x))
                    if (!prims.Contains(child))
                        prims.Add(child);
        }
        return prims.Where(l => Access(l).CanModify).ToList();
    }

    /// <summary>The physics shape type and values for every selected prim that may be modified, each with its own flags.</summary>
    public bool SetPhysics(PhysicsParams physics)
    {
        var p = FeatureRules.Clamp(physics);
        bool any = false;
        foreach (var l in ModifiablePrims())
        {
            uint flags = Find(RootOf(l))?.Interaction?.Flags ?? 0;
            any |= Send(new SetPhysicsIntent(l, p, (flags & ObjectFlagBits.Physics) != 0, false, false));
            if (Find(l) is { } o)
                _physics[o.FullId] = p;
        }
        return any;
    }

    /// <summary>The material (Stone 0 to Rubber 6) for every selected prim that may be modified.</summary>
    public bool SetMaterial(int material)
    {
        if (material is < 0 or > 6)
            return false;
        bool any = false;
        foreach (var l in ModifiablePrims())
            any |= Send(new SetObjectMaterialIntent(l, (byte)material));
        return any;
    }

    /// <summary>Asks the region for the selected prims' physics settings.</summary>
    public bool RequestPhysics()
    {
        var ids = _selection.Select(Find).Where(o => o is not null).Select(o => o!.FullId).Distinct().ToArray();
        return ids.Length > 0 && Send(new RequestPhysicsIntent(ids));
    }

    // ---------------- the Content tab ----------------

    /// <summary>The primary prim's items, sorted by name as the official list sorts them; empty until the region has answered.</summary>
    public IReadOnlyList<TaskItem> Contents => ContentsKnown ? _contents!.Value.Items : [];

    /// <summary>Whether the region has answered for the primary prim.</summary>
    public bool ContentsKnown => _contents is { } c && Object() is { } o && o.FullId == c.ObjectId;

    /// <summary>Asks the region for the primary prim's items.</summary>
    public bool RequestContents() => Object() is { } o && Send(new RequestContentsIntent(o.Id.LocalId, o.FullId));

    /// <summary>
    /// An item of the shown object was saved in place (a notecard edited from Contents): its listed asset becomes the one the save stored and
    /// the object's contents are read again, as the official window takes the new asset id from the upload's answer and reads the card
    /// again (llpreviewnotecard.cpp finishTaskUpload). Opening the item again then reads what was saved, not the asset listed before.
    /// False when the item is not listed for that object.
    /// </summary>
    public bool ItemSaved(Guid objectId, Guid itemId, Guid newAssetId)
    {
        if (_contents is not { } c || c.ObjectId != objectId || !c.Items.Any(i => i.ItemId == itemId))
            return false;
        if (newAssetId != Guid.Empty)
            _contents = (c.ObjectId, c.Items.Select(i => i.ItemId == itemId ? i with { AssetId = newAssetId } : i).ToArray());
        Changed?.Invoke();
        RequestContents();
        return true;
    }

    /// <summary>The primary prim when its contents may be changed: one the agent may modify.</summary>
    private Viewer.Core.World.ObjectState? ContentTarget() => Primary is { } p && Access(p).CanModify ? Find(p) : null;

    /// <summary>New Script: the region writes its default script into the primary prim; the list is asked for again.</summary>
    public bool NewScript(string name)
    {
        if (ContentTarget() is not { } o || !Send(new NewScriptIntent(o.Id.LocalId, o.FullId, name)))
            return false;
        RequestContents();
        return true;
    }

    /// <summary>An inventory item dropped on the Content tab goes into the primary prim (a script as a script); the list is asked for again.</summary>
    public bool DropItem(Guid itemId, bool script)
    {
        if (ContentTarget() is not { } o || itemId == Guid.Empty || !Send(new GiveItemToObjectIntent(o.Id.LocalId, itemId, script)))
            return false;
        RequestContents();
        return true;
    }

    /// <summary>A folder dropped on the Content tab: its items wait in a queue and go into the primary prim one at a time (<see cref="PumpQueues"/>),
    /// as Firestorm's Z-drop does. The number queued.</summary>
    public int DropFolder(IEnumerable<(Guid ItemId, bool Script)> items)
    {
        if (ContentTarget() is not { } o)
            return 0;
        int n = 0;
        foreach (var (item, script) in items)
        {
            if (item == Guid.Empty)
                continue;
            _drops.Enqueue((o.Id.LocalId, item, script));
            n++;
        }
        return n;
    }

    private TaskItem? Listed(Guid itemId) => Contents.FirstOrDefault(i => i.ItemId == itemId);

    /// <summary>Open on a listed item of the primary prim, for the script editor: the script and the prim it is in, or null when the item is not
    /// a listed script (llpanelobjectinventory.cpp opens a script's live editor; other kinds are not opened from here yet).</summary>
    public Viewer.Core.Inventory.ScriptAccess? ScriptToOpen(Guid itemId, Guid agent) =>
        Object() is { } o && Listed(itemId) is { } item ? Viewer.Shell.Inventory.ScriptEditing.AccessFor(item, o.FullId, o.Id.LocalId, agent) : null;

    /// <summary>Deletes a listed item from the primary prim; the list is asked for again.</summary>
    public bool DeleteItem(Guid itemId)
    {
        if (ContentTarget() is not { } o || Listed(itemId) is null || !Send(new RemoveObjectItemIntent(o.Id.LocalId, itemId)))
            return false;
        RequestContents();
        return true;
    }

    /// <summary>Copies a listed item out of the primary prim into the inventory folder for its type.</summary>
    public bool CopyItemToInventory(Guid itemId) =>
        Object() is { } o && Listed(itemId) is { } item && Send(new CopyObjectItemIntent(o.Id.LocalId, itemId, item.AssetType));

    /// <summary>Whether a listed item of the primary prim may be dragged out (startDrag), the attachment counted from the prim's root.</summary>
    public bool CanDragOut(Guid itemId, Guid agent)
    {
        if (Object() is not { } o || Listed(itemId) is not { } item)
            return false;
        bool youOwn = ((o.Interaction?.Flags ?? 0) & ObjectFlagBits.YouOwner) != 0;
        bool attachment = o.AttachmentPoint is not null || Find(RootOf(o.Id.LocalId))?.AttachmentPoint is not null;
        return TaskItemRules.CanDragOut(item, agent, youOwn, attachment);
    }

    /// <summary>A listed item of the primary prim, or null.</summary>
    public TaskItem? ContentItem(Guid itemId) => Listed(itemId);

    /// <summary>An item dragged out of the primary prim and dropped on an inventory folder: copied (or moved) into that folder, when the
    /// official drag rules let it leave (llpanelobjectinventory.cpp LLTaskInvFVBridge::startDrag).</summary>
    public bool CopyItemToFolder(Guid itemId, Guid folder, Guid agent)
    {
        if (Object() is not { } o || Listed(itemId) is not { } item || folder == Guid.Empty || !CanDragOut(itemId, agent))
            return false;
        return Send(new CopyObjectItemIntent(o.Id.LocalId, itemId, item.AssetType, folder));
    }

    /// <summary>Rename or a new description for a listed item of the primary prim, when the item may be modified and so may the prim
    /// (LLTaskInvFVBridge::renameItem; the region's UpdateTaskInventory needs the prim's contents to be editable). Null leaves a field as is.</summary>
    public bool UpdateItemText(Guid itemId, string? name, string? description, Guid agent)
    {
        if (ContentTarget() is not { } o || Listed(itemId) is not { } item || !TaskItemRules.CanRename(item, agent))
            return false;
        string newName = name is null ? item.Name : Cut(name.Trim(), TaskItemRules.MaxName);
        string newDescription = description is null ? item.Description : Cut(description, TaskItemRules.MaxDescription);
        if (newName.Length == 0)
            return false;
        if (newName == item.Name && newDescription == item.Description)
            return true;
        if (!Send(new UpdateObjectItemIntent(o.Id.LocalId, o.FullId, item with { Name = newName, Description = newDescription })))
            return false;
        RequestContents();
        return true;
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];

    /// <summary>Whether the primary prim's contents may be changed (New Script, Delete, rename, items dropped in).</summary>
    public bool ContentsEditable => ContentTarget() is not null;

    /// <summary>The prims Build › Scripts works on: every chosen prim (and with Edit Linked off every prim of each chosen linkset) that
    /// has a script and may be modified, as the official queue_actions takes them; the refusal when none does.</summary>
    public (IReadOnlyList<ScriptQueueObject> Objects, ScriptQueueRefusal? Refusal) ScriptQueueObjects()
    {
        var prims = new List<uint>();
        foreach (var l in _selection)
        {
            if (!prims.Contains(l))
                prims.Add(l);
            if (!_editLinked)
                foreach (var child in _world.Snapshot().Objects.Values.Where(o => o.ParentLocalId == l).Select(o => o.Id.LocalId).OrderBy(x => x))
                    if (!prims.Contains(child))
                        prims.Add(child);
        }
        var objects = new List<ScriptQueueObject>();
        bool anyScripted = false;
        foreach (var l in prims)
        {
            if (Find(l) is not { } o)
                continue;
            bool scripted = ((o.Interaction?.Flags ?? 0) & ObjectFlagBits.Scripted) != 0;
            anyScripted |= scripted;
            if (scripted && Access(l).CanModify)
                objects.Add(new ScriptQueueObject(l, o.FullId, PropertiesOf(l)?.Name ?? ""));
        }
        if (objects.Count > 0)
            return (objects, null);
        return ([], anyScripted ? ScriptQueueRefusal.NoPermission : ScriptQueueRefusal.NoScripts);
    }

    // ---------------- selection helpers ----------------

    /// <summary>Select Only My Objects (SelectOwnedOnly, off by default): a click or drag-select passes over objects the agent does not own.</summary>
    public bool SelectOwnedOnly { get; set; }

    /// <summary>Whether Select Only My Objects lets this object be chosen (its root's owner flag decides).</summary>
    private bool Choosable(uint local) => !SelectOwnedOnly || ((Find(RootOf(local))?.Interaction?.Flags ?? 0) & ObjectFlagBits.YouOwner) != 0;

    /// <summary>Select Next Part and Select Previous Part are on with something selected and Edit Linked on.</summary>
    public bool CanSelectNextPart => BuildMode && _editLinked && _selection.Count > 0;

    /// <summary>Ctrl+. and Ctrl+,: the next or previous part of the selection's linkset, in link order (the root first, then its parts), going
    /// round. With one part selected the next one in that order is chosen; with several, the first selected in link order going forward or the
    /// last going back (llviewermenu.cpp LLToolsSelectNextPartFace). This viewer orders the parts by their local ids, the order the region
    /// gave them; the official viewer uses its own child list, which it also notes may differ from the region's link order.</summary>
    public bool SelectNextPart(bool forward)
    {
        if (!CanSelectNextPart || Primary is not { } p || RootOf(p) is var root && root == 0)
            return false;
        var parts = new List<uint> { root };
        parts.AddRange(_world.Snapshot().Objects.Values.Where(o => o.ParentLocalId == root).Select(o => o.Id.LocalId).OrderBy(x => x));
        var selected = parts.Where(_selection.Contains).ToList();
        if (selected.Count == 0)
            return false;
        uint next;
        if (_selection.Count > 1)
            next = forward ? selected[0] : selected[^1];
        else
        {
            int at = parts.IndexOf(selected[0]);
            next = parts[(at + (forward ? 1 : parts.Count - 1)) % parts.Count];
        }
        SendDeselect(_selection.Where(s => s != next));
        bool had = _selection.Contains(next);
        _selection.Clear();
        _faceSelection.Clear();
        if (had)
            _selection.Add(next);
        else
            Add(next);
        Changed?.Invoke();
        return true;
    }

    // ---------------- take into a folder ----------------

    /// <summary>Starts taking the selected objects (their roots) into <paramref name="folder"/>, one at a time (<see cref="PumpQueues"/>), as
    /// Firestorm's Z-take does (one DeRezObject per object, taking into the agent's inventory with that folder). The number queued; objects that
    /// may not be taken (worn, sat on, or neither owned nor modifiable and transferable) are left selected.</summary>
    public int TakeIntoFolder(Guid folder)
    {
        if (folder == Guid.Empty)
            return 0;
        int n = 0;
        foreach (var root in _selection.Select(RootOf).Where(r => r != 0).Distinct().ToList())
        {
            if (Find(root) is not { } o || !BuildRules.CanTake(o.Interaction?.Flags ?? 0, o.AttachmentPoint is not null, false) || _takes.Any(t => t.Local == root))
                continue;
            _takes.Enqueue((root, folder));
            n++;
        }
        return n;
    }

    /// <summary>How many folder items and takes still wait.</summary>
    public int Pending => _drops.Count + _takes.Count;

    /// <summary>Sends the next waiting folder item and the next waiting take (the host calls this on a timer). True when anything was sent.</summary>
    public bool PumpQueues()
    {
        bool any = false;
        if (_drops.TryDequeue(out var drop))
        {
            any |= Send(new GiveItemToObjectIntent(drop.Local, drop.ItemId, drop.Script));
            if (_drops.Count == 0 && Object() is { } o && o.Id.LocalId == drop.Local)
                RequestContents();
        }
        if (_takes.TryDequeue(out var take))
        {
            any |= Send(new TakeIntoFolderIntent(take.Local, take.Folder));
            // the taken object leaves the selection, as it leaves the world
            if (_selection.RemoveAll(l => RootOf(l) == take.Local || l == take.Local) > 0)
                Changed?.Invoke();
        }
        return any;
    }

    /// <summary>The Features and Content tabs' events: the region's physics settings and a prim's items.</summary>
    private void ReceiveFeatures(TransportEvent e)
    {
        switch (e)
        {
            case ObjectPhysicsReceived p:
                _physics[p.ObjectId] = p.Physics;
                if (Object() is { } o && o.FullId == p.ObjectId)
                    Changed?.Invoke();
                break;
            // an answer with nothing in it is an empty list: a prim whose last item was deleted has no contents file, and its read ends empty
            case TaskInventoryReceived t when (t.Ok || t.Items.Count == 0) && Object() is { } shown && shown.FullId == t.ObjectId:
                _contents = (t.ObjectId, t.Items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Name, StringComparer.Ordinal).ToArray());
                Changed?.Invoke();
                break;
        }
    }
}
