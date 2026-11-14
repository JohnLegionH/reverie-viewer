// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Building;
using Viewer.Core.Interaction;
using Viewer.Core.Meshing;
using Viewer.Core.Session;
using Viewer.Core.World;

namespace Viewer.Shell.Build;

/// <summary>The build tools as the official viewer names them (lltoolmgr.cpp, menu_viewer.xml: Ctrl+1 to Ctrl+5).</summary>
public enum BuildTool { Focus, Move, Edit, Create, Land }

/// <summary>
/// Build mode, its tools and the edit selection, and every edit sent from it: names, descriptions, permissions, Locked, place, rotation
/// and size by number, flags, shape, Link, Unlink and the Shift-drag copy. Behaviour follows the official viewer's LLToolMgr,
/// LLSelectMgr and LLPanelObject (read as behaviour only). No engine types: the Godot side draws the window and the handles from it.
/// </summary>
public sealed class EditController
{
    private readonly WorldState _world;
    private readonly Func<ActionIntent, ActionResult> _submit;
    private readonly Func<EditLimits> _limits;
    private readonly Func<float, float, float>? _ground;
    private readonly List<uint> _selection = new();
    private readonly Dictionary<Guid, ObjectPropertiesReceived> _properties = new();
    private bool _editLinked;

    /// <param name="ground">The ground's height at a point of the region, when known (a typed height may go down to the ground less half the
    /// object's diagonal).</param>
    public EditController(WorldState world, Func<ActionIntent, ActionResult> submit, Func<EditLimits>? limits = null, Func<float, float, float>? ground = null)
    {
        _world = world;
        _submit = submit;
        _limits = limits ?? (() => EditLimits.Official);
        _ground = ground;
    }

    public event Action? Changed;

    public bool BuildMode { get; private set; }
    public BuildTool Tool { get; private set; } = BuildTool.Create;

    /// <summary>"Edit linked": a click selects one part of a linkset instead of the whole (EditLinkedParts). Turning it off promotes the
    /// selection to the roots.</summary>
    public bool EditLinked
    {
        get => _editLinked;
        set
        {
            if (_editLinked == value)
                return;
            _editLinked = value;
            if (!value)
            {
                var roots = _selection.Select(RootOf).Where(r => r != 0).Distinct().ToList();
                _selection.Clear();
                _selection.AddRange(roots);
            }
            Changed?.Invoke();
        }
    }

    /// <summary>Snap to grid (SnapEnabled, on by default; G toggles it).</summary>
    public bool SnapEnabled { get; set; } = true;
    /// <summary>The grid unit in metres (GridResolution).</summary>
    public float GridUnit { get; set; } = Snap.DefaultGridUnit;
    /// <summary>"Stretch Both Sides" (ScaleUniform, off by default).</summary>
    public bool StretchBothSides { get; set; }
    /// <summary>"Stretch Textures" (ScaleStretchTextures, on by default).</summary>
    public bool StretchTextures { get; set; } = true;

    /// <summary>The selection in the order it was made, the latest last.</summary>
    public IReadOnlyList<uint> Selection => _selection.ToArray();

    /// <summary>The object the window shows: the one selected last.</summary>
    public uint? Primary => _selection.Count == 0 ? null : _selection[^1];

    /// <summary>The limits in force (the official ones with the region's own).</summary>
    public EditLimits Limits => _limits();

    /// <summary>Ctrl+B: opens build mode with the Create tool, or closes it.</summary>
    public void ToggleBuild()
    {
        if (BuildMode)
        {
            CloseBuild();
            return;
        }
        BuildMode = true;
        Tool = BuildTool.Create;
        Changed?.Invoke();
    }

    /// <summary>Esc or closing the window: build mode ends, Edit linked goes off, nothing stays selected.</summary>
    public void CloseBuild()
    {
        BuildMode = false;
        _editLinked = false;
        _selection.Clear();
        Changed?.Invoke();
    }

    /// <summary>Choose a tool (Ctrl+1 to Ctrl+5); build mode opens with it.</summary>
    public void SelectTool(BuildTool tool)
    {
        BuildMode = true;
        Tool = tool;
        Changed?.Invoke();
    }

    private bool Selecting => BuildMode && Tool is BuildTool.Edit or BuildTool.Move;

