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

/// <summary>Where the grid lies: the region's axes, the selected object's own, or a place and turn taken from the selection (Use Selection for
/// Grid, Shift+G).</summary>
public enum GridMode { World, Local, Reference }

/// <summary>The build tools as the official viewer names them (lltoolmgr.cpp, menu_viewer.xml: Ctrl+1 to Ctrl+5).</summary>
public enum BuildTool { Focus, Move, Edit, Create, Land }

/// <summary>
/// Build mode, its tools and the edit selection, and every edit sent from it: names, descriptions, permissions, Locked, place, rotation
/// and size by number, flags, shape, Link, Unlink and the Shift-drag copy. Behaviour follows the official viewer's LLToolMgr,
/// LLSelectMgr and LLPanelObject (read as behaviour only). No engine types: the Godot side draws the window and the handles from it.
/// </summary>
public sealed partial class EditController
{
    private readonly WorldState _world;
    private readonly Func<ActionIntent, ActionResult> _submit;
    private readonly Func<long> _clock;   // milliseconds; the handles' hold after a drag runs on it
    private readonly Func<EditLimits> _limits;
    private readonly Func<float, float, float>? _ground;
    private readonly Func<IReadOnlyList<RegionArea>>? _regions;
    private readonly List<uint> _selection = new();
    private readonly Dictionary<Guid, ObjectPropertiesReceived> _properties = new();
    private bool _editLinked;

    /// <param name="ground">The ground's height at a point of the region, when known (a typed height may go down to the ground less half the
    /// object's diagonal).</param>
    /// <param name="regions">The regions the viewer knows, relative to the agent's own (a typed position must lie in one); the agent's region
    /// alone when not given.</param>
    public EditController(WorldState world, Func<ActionIntent, ActionResult> submit, Func<EditLimits>? limits = null, Func<float, float, float>? ground = null,
        Func<IReadOnlyList<RegionArea>>? regions = null, Func<long>? clock = null)
    {
        _clock = clock ?? (() => Environment.TickCount64);
        _world = world;
        _submit = submit;
        _limits = limits ?? (() => EditLimits.Official);
        _ground = ground;
        _regions = regions;
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
                // the parts go and their roots are selected (llselectmgr.cpp promoteSelectionToRoot)
                var roots = _selection.Select(RootOf).Where(r => r != 0).Distinct().ToList();
                var before = _selection.ToList();
                SendDeselect(before.Except(roots));
                _selection.Clear();
                foreach (var r in roots)
                {
                    if (before.Contains(r))
                        _selection.Add(r);
                    else
                        Add(r);
                }
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
        SendDeselect(_selection);
        _selection.Clear();
        _faceSelection.Clear();
        Changed?.Invoke();
    }

    /// <summary>Whether the Land tool exists. It is not built (terraforming), so nothing selects it: the window has no segment for it and
    /// <see cref="SelectTool"/> and <see cref="TryParseTool"/> refuse it. The enum value, its hint and its strings stay for when it is built.</summary>
    public static bool LandToolBuilt => false;

    /// <summary>Reads a tool from text (the automation's "tool" action): the name of a tool that exists.</summary>
    public static bool TryParseTool(string? text, out BuildTool tool) =>
        Enum.TryParse(text, true, out tool) && Enum.IsDefined(tool) && (tool != BuildTool.Land || LandToolBuilt);

