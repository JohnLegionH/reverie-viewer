// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Core.Interaction;
using Viewer.Shell.Build;
using NVector3 = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;
using BuildHandle = Viewer.Shell.Build.Handle;
using NVector2 = System.Numerics.Vector2;

namespace Viewer.Godot;

/// <summary>
/// The edit tools' in-world handles: drawn over the selection with the Edit and Move tools, the one under the pointer lit, and dragged to move,
/// turn or stretch the selection (Ctrl held shows Rotate, Ctrl+Shift Stretch; Shift at the start of a drag leaves a copy behind). The grid is
/// drawn while a Move handle is dragged with snapping on. The maths and what is sent are <see cref="Manipulator"/>'s and
/// <see cref="EditController"/>'s; this side feeds them the pointer and draws their lines.
/// </summary>
public partial class Main
{
    private Presentation.HandleOverlay? _handles;
    private BuildHandle _handleHot = BuildHandle.None;
    private HandleDragState? _handleDrag;

    /// <summary>A handle being dragged, from the press until the release.</summary>
    private sealed class HandleDragState
    {
        public BuildHandle Handle;
        public ManipMode Mode;
        public PickRay Start;
        public NVector3 Centre, Size;
        public NQuaternion Frame;
        public bool Copied;
        public ulong LastSent;
        public Action? Pending;
    }

    private static NVector3 RegionOf(Vector3 engine) => new(engine.X, -engine.Z, engine.Y);

    private PickRay RayAt(Vector2 screen) => new(RegionOf(_camera!.ProjectRayOrigin(screen)), RegionOf(_camera.ProjectRayNormal(screen)));

    private Vector2? ProjectRegion(NVector3 p)
    {
        var engine = Presentation.HandleOverlay.Engine(p);
        return _camera is null || _camera.IsPositionBehind(engine) ? null : _camera.UnprojectPosition(engine);
    }

    private NVector2? ProjectN(NVector3 p) => ProjectRegion(p) is { } v ? new NVector2(v.X, v.Y) : null;

    private HandleView HandleViewNow() =>
        new(RegionOf(_camera!.GlobalPosition), Mathf.DegToRad(_camera.Fov), GetViewport().GetVisibleRect().Size.Y);

    /// <summary>The handles shown now: Ctrl held shows Rotate and Ctrl+Shift Stretch, as the official build tools do.</summary>
    private static ManipMode ManipNow(EditController e) =>
        Input.IsKeyPressed(Key.Ctrl) ? (Input.IsKeyPressed(Key.Shift) ? ManipMode.Scale : ManipMode.Rotate) : e.Manip;

    /// <summary>Where the handles stand: the selection's centre, the handles' axes and the Stretch box; false when none show.</summary>
    private bool HandlePlace(EditController e, ManipMode mode, out NVector3 centre, out NQuaternion frame, out NVector3 size)
    {
        centre = default;
        frame = NQuaternion.Identity;
        size = NVector3.One;
        if (!e.BuildMode || e.Tool is not (BuildTool.Edit or BuildTool.Move) || e.Pivot is not { } pivot || _interactFor is not { } s)
            return false;
        if (!e.Selection.Any(l => e.Access(l).CanPlace))
            return false;
        centre = pivot;
        size = e.HandleSize;
        if (mode == ManipMode.Scale)
        {
            if (e.Selection.Count == 1 && e.Object() is { } o && ObjectPicker.WorldPose(s.World, o, out _, out var turn))
                frame = turn;
        }
        else
            frame = e.GridFrame().Turn;
        return true;
    }