    /// <summary>A click with the Edit or Move tool: selects the object's root (or the part, with Edit linked); Shift adds or removes; a click on
    /// nothing clears. True when the click was the edit tools'.</summary>
    public bool Click(PickResult pick, bool shift)
    {
        if (!Selecting)
            return false;
        if (pick.Kind != PickKind.Object || pick.Hit is not { } hit || _world.Object(hit.Id) is not { } prim || prim.AttachmentPoint is not null)
        {
            if (!shift)
                Deselect();
            return true;
        }
        uint id = _editLinked ? prim.Id.LocalId : RootOf(prim.Id.LocalId);
        if (shift)
        {
            if (!_selection.Remove(id))
                Add(id);
        }
        else
        {
            _selection.Clear();
            Add(id);
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>A drag-select: the objects inside the box (their roots, or the parts with Edit linked); Shift adds to the selection.</summary>
    public void SelectMany(IEnumerable<uint> locals, bool shift)
    {
        if (!shift)
            _selection.Clear();
        foreach (var l in locals)
        {
            uint id = _editLinked ? l : RootOf(l);
            if (id != 0 && !_selection.Contains(id))
                Add(id);
        }
        Changed?.Invoke();
    }

    public void Deselect()
    {
        _selection.Clear();
        Changed?.Invoke();
    }

    private void Add(uint id)
    {
        _selection.Add(id);
        // the region answers a selection with the object's properties (owner, creator, permissions)
        _submit(new ObjectPropertiesIntent(id));
    }

    /// <summary>What may be done with a selected object.</summary>
    public EditAccess Access(uint local)
    {
        if (Find(local) is not { } o)
            return new(false, false, false, false);
        var root = Find(RootOf(local)) ?? o;
        uint? mask = _properties.TryGetValue(root.FullId, out var p) ? p.OwnerMask : null;
        return EditAccess.Of(root.Interaction?.Flags ?? 0, mask);
    }

    /// <summary>The object's properties, once the region has sent them for the selection.</summary>
    public ObjectPropertiesReceived? PropertiesOf(uint local) => Find(local) is { } o && _properties.TryGetValue(o.FullId, out var p) ? p : null;

    public void Receive(TransportEvent e)
    {
        if (e is ObjectPropertiesReceived p)
        {
            _properties[p.ObjectId] = p;
            if (_selection.Any(l => Find(l)?.FullId == p.ObjectId))
                Changed?.Invoke();
        }
    }

    /// <summary>A selected object as the world holds it (the primary one by default).</summary>
    public ObjectState? Object(uint? local = null) => (local ?? Primary) is { } l ? Find(l) : null;

    // ---------------- edits ----------------

    private bool Send(ActionIntent i) => _submit(i).Status == ActionStatus.Sent;

    private uint? Placeable() => Primary is { } p && Access(p).CanPlace ? p : null;
    private uint? Reshapable() => Primary is { } p && Access(p).CanReshape ? p : null;

    public bool SetName(string name) =>
        _selection.Count == 1 && Reshapable() is { } id && Send(new SetObjectNameIntent(id, name.Length > EditText.MaxName ? name[..EditText.MaxName] : name));

    public bool SetDescription(string description) =>
        _selection.Count == 1 && Reshapable() is { } id
        && Send(new SetObjectDescriptionIntent(id, description.Length > EditText.MaxDescription ? description[..EditText.MaxDescription] : description));

    public bool SetPosition(Vector3 position)
    {
        if (Placeable() is not { } id || Find(id) is not { } o)
            return false;
        var p = ClampPosition(o, position);
        return Send(new UpdateObjectIntent(id, p, null, null, Linkset(o)));
    }

    public bool SetRotation(Vector3 degrees)
    {
        if (Placeable() is not { } id || Find(id) is not { } o)
            return false;
        return Send(new UpdateObjectIntent(id, o.Position, EditNumbers.FromEulerDegrees(degrees), null, Linkset(o)));
    }

    public bool SetSize(Vector3 size)
    {
        if (Reshapable() is not { } id || Find(id) is not { } o)
            return false;
        bool physical = ((o.Interaction?.Flags ?? 0) & ObjectFlagBits.Physics) != 0;
        return Send(new UpdateObjectIntent(id, o.Position, null, EditNumbers.ClampScale(size, physical, _limits()), Linkset(o)));
    }

    public bool SetFlags(bool physical, bool temporary, bool phantom) =>
        Reshapable() is { } id && Send(new SetObjectFlagsIntent(RootOf(id), physical, temporary, phantom));

    public bool SetLocked(bool locked)
    {
        var roots = _selection.Select(RootOf).Distinct().Where(r => Access(r).Owned).ToArray();
        return roots.Length > 0 && Send(new SetObjectLockedIntent(roots, locked));
    }

    public bool SetPermission(byte field, uint mask, bool on)
    {
        var roots = _selection.Select(RootOf).Distinct().Where(r => Access(r).Owned).ToArray();
        return roots.Length > 0 && Send(new SetObjectPermissionsIntent(roots, field, on, mask));
    }

    public bool SetShape(PrimShape shape) =>
        Reshapable() is { } id && Find(id) is { Shape: var current } && (current?.SculptType ?? 0) == 0
        && Send(new SetObjectShapeIntent(id, ShapeRules.Encode(ShapeRules.Clamp(shape))));

    /// <summary>Ctrl+L: links the selection, the last selected becoming the root. Null when sent, else why not.</summary>
    public string? Link()
    {
        if (_editLinked)
            return "link.edit_linked";
        var roots = _selection.Select(RootOf).Distinct().ToList();
        var refusal = LinkRules.Refusal(roots.Select(r => (Find(r)?.Interaction?.Flags ?? 0, PrimsOf(r))).ToArray(), _limits());
        if (refusal is not null)
            return refusal;
        return Send(new LinkObjectsIntent(LinkRules.Order(roots))) ? null : "link.not_sent";
    }

    /// <summary>Ctrl+Shift+L: unlinks every prim of the selected linksets.</summary>
    public bool Unlink()
    {
        var roots = _selection.Select(RootOf).Distinct().Where(r => Access(r).CanModify).ToHashSet();
        if (roots.Count == 0)
            return false;
        var prims = _world.Snapshot().Objects.Values.Where(o => roots.Contains(o.Id.LocalId) || roots.Contains(o.ParentLocalId))
            .Select(o => o.Id.LocalId).ToArray();
        return prims.Length > 1 && Send(new DelinkObjectsIntent(prims));
    }

    /// <summary>The start of a Shift-drag with the Move tool: the selection is copied in place and the copy stays behind while the
    /// originals move on with the drag. Every root must be copyable and not worn.</summary>
    public bool CopyInPlace()
    {
        var roots = _selection.Select(RootOf).Distinct().ToArray();
        if (roots.Length == 0 || roots.Any(r => Find(r) is not { AttachmentPoint: null } o || ((o.Interaction?.Flags ?? 0) & ObjectFlagBits.Copy) == 0))
            return false;
        return Send(new DuplicateObjectsIntent(roots, Vector3.Zero, 0));
    }

    // ---------------- the world ----------------

    private ObjectState? Find(uint local) => _world.Snapshot().Objects.Values.FirstOrDefault(o => o.Id.LocalId == local);

    private uint RootOf(uint local)
    {
        var o = Find(local);
        for (int depth = 0; depth < 16 && o is { ParentLocalId: not 0 } && Find(o.ParentLocalId) is { } parent; depth++)
            o = parent;
        return o?.Id.LocalId ?? 0;
    }

    private int PrimsOf(uint root) => _world.Snapshot().Objects.Values.Count(o => o.Id.LocalId == root || o.ParentLocalId == root);

    /// <summary>A root moves its linkset; a part chosen with Edit linked moves alone.</summary>
    private static bool Linkset(ObjectState o) => o.ParentLocalId == 0;

    private Vector3 ClampPosition(ObjectState o, Vector3 p)
    {
        if (o.ParentLocalId != 0)
            return p;   // a part's place is relative to its root
        var region = _world.Region;
        float w = region?.SizeX ?? 256, h = region?.SizeY ?? 256;
        float ground = _ground?.Invoke(Math.Clamp(p.X, 0, w), Math.Clamp(p.Y, 0, h)) ?? float.NegativeInfinity;
        return EditNumbers.ClampPosition(p, w, h, ground, o.Scale.Length() / 2f, _limits());
    }
}