    /// <summary>Choose a tool (Ctrl+1 to Ctrl+5); build mode opens with it. A tool that is not built (Land) changes nothing.</summary>
    public void SelectTool(BuildTool tool)
    {
        if (tool == BuildTool.Land && !LandToolBuilt)
            return;   // not built: nothing changes, build mode stays as it was
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
        if (_selectFaces)
            return ClickFace(pick, shift);
        // Select Only My Objects: another's object is passed over as if the click had found nothing (llselectmgr.cpp, SelectOwnedOnly)
        if (pick.Kind != PickKind.Object || pick.Hit is not { } hit || _world.Object(hit.Id) is not { } prim || prim.AttachmentPoint is not null
            || !Choosable(prim.Id.LocalId))
        {
            if (!shift)
                Deselect();
            return true;
        }
        uint id = _editLinked ? prim.Id.LocalId : RootOf(prim.Id.LocalId);
        if (shift)
        {
            if (_selection.Remove(id))
                SendDeselect([id]);
            else
                Add(id);
        }
        else
        {
            SendDeselect(_selection.Where(s => s != id));
            _selection.Clear();
            Add(id);
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Right-click Edit: build mode opens with the Edit tool and only this object's linkset selected.</summary>
    public bool EditObject(uint local)
    {
        if (Find(local) is null)
            return false;   // a worn object is allowed: the right-click menu offers Edit only on the agent's own
        SelectTool(BuildTool.Edit);
        _faceSelection.Clear();
        // the root, whatever Edit linked says (the right-click Edit takes the whole linkset), and whatever Select Only My Objects says
        // (the official menu's Edit does not look at it); the select is sent even when the root was already in the selection, because
        // the right-click let go of it on the region just before
        uint root = RootOf(local);
        SendDeselect(_selection.Where(s => s != root));
        _selection.Clear();
        Add(root);
        Changed?.Invoke();
        return true;
    }

    /// <summary>A drag-select: the objects inside the box (their roots, or the parts with Edit linked); Shift adds to the selection.</summary>
    public void SelectMany(IEnumerable<uint> locals, bool shift)
    {
        var chosen = locals.Where(Choosable).ToList();
        if (!shift)
        {
            var kept = chosen.Select(l => _editLinked ? l : RootOf(l)).ToHashSet();
            SendDeselect(_selection.Where(s => !kept.Contains(s)));
            _selection.Clear();
            _faceSelection.Clear();
        }
        foreach (var l in chosen)
        {
            uint id = _editLinked ? l : RootOf(l);
            if (id != 0 && !_selection.Contains(id))
                Add(id);
        }
        Changed?.Invoke();
    }

    /// <summary>Logging out: the selection is let go on the region first (llappviewer.cpp's logout deselects everything).</summary>
    public void BeforeLogout() => Deselect();

    /// <summary>Nothing stays selected, and the region is told (a selected physical object is held still until it is deselected).</summary>
    public void Deselect()
    {
        _faceSelection.Clear();
        if (_selection.Count == 0)
            return;
        SendDeselect(_selection);
        _selection.Clear();
        _clickAsked.Clear();
        Changed?.Invoke();
    }

    /// <summary>One ObjectDeselect for the objects given (llselectmgr.cpp deselectAll and deselectObjectOnly).</summary>
    private void SendDeselect(IEnumerable<uint> locals)
    {
        var ids = locals.Where(l => l != 0).Distinct().ToArray();
        if (ids.Length > 0)
            _submit(new DeselectObjectsIntent(ids));
    }

    private void Add(uint id)
    {
        _selection.Add(id);
        // the region answers a selection with the object's properties (owner, creator, permissions)
        _submit(new ObjectPropertiesIntent(id, Hold: true));
        if (Find(RootOf(id)) is { } root)
            _submit(new RequestObjectCostIntent([root.FullId]));
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
        // a teleport lets the selection go before the agent leaves (llagent.cpp teleportCore resets the view, which deselects)
        if (e is TeleportProgressed { Phase: TeleportPhase.Started or TeleportPhase.Local })
        {
            Deselect();
            return;
        }
        ReceiveFeatures(e);
        ReceiveCost(e);
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
        if (TypedPosition(o, position) is not { } p)
            return false;   // outside every region the viewer knows: the window shows the object's own place again
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

    // ---------------- the handles ----------------

    /// <summary>Which handles show: Move, Rotate or Stretch (the Build window's choice; Ctrl held shows Rotate and Ctrl+Shift Stretch).</summary>
    public ManipMode Manip { get; set; } = ManipMode.Move;

    /// <summary>The grid's frame (the Grid Options' ruler mode): the region's axes by default.</summary>
    public GridMode Grid { get; set; }
    /// <summary>The grid's origin and turn in Reference mode (set by Use Selection for Grid).</summary>
    public Vector3 GridOrigin { get; set; }
    public Quaternion GridRotation { get; set; } = Quaternion.Identity;
    /// <summary>How far the drawn grid reaches each way, in metres (GridDrawSize, 12 by default).</summary>
    public float GridExtent { get; set; } = 12f;
    /// <summary>Snapping may go below one grid unit (GridSubUnit, off by default).</summary>
    public bool GridSubUnits { get; set; }
    /// <summary>How strongly the grid is drawn (GridOpacity, 0.7 by default).</summary>
    public float GridOpacity { get; set; } = 0.7f;

    /// <summary>A selected object's pose when a handle drag began, in the region's frame.</summary>
    private sealed record Start(uint Local, Vector3 Position, Quaternion Rotation, Vector3 Scale, uint Parent, Vector3 ParentPosition, Quaternion ParentRotation);
    private List<Start>? _manip;
    private Vector3 _manipPivot;

    /// <summary>The middle of the selection's box of extents whatever the pivot is: where the Stretch handles stand.</summary>
    public Vector3? BoxCentre => Boxes() is { Count: > 0 } boxes ? PivotRules.Centre(boxes) : null;

    private List<PivotBox> Boxes() => _selection.Select(l => (Pose: WorldOf(l), O: Find(l))).Where(x => x.Pose is not null && x.O is not null)
        .Select(x => new PivotBox(x.Pose!.Value.Position, x.Pose.Value.Rotation, x.O!.Scale)).ToList();

    private PivotMode _pivotMode;
    private PivotUnit _pivotUnit = PivotUnit.Percent;
    private Vector3 _pivotOffset;
    private string _pivotFor = "";

    private string SelectionKey() => string.Join(",", _selection.Order());

    /// <summary>The pivot of the selection in the region's frame (<see cref="PivotChoice"/>: the middle of the selection's box of extents, the
    /// root prim's place, or a custom point), or null with nothing selected. Percent and metre offsets from the box's minimum corner, as
    /// Firestorm's pivot settings work (read as behaviour).</summary>
    public Vector3? Pivot
    {
        get
        {
            var boxes = Boxes();
            if (boxes.Count == 0)
                return null;
            switch (PivotChoice)
            {
                case PivotMode.Root when _selection.Count > 0 && WorldOf(RootOf(_selection[0])) is { } root:
                    return root.Position;
                case PivotMode.Custom:
                    return PivotRules.Custom(boxes, _pivotOffset, _pivotUnit);
                default:
                    return PivotRules.Centre(boxes);
            }
        }
    }

    /// <summary>The pivot the handles use now: Centre unless the person chose another for this very selection (a new selection starts at Centre).</summary>
    public PivotMode PivotChoice
    {
        get
        {
            if (_pivotMode != PivotMode.Centre && _pivotFor != SelectionKey())
                _pivotMode = PivotMode.Centre;   // the selection changed since the choice: start again at Centre, and stay there if the old set comes back
            return _pivotMode;
        }
    }
    /// <summary>How the Custom pivot's numbers are read: percent of the box or metres, both from its minimum corner.</summary>
    public PivotUnit PivotUnitChoice => _pivotUnit;
    /// <summary>The Custom pivot's numbers, in <see cref="PivotUnitChoice"/>.</summary>
    public Vector3 PivotOffset => _pivotOffset;

    /// <summary>Chooses where handle drags turn and stretch the selection about; the choice stays with this selection only.</summary>
    public void SetPivot(PivotMode mode)
    {
        _pivotMode = mode;
        _pivotFor = SelectionKey();
        _held = null;   // handles kept after a drag stood on the old pivot
        Changed?.Invoke();
    }

    /// <summary>The Custom pivot's numbers, counted from the box's minimum corner; chooses Custom for this selection.</summary>
    public void SetCustomPivot(Vector3 offset, PivotUnit unit)
    {
        _pivotOffset = offset;
        _pivotUnit = unit;
        SetPivot(PivotMode.Custom);
    }

    /// <summary>The handles' axes: the grid's turn in Reference mode, the primary object's own turn in Local mode or for Stretch, else the world's.</summary>
    public Quaternion HandleFrame =>
        Manip == ManipMode.Scale || Grid == GridMode.Local ? (Primary is { } p && WorldOf(p) is { } w ? w.Rotation : Quaternion.Identity)
        : Grid == GridMode.Reference ? GridRotation : Quaternion.Identity;

    /// <summary>The box the Stretch handles sit on: the primary object's size for one object, else a box round every selected object.</summary>
    public Vector3 HandleSize
    {
        get
        {
            if (_selection.Count == 1 && Primary is { } p && Find(p) is { } o)
                return o.Scale;
            var boxes = _selection.Select(l => (Pose: WorldOf(l), O: Find(l))).Where(x => x.Pose is not null && x.O is not null).ToList();
            if (boxes.Count == 0)
                return Vector3.One;
            var min = boxes.Select(b => b.Pose!.Value.Position - b.O!.Scale / 2).Aggregate(Vector3.Min);
            var max = boxes.Select(b => b.Pose!.Value.Position + b.O!.Scale / 2).Aggregate(Vector3.Max);
            return max - min;
        }
    }

    /// <summary>A handle drag begins: the selected objects' places, turns and sizes are kept, and every step of the drag is measured from them. False
    /// when nothing selected can be moved.</summary>
    public bool BeginManip()
    {
        var starts = new List<Start>();
        foreach (var l in _selection)
        {
            if (Find(l) is not { } o || WorldOf(l) is not { } w || !Access(l).CanPlace)
                continue;
            var (pp, pr) = o.ParentLocalId != 0 && WorldOf(o.ParentLocalId) is { } parent ? parent : (Vector3.Zero, Quaternion.Identity);
            starts.Add(new Start(l, w.Position, w.Rotation, o.Scale, o.ParentLocalId, pp, pr));
        }
        if (starts.Count == 0)
            return false;
        _manip = starts;
        _held = null;
        _turned = null;
        _manipPivot = Pivot ?? starts[0].Position;
        _manipCentre = _manipPivot;
        _manipBox = BoxCentre ?? _manipPivot;
        _manipSize = _manipStartSize = HandleSize;
        _snapLines = [];
        _snapScene = _snapToObjects ? ReadSnapScene(starts.Select(s => s.Local).ToArray()) : null;
        return true;
    }

    /// <summary>Ends a handle drag. The handles stay where the drag left the selection (<see cref="ManipCentre"/>, <see cref="ManipSize"/>)
    /// until the region's own update of the objects arrives, or <see cref="HoldMilliseconds"/> pass: the region answers a move a moment
    /// later, and until then the world still holds the old place, so the handles would jump back to it.</summary>
    public void EndManip()
    {
        if (_manip is not null)
        {
            // after a turn the box the selection stands in has changed: the handles go to the box the turn gives, which is what the
            // region will report, so they neither sit on the old box nor wait for the hold to run out
            _held = (_turned?.Centre ?? _manipCentre, _turned?.Size ?? _manipSize, _clock() + HoldMilliseconds);
        }
        _manip = null;
        _turned = null;
        _snapScene = null;
        _snapLines = [];
    }

    /// <summary>How long after a drag the handles wait for the region's update of the moved objects (the region may refuse the move).</summary>
    public const long HoldMilliseconds = 2000;

    private Vector3 _manipCentre, _manipSize, _manipStartSize, _manipBox;
    private (Vector3 Centre, Vector3 Size, long Until)? _held;
    private (Vector3 Centre, Vector3 Size)? _turned;   // where the handles stand once the drag's turn has been applied

    /// <summary>The kept place of the handles after a drag, while it still stands: until the world's selection has reached it (the region's
    /// update arrived: the same centre and box within a centimetre), the time runs out, the selection is empty, or a new drag starts.</summary>
    private (Vector3 Centre, Vector3 Size)? Held()
    {
        if (_held is not { } h)
            return null;
        if (_clock() > h.Until || _selection.Count == 0 || Pivot is not { } now
            || (Vector3.Distance(now, h.Centre) <= 0.01f && Vector3.Distance(HandleSize, h.Size) <= 0.01f))
        {
            _held = null;
            return null;
        }
        return (h.Centre, h.Size);
    }

    /// <summary>While a handle is dragged, the selection's centre as the drag's latest step put it, so the handles move with the
    /// selection as the official manipulators do (they are drawn at the selection's box, which each drag step updates); after the drag
    /// the same place, until the region's update arrives (<see cref="EndManip"/>); null otherwise.</summary>
    public Vector3? ManipCentre => _manip is null ? Held()?.Centre : _manipCentre;

    /// <summary>While a handle is dragged, the Stretch box's size as the drag's latest step made it (and after it, until the region's update
    /// arrives); null otherwise.</summary>
    public Vector3? ManipSize => _manip is null ? Held()?.Size : _manipSize;

    /// <summary>A Move handle's drag: every object moves by <paramref name="delta"/> from where it began; with snapping on, the selection's centre
    /// lands on the grid along the axes being moved (the grid's own axes). With Snap to objects on, the selection's box then catches on
    /// other boxes within <paramref name="snapDistance"/> metres (never more than <see cref="ObjectSnap.MaxDistance"/>) unless
    /// <paramref name="letGo"/> is set (Alt held).</summary>
    public bool ManipMove(Vector3 delta, float snapDistance = ObjectSnap.MaxDistance, bool letGo = false, float guideMargin = 0)
    {
        if (_manip is not { } starts)
            return false;
        var pointer = delta;
        if (SnapEnabled)
        {
            var (origin, turn) = GridFrame();
            var inv = Quaternion.Inverse(turn);
            var moved = Vector3.Transform(delta, inv);
            var target = Vector3.Transform(_manipPivot + delta - origin, inv);
            float sub = GridSubUnits ? 4 : 1;
            target = new Vector3(
                MathF.Abs(moved.X) > 1e-6f ? Snap.ToGrid(target.X, 0, GridUnit, sub) : target.X,
                MathF.Abs(moved.Y) > 1e-6f ? Snap.ToGrid(target.Y, 0, GridUnit, sub) : target.Y,
                MathF.Abs(moved.Z) > 1e-6f ? Snap.ToGrid(target.Z, 0, GridUnit, sub) : target.Z);
            delta = origin + Vector3.Transform(target, turn) - _manipPivot;
        }
        delta = SnapToOthers(pointer, delta, snapDistance, letGo, guideMargin);   // after the grid, and over it on the axes that catch
        _manipCentre = _manipPivot + delta;
        bool any = false;
        foreach (var s in starts)
            any |= SendPose(s, s.Position + delta, null, null);
        return any;
    }

    /// <summary>A ring's drag: the selection turns by <paramref name="degrees"/> about <paramref name="axis"/> (the region's frame) round its centre.</summary>
    public bool ManipRotate(Vector3 axis, float degrees)
    {
        if (_manip is not { } starts || axis.LengthSquared() < 1e-12f)
            return false;
        var q = Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), degrees * MathF.PI / 180f);
        _manipCentre = _manipPivot;   // a turn round the centre leaves it where it is
        bool any = false;
        var turned = new List<(uint Local, Vector3 Position, Quaternion Rotation)>();
        foreach (var s in starts)
        {
            var place = _manipPivot + Vector3.Transform(s.Position - _manipPivot, q);
            var turn = Quaternion.Normalize(q * s.Rotation);
            turned.Add((s.Local, place, turn));
            any |= SendPose(s, place, turn, null);
        }
        _turned = AfterTurn(turned);
        return any;
    }

    /// <summary>Where the handles stand once the selected objects are at these places and turns: the pivot the person chose and the Stretch box,
    /// worked out the way <see cref="Pivot"/> and <see cref="HandleSize"/> read them from the world.</summary>
    private (Vector3 Centre, Vector3 Size)? AfterTurn(List<(uint Local, Vector3 Position, Quaternion Rotation)> poses)
    {
        var boxes = new List<PivotBox>();
        var scales = new List<Vector3>();
        foreach (var (local, place, turn) in poses)
        {
            if (Find(local) is not { } o)
                return null;
            boxes.Add(new PivotBox(place, turn, o.Scale));
            scales.Add(o.Scale);
        }
        if (boxes.Count == 0)
            return null;
        var centre = PivotChoice switch
        {
            PivotMode.Root when poses.FirstOrDefault(p => p.Local == RootOf(_selection[0])) is { Local: not 0 } root => root.Position,
            PivotMode.Root when WorldOf(RootOf(_selection[0])) is { } root => root.Position,
            PivotMode.Custom => PivotRules.Custom(boxes, _pivotOffset, _pivotUnit),
            _ => PivotRules.Centre(boxes),
        };
        Vector3 size;
        if (boxes.Count == 1)
            size = _manipSize;   // the one object's size as the drag has it (a turn does not change it)
        else
        {
            var min = boxes.Select((b, i) => b.Position - scales[i] / 2).Aggregate(Vector3.Min);
            var max = boxes.Select((b, i) => b.Position + scales[i] / 2).Aggregate(Vector3.Max);
            size = max - min;
        }
        return (centre, size);
    }

    /// <summary>A corner box's drag: every selected object grows by <paramref name="ratio"/> (held so no size leaves the limits) and, with several,
    /// they spread from the selection's centre by the same ratio.</summary>
    public bool ManipScaleCorner(float ratio)
    {
        if (_manip is not { } starts || !(ratio > 0))
            return false;
        var lim = _limits();
        foreach (var s in starts)
        {
            bool physical = ((Find(s.Local)?.Interaction?.Flags ?? 0) & ObjectFlagBits.Physics) != 0;
            float min = physical ? lim.MinPhysScale : lim.MinScale, max = physical ? lim.MaxPhysScale : lim.MaxScale;
            ratio = ScaleHandles.Corner(s.Scale, ratio, min, max).X / s.Scale.X;
        }
        // the Stretch box stands on the selection's middle, which spreads about the pivot with the objects (one object grows about its own middle)
        _manipCentre = starts.Count > 1 ? _manipPivot + (_manipBox - _manipPivot) * ratio : _manipBox;
        _manipSize = _manipStartSize * ratio;
        bool any = false;
        foreach (var s in starts.Where(s => Access(s.Local).CanReshape))
            any |= SendPose(s, starts.Count > 1 ? _manipPivot + (s.Position - _manipPivot) * ratio : s.Position, null, s.Scale * ratio, uniform: starts.Count == 1);
        return any;
    }

    /// <summary>A face box's drag on one object: its size along its own <paramref name="axis"/> becomes <paramref name="newSize"/>; with Stretch Both
    /// Sides off the opposite face stays where it is, with it on the centre stays.</summary>
    public bool ManipScaleFace(int axis, int sign, float newSize)
    {
        if (_manip is not [var s] || !Access(s.Local).CanReshape)
            return false;
        var lim = _limits();
        bool physical = ((Find(s.Local)?.Interaction?.Flags ?? 0) & ObjectFlagBits.Physics) != 0;
        var (scale, shift) = ScaleHandles.Face(s.Scale, axis, sign, newSize, StretchBothSides,
            physical ? lim.MinPhysScale : lim.MinScale, physical ? lim.MaxPhysScale : lim.MaxScale);
        _manipCentre = s.Position + Vector3.Transform(shift, s.Rotation);
        _manipSize = scale;
        return SendPose(s, _manipCentre, null, scale, uniform: StretchBothSides);
    }

    /// <summary>Whether the left button belongs to build mode's tools: while build mode is on a press and drag works the tool and its
    /// handles and never steers the avatar (the official build toolset has no click-to-steer tool; that is the pie tool's, outside build mode).</summary>
    public bool OwnsLeftButton => BuildMode;

    /// <summary>The objects Undo and Redo name: the selected roots, or each selected part with Edit linked on (llselectmgr.cpp undo:
    /// SEND_ONLY_ROOTS unless EditLinkedParts), with what each may have done to it.</summary>
    private IEnumerable<(Guid Id, EditAccess Access)> UndoTargets() =>
        (EditLinked ? _selection.Distinct() : _selection.Select(RootOf).Where(r => r != 0).Distinct())
            .Select(l => (Object: Find(l), Access: Access(l)))
            .Where(t => t.Object is not null)
            .Select(t => (t.Object!.FullId, t.Access));

    /// <summary>Undo is on when a selected object can be moved or modified (llselectmgr.cpp canUndo).</summary>
    public bool CanUndo => UndoTargets().Any(t => t.Access.CanModify || t.Access.CanMove);

    /// <summary>Redo is on when a selected object can be modified (llselectmgr.cpp canRedo).</summary>
    public bool CanRedo => UndoTargets().Any(t => t.Access.CanModify);

    /// <summary>Undo (Ctrl+Z): the region steps each selected object back to the state it kept before its last change.</summary>
    public bool Undo()
    {
        var ids = UndoTargets().Where(t => t.Access.CanModify || t.Access.CanMove).Select(t => t.Id).ToArray();
        return ids.Length > 0 && Send(new UndoObjectsIntent(ids));
    }

    /// <summary>Redo (Ctrl+Y): the region repeats a change it undid on each selected object.</summary>
    public bool Redo()
    {
        var ids = UndoTargets().Where(t => t.Access.CanModify).Select(t => t.Id).ToArray();
        return ids.Length > 0 && Send(new RedoObjectsIntent(ids));
    }

    /// <summary>Shift+X, Snap Object XY to Grid: each selected root's x and y go to the nearest grid point.</summary>
    public bool SnapSelectionXY()
    {
        var (origin, turn) = GridFrame();
        var inv = Quaternion.Inverse(turn);
        bool any = false;
        foreach (var l in _selection.Select(RootOf).Distinct())
        {
            if (Find(l) is not { } o || !Access(l).CanPlace)
                continue;
            var local = Vector3.Transform(o.Position - origin, inv);
            local = new Vector3(Snap.ToGrid(local.X, 0, GridUnit, 1), Snap.ToGrid(local.Y, 0, GridUnit, 1), local.Z);
            any |= Send(new UpdateObjectIntent(l, origin + Vector3.Transform(local, turn), null, null, true));
        }
        return any;
    }

    /// <summary>Shift+G, Use Selection for Grid: the grid takes the primary object's place and turn (Reference mode).</summary>
    public void UseSelectionForGrid()
    {
        if (Primary is not { } p || WorldOf(p) is not { } w)
            return;
        GridOrigin = w.Position;
        GridRotation = w.Rotation;
        Grid = GridMode.Reference;
        Changed?.Invoke();
    }

    /// <summary>G: snapping to the grid on or off.</summary>
    public void ToggleSnap()
    {
        SnapEnabled = !SnapEnabled;
        Changed?.Invoke();
    }

    /// <summary>The grid's origin and turn for snapping: the region's, the primary object's own (Local), or the reference taken from a selection.</summary>
    public (Vector3 Origin, Quaternion Turn) GridFrame() => Grid switch
    {
        GridMode.Reference => (GridOrigin, GridRotation),
        GridMode.Local when Primary is { } p && WorldOf(p) is { } w => (w.Position, w.Rotation),
        _ => (Vector3.Zero, Quaternion.Identity),
    };

    /// <summary>Sends an object's new pose in the region's frame: a root as it is, a part in its root's frame (its root does not move).</summary>
    private bool SendPose(Start s, Vector3 position, Quaternion? rotation, Vector3? scale, bool uniform = false)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            return false;
        if (s.Parent == 0)
            return Send(new UpdateObjectIntent(s.Local, position, rotation, scale, true, uniform));
        var inv = Quaternion.Inverse(s.ParentRotation);
        var local = Vector3.Transform(position - s.ParentPosition, inv);
        Quaternion? localTurn = rotation is { } r ? Quaternion.Normalize(inv * r) : null;
        return Send(new UpdateObjectIntent(s.Local, local, localTurn, scale, false, uniform));
    }

    /// <summary>A prim's place and turn in the region (a part's comes through its root).</summary>
    private (Vector3 Position, Quaternion Rotation)? WorldOf(uint local) =>
        Find(local) is { } o && ObjectPicker.WorldPose(_world, o, out var p, out var r) ? (p, r) : null;

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

    private Vector3? TypedPosition(ObjectState o, Vector3 p)
    {
        if (o.ParentLocalId != 0)
            return float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z) ? p : null;   // a part's place is relative to its root
        var region = _world.Region;
        float w = region?.SizeX ?? 256, h = region?.SizeY ?? 256;
        var regions = _regions?.Invoke() is { Count: > 0 } known ? known : [new RegionArea(0, 0, w, h)];
        // the ground is known for the agent's own region; a neighbour's height is held only to the limits
        bool inOwn = p.X >= 0 && p.X < w && p.Y >= 0 && p.Y < h;
        float ground = inOwn ? _ground?.Invoke(p.X, p.Y) ?? float.NegativeInfinity : float.NegativeInfinity;
        return EditNumbers.TypedPosition(p, regions, ground, o.Scale.Length() / 2f, _limits());
    }
}