    /// <summary>Draws the handles (and the grid while a Move handle is dragged) for this frame.</summary>
    private void UpdateHandles()
    {
        if (_camera is null || _stage is null)
            return;
        if (_handles is null)
        {
            _handles = new Presentation.HandleOverlay();
            _stage.AddChild(_handles);
        }
        var lines = new List<HandleLine>();
        if (_edit is { } e)
        {
            var mode = _handleDrag?.Mode ?? ManipNow(e);
            if (HandlePlace(e, mode, out var centre, out var frame, out var size))
            {
                if (_handleDrag is { } d)
                {
                    centre = d.Centre;
                    frame = d.Frame;
                    size = mode == ManipMode.Scale ? e.HandleSize : d.Size;
                }
                var view = HandleViewNow();
                lines.AddRange(Manipulator.Lines(mode, centre, frame, size, view, _handleDrag?.Handle ?? _handleHot));
                if (_handleDrag is { Mode: ManipMode.Move } && e.SnapEnabled)
                {
                    var (origin, turn) = e.GridFrame();
                    lines.AddRange(Manipulator.GridLines(e.Pivot ?? centre, origin, turn, e.GridUnit, e.GridExtent, e.GridOpacity));
                }
            }
        }
        _handles.Show(lines, _camera.GlobalPosition, _camera.Fov, GetViewport().GetVisibleRect().Size.Y);
        FlushHeldHandleStep();
        if (_handleDrag is { Pending: { } pending } dr && Time.GetTicksMsec() - dr.LastSent >= 100)
        {
            dr.Pending = null;
            dr.LastSent = Time.GetTicksMsec();
            pending();
        }
    }

    /// <summary>The handle under a point of the view, for the handles shown now.</summary>
    private BuildHandle HandleAt(EditController e, Vector2 at)
    {
        var mode = ManipNow(e);
        return HandlePlace(e, mode, out var centre, out var frame, out var size) ? Manipulator.Hit(mode, centre, frame, size, HandleViewNow(), ProjectN, new NVector2(at.X, at.Y)) : BuildHandle.None;
    }

    /// <summary>A press on a handle starts its drag (Shift copies the selection in place first). True when the press was on a handle.</summary>
    private bool HandlePress(EditController e, Vector2 at, bool shift)
    {
        var mode = ManipNow(e);
        if (!HandlePlace(e, mode, out var centre, out var frame, out var size))
            return false;
        var h = Manipulator.Hit(mode, centre, frame, size, HandleViewNow(), ProjectN, new NVector2(at.X, at.Y));
        if (h.Kind == HandleKind.None || !e.BeginManip())
            return false;
        _handleDrag = new HandleDragState { Handle = h, Mode = mode, Start = RayAt(at), Centre = centre, Frame = frame, Size = size };
        // Shift at the start of a Move drag leaves a copy where the selection was (llmaniptranslate.cpp: selectDuplicate with no offset)
        if (shift && mode == ManipMode.Move && e.CopyInPlace())
        {
            _handleDrag.Copied = true;
            _heldMoveCopyFrom = e.Object()?.Position;   // the primary root: its copy appears where it stands
            _copyAskedAt = Time.GetTicksMsec();
        }
        return true;
    }

    /// <summary>Where a Shift-drag's copy was asked for: the selection's moves wait until the copy stands there (or two seconds have passed).</summary>
    private NVector3? _heldMoveCopyFrom;
    private ulong _copyAskedAt;

    private void HandleMotion(EditController e, Vector2 at)
    {
        if (_handleDrag is not { } d)
            return;
        var now = RayAt(at);
        var axis = NVector3.Transform(Manipulator.AxisVector(d.Handle.Axis), d.Frame);
        Action? step = d.Handle.Kind switch
        {
            HandleKind.Axis => () => e.ManipMove(Manipulator.AxisDrag(d.Centre, axis, d.Start, now)),
            HandleKind.Plane => () => e.ManipMove(Manipulator.PlaneDrag(d.Centre, axis, d.Start, now)),
            HandleKind.Ring => () =>
            {
                float pixels = ProjectRegion(d.Centre) is { } c ? (at - c).Length() : 0f;
                e.ManipRotate(axis, Manipulator.RotationDegrees(Manipulator.RingDegrees(d.Centre, axis, d.Start, now), pixels, e.SnapEnabled));
            },
            HandleKind.Corner => () =>
                e.ManipScaleCorner(Manipulator.CornerRatio(d.Centre, NVector3.Transform(Manipulator.CornerOffset(d.Size, d.Handle.Corner), d.Frame), d.Start, now)),
            HandleKind.Face => () =>
            {
                float moved = Manipulator.AlongAxis(d.Centre, axis, now) - Manipulator.AlongAxis(d.Centre, axis, d.Start);
                float start = d.Handle.Axis switch { 0 => d.Size.X, 1 => d.Size.Y, _ => d.Size.Z };
                e.ManipScaleFace(d.Handle.Axis, d.Handle.Sign, start + moved * d.Handle.Sign * (e.StretchBothSides ? 2 : 1));
            },
            _ => null,
        };
        if (step is null)
            return;
        // once a copy was asked for, the selection moves only when the copy stands where it started
        if (d.Copied && _heldMoveCopyFrom is { } from && !CopyArrivedNear(from) && _copyAskedAt != 0 && Time.GetTicksMsec() - _copyAskedAt < 2000)
        {
            d.Pending = step;
            return;
        }
        // about ten updates a second while dragging (llmanipscale.cpp UPDATE_DELAY)
        if (Time.GetTicksMsec() - d.LastSent >= 100)
        {
            d.LastSent = Time.GetTicksMsec();
            d.Pending = null;
            step();
        }
        else
            d.Pending = step;
    }

    private void HandleRelease(EditController e)
    {
        if (_handleDrag is not { } d)
            return;
        if (d.Pending is { } last)
        {
            if (d.Copied && _heldMoveCopyFrom is { } from && !CopyArrivedNear(from))
                _heldHandleStep = (last, Time.GetTicksMsec());
            else
                last();
        }
        _handleDrag = null;
        e.EndManip();
    }

    /// <summary>A drag's last step held back until its copy has arrived (or two seconds have passed).</summary>
    private (Action Step, ulong Since)? _heldHandleStep;

    private void FlushHeldHandleStep()
    {
        if (_heldHandleStep is not { } h || _heldMoveCopyFrom is not { } from)
            return;
        if (!CopyArrivedNear(from) && Time.GetTicksMsec() - h.Since < 2000)
            return;
        _heldHandleStep = null;
        if (_edit is { } e && e.BeginManip())
        {
            h.Step();
            e.EndManip();
        }
    }

    /// <summary>Whether an own object other than the selected ones stands at <paramref name="at"/> (the copy a Shift-drag asked for).</summary>
    private bool CopyArrivedNear(NVector3 at) =>
        _edit is { } e && _interactFor?.World.Snapshot().Objects.Values.Any(o => o.ParentLocalId == 0 && !e.Selection.Contains(o.Id.LocalId)
            && ((o.Interaction?.Flags ?? 0) & ObjectFlagBits.YouOwner) != 0 && NVector3.Distance(o.Position, at) < 0.05f) == true;

    /// <summary>The grid's keys in build mode: G, Shift+X, Shift+G and Ctrl+Shift+B (also in the Build menu's Options). True when used.</summary>
    private bool GridKey(EditController e, InputEventKey k)
    {
        if (!k.Pressed || k.Echo || k.AltPressed)
            return false;
        switch (k.Keycode)
        {
            case Key.G when !k.CtrlPressed && !k.ShiftPressed:
                e.ToggleSnap();
                return true;
            case Key.X when k.ShiftPressed && !k.CtrlPressed:
                e.SnapSelectionXY();
                return true;
            case Key.G when k.ShiftPressed && !k.CtrlPressed:
                e.UseSelectionForGrid();
                return true;
            case Key.B when k.ShiftPressed && k.CtrlPressed:
                OpenGridOptions();
                return true;
        }
        return false;
    }

    private GridPanel? _gridPanel;

    private void OpenGridOptions()
    {
        if (_shell is null || _gridPanel is null)
            return;
        _gridPanel.Refresh();
        _shell.Shell.Floaters.Open(GridPanel.FloaterId);
    }

    /// <summary>The automation's handle drag, through the same pointer path as a hand: <paramref name="what"/> names the handle (axis:x+, axis:z-,
    /// plane:z, ring:z, corner:N, face:x+); for an arrow or a plane handle (x, y, z) is how far to move, for a ring x is the angle in degrees (the
    /// pointer stays inside the snap ring unless y is 1), for a corner x is the ratio and for a face x is the new size. Shift copies.</summary>
    private object? HandleCall(EditController e, string? what, float x, float y, float z, bool shift)
    {
        if (string.IsNullOrEmpty(what) || _camera is null)
            return new { error = "no handle" };
        var parts = what.Split(':', 2);
        if (parts.Length != 2)
            return new { error = "bad handle" };
        static int AxisOf(char c) => c switch { 'x' => 0, 'y' => 1, _ => 2 };
        var mode = parts[0] switch { "ring" => ManipMode.Rotate, "corner" or "face" => ManipMode.Scale, _ => ManipMode.Move };
        var was = e.Manip;
        e.Manip = mode;
        try
        {
            if (!HandlePlace(e, mode, out var centre, out var frame, out var size))
                return new { error = "no handles shown" };
            var view = HandleViewNow();
            NVector3 from, to;
            switch (parts[0])
            {
                case "axis":
                {
                    int axis = AxisOf(parts[1][0]);
                    int sign = parts[1].EndsWith('-') ? -1 : 1;
                    var dir = NVector3.Transform(Manipulator.AxisVector(axis), frame);
                    from = centre + dir * sign * Manipulator.Metres(Manipulator.ArrowPixels, centre, view) * 0.7f;
                    to = from + new NVector3(x, y, z);
                    break;
                }
                case "plane":
                {
                    int axis = AxisOf(parts[1][0]);
                    var (u, w) = axis switch { 0 => (1, 2), 1 => (0, 2), _ => (0, 1) };
                    float arrow = Manipulator.Metres(Manipulator.ArrowPixels, centre, view);
                    from = centre + (NVector3.Transform(Manipulator.AxisVector(u), frame) + NVector3.Transform(Manipulator.AxisVector(w), frame)) * arrow * Manipulator.PlaneOffset;
                    to = from + new NVector3(x, y, z);
                    break;
                }
                case "ring":
                {
                    int axis = AxisOf(parts[1][0]);
                    var (u, _) = axis switch { 0 => (1, 2), 1 => (0, 2), _ => (0, 1) };
                    var n = NVector3.Transform(Manipulator.AxisVector(axis), frame);
                    var start = NVector3.Transform(Manipulator.AxisVector(u), frame) * Manipulator.Metres(Manipulator.RingPixels, centre, view);
                    from = centre + start;
                    float reach = y >= 1 ? Manipulator.SnapRingRadii * 1.3f : 1f;   // outside the snap ring the turn snaps
                    to = centre + NVector3.Transform(start, NQuaternion.CreateFromAxisAngle(n, x * MathF.PI / 180f)) * reach;
                    break;
                }
                case "corner" when int.TryParse(parts[1], out int corner):
                    from = centre + NVector3.Transform(Manipulator.CornerOffset(size, corner), frame);
                    to = centre + (from - centre) * x;
                    break;
                case "face":
                {
                    int axis = AxisOf(parts[1][0]);
                    int sign = parts[1].EndsWith('-') ? -1 : 1;
                    var dir = NVector3.Transform(Manipulator.AxisVector(axis), frame) * sign;
                    float old = axis switch { 0 => size.X, 1 => size.Y, _ => size.Z };
                    from = centre + dir * old / 2;
                    to = from + dir * (x - old) / (e.StretchBothSides ? 2 : 1);
                    break;
                }
                default:
                    return new { error = "bad handle" };
            }
            if (ProjectRegion(from) is not { } a || ProjectRegion(to) is not { } b)
                return new { error = "off screen" };
            var hit = HandleAt(e, a);
            if (!HandlePress(e, a, shift))
                return new { error = "the press missed the handle", hit = hit.Kind.ToString() };
            var handle = _handleDrag!.Handle;
            for (int i = 1; i <= 8; i++)
            {
                if (_handleDrag is { } d)
                    d.LastSent = 0;   // every step goes out, as a slow hand's would
                HandleMotion(e, a.Lerp(b, i / 8f));
            }
            HandleRelease(e);
            return new { handle = handle.Kind.ToString().ToLowerInvariant(), axis = handle.Axis, sign = handle.Sign, corner = handle.Corner, copied = shift };
        }
        finally { e.Manip = was; }
    }
}
