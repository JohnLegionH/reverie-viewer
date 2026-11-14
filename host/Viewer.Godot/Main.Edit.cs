// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Viewer.Core.Building;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Viewer.Shell.Build;

namespace Viewer.Godot;

/// <summary>
/// Build mode in the world: the Build window, the Build menu's edit entries and shortcuts, clicks and drag-select with the Edit and Move
/// tools, the Move tool's drag with its Shift copy, Esc leaving build mode, and the automation's edit command. What is selected and what
/// each change sends is <see cref="EditController"/>'s; this side only feeds it the pointer and draws its state.
/// </summary>
public partial class Main
{
    private EditController? _edit;
    private EditPanel? _editPanel;
    private EditDrag? _editDrag;

    /// <summary>A press with the Edit or Move tool, until it is released.</summary>
    private sealed class EditDrag
    {
        public Vector2 From;
        public bool Moved, Copied, Shift, OnObject;
        public System.Numerics.Vector3 GrabPoint, StartPosition;
        public uint Object;
        public System.Numerics.Vector3? Target;
        public ulong CopiedAt, LastSent;
    }

    /// <summary>A move held back until the copy a Shift-drag asked for has appeared where the original was: the region may handle the
    /// copy after a move sent at the same moment, and the copy would then appear at the new place.</summary>
    private (uint Object, System.Numerics.Vector3 From, System.Numerics.Vector3 To, ulong Since)? _heldMove;

    /// <summary>Whether a copy of the dragged object stands where it started (another own object at that place).</summary>
    private bool CopyArrived(uint original, System.Numerics.Vector3 at) =>
        _interactFor?.World.Snapshot().Objects.Values.Any(o => o.ParentLocalId == 0 && o.Id.LocalId != original
            && ((o.Interaction?.Flags ?? 0) & Viewer.Core.Interaction.ObjectFlagBits.YouOwner) != 0
            && System.Numerics.Vector3.Distance(o.Position, at) < 0.01f) ?? false;

    /// <summary>Sends a held move once its copy has arrived, or after two seconds.</summary>
    private void FlushHeldMove()
    {
        if (_heldMove is not { } h || _edit is not { } e)
            return;
        if (!CopyArrived(h.Object, h.From) && Time.GetTicksMsec() - h.Since < 2000)
            return;
        _heldMove = null;
        e.SetPosition(h.To);
    }

    /// <summary>A new session's edit controller; the window and the menu entries are made once and outlive a session.</summary>
    private void AttachEdit(ViewerSession s, InteractionController ic)
    {
        _edit = new EditController(s.World, s.Actions.Submit, () => s.EditLimits);
        _edit.Changed += () => _editPanel?.Refresh();
        _editDrag = null;
        if (_editPanel is not null || _shell is null)
            return;
        var shell = _shell.Shell;
        bool InWorld() => _interactFor is { IsEnded: false } && _interact is not null;
        _editPanel = new EditPanel(shell.Strings, () => _edit);
        _editPanel.ToolChosen += ChooseTool;
        _shell.AddFloater(new FloaterSpec(EditPanel.FloaterId, "edit.title", new UiSize(460, 680), new UiSize(400, 520)), _editPanel);
        var timer = new global::Godot.Timer { Name = "edit_refresh", WaitTime = 0.25, Autostart = true };
        timer.Timeout += () => { FlushHeldMove(); if (_editPanel.IsVisibleInTree()) _editPanel.Refresh(); };
        _editPanel.AddChild(timer);
        shell.Floaters.Closed += id => { if (id == EditPanel.FloaterId && _edit is { BuildMode: true } e) e.CloseBuild(); };
        shell.Commands.Register(BuildMenu.ToggleCommand, ToggleBuild);
        shell.Commands.Register(BuildMenu.FocusCommand, () => ChooseTool(BuildTool.Focus));
        shell.Commands.Register(BuildMenu.MoveCommand, () => ChooseTool(BuildTool.Move));
        shell.Commands.Register(BuildMenu.EditCommand, () => ChooseTool(BuildTool.Edit));
        shell.Commands.Register(BuildMenu.EditLinkedCommand, () => { if (_edit is { } e) e.EditLinked = !e.EditLinked; });
        shell.Commands.Register(BuildMenu.LinkCommand, () => _edit?.Link());
        shell.Commands.Register(BuildMenu.UnlinkCommand, () => _edit?.Unlink());
        // the Create entry (Ctrl+4) is the Create tool of build mode, and its click places the box as before
        shell.Commands.Register(BuildMenu.CreateCommand, () => ChooseTool(BuildTool.Create));
        BuildMenu.InstallEditing(shell.Menus, InWorld, () => _edit, () => _edit?.Selection.Count > 0);
    }

    private void ToggleBuild()
    {
        if (_edit is not { } e || _shell is null)
            return;
        e.ToggleBuild();
        if (e.BuildMode)
        {
            _shell.Shell.Floaters.Open(EditPanel.FloaterId);
            _interact?.BeginCreate();   // build mode opens with the Create tool (lltoolmgr.cpp)
        }
        else
            LeaveBuild();
    }

    private void ChooseTool(BuildTool tool)
    {
        if (_edit is not { } e || _shell is null)
            return;
        e.SelectTool(tool);
        _shell.Shell.Floaters.Open(EditPanel.FloaterId);
        if (tool == BuildTool.Create)
            _interact?.BeginCreate();
        else
            _interact?.CancelCreate();
        _editPanel?.Refresh();
    }

    /// <summary>Esc or the window's close: build mode ends, Edit linked goes off, the selection clears and the window closes.</summary>
    private void LeaveBuild()
    {
        _edit?.CloseBuild();
        _interact?.CancelCreate();
        _editDrag = null;
        if (_shell is { } sh && sh.Shell.Floaters.IsOpen(EditPanel.FloaterId))
            sh.Shell.Floaters.Close(EditPanel.FloaterId);
    }

    /// <summary>The pointer and Esc while build mode is on. True when used.</summary>
    private bool EditInput(InputEvent ev)
    {
        if (_edit is not { BuildMode: true } e || _camera is null)
            return false;
        switch (ev)
        {
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }:
                LeaveBuild();
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } b when e.Tool is BuildTool.Edit or BuildTool.Move:
                if (b.Pressed)
                    EditPress(e, b.Position, b.ShiftPressed);
                else
                    EditRelease(e, b.Position);
                return true;
            case InputEventMouseMotion m when _editDrag is { } d:
                EditMotion(e, d, m.Position);
                return true;
        }
        return false;
    }

    private void EditPress(EditController e, Vector2 at, bool shift)
    {
        var pick = PickAt(at);
        var d = new EditDrag { From = at, Shift = shift };
        if (pick.Kind == Viewer.Core.Interaction.PickKind.Object && pick.Hit is { } hit && _interactFor?.World.Object(hit.Id) is { } prim)
        {
            uint id = e.EditLinked ? prim.Id.LocalId : RootLocal(prim);
            // pressing on a selected object with Shift starts a copy-drag; otherwise the press selects as a click does
            if (!(shift && e.Selection.Contains(id) && e.Tool == BuildTool.Move))
                e.Click(pick, shift);
            if (e.Object(id) is { } o)
            {
                d.OnObject = true;
                d.Object = id;
                d.GrabPoint = pick.Point;
                d.StartPosition = o.Position;
            }
        }
        _editDrag = d;
    }

    private void EditMotion(EditController e, EditDrag d, Vector2 at)
    {
        if (!d.Moved && (at - d.From).Length() <= 2f)
            return;   // the official drag starts past a two-pixel slop
        d.Moved = true;
        if (!d.OnObject || e.Tool != BuildTool.Move || !e.Access(d.Object).CanPlace)
            return;
        if (d.Shift && !d.Copied && (d.Copied = e.CopyInPlace()))   // the copy stays where it is; the original moves on with the drag
            d.CopiedAt = Time.GetTicksMsec();
        // the object follows the pointer across the horizontal plane through the point it was grabbed at
        var origin = Region(_camera!.ProjectRayOrigin(at));
        var dir = Region(_camera.ProjectRayNormal(at));
        if (MathF.Abs(dir.Z) < 1e-4f)
            return;
        float t = (d.GrabPoint.Z - origin.Z) / dir.Z;
        if (t <= 0)
            return;
        var hit = origin + dir * t;
        var target = d.StartPosition + new System.Numerics.Vector3(hit.X - d.GrabPoint.X, hit.Y - d.GrabPoint.Y, 0);
        if (e.SnapEnabled)
            target = new System.Numerics.Vector3(Snap.ToGrid(target.X, 0, e.GridUnit, 1), Snap.ToGrid(target.Y, 0, e.GridUnit, 1), target.Z);
        d.Target = target;
        // the object follows the drag about ten times a second, once any copy has appeared where it started
        ulong now = Time.GetTicksMsec();
        if (now - d.LastSent >= 100 && (!d.Copied || CopyArrived(d.Object, d.StartPosition)))
        {
            d.LastSent = now;
            e.SetPosition(target);
        }
    }

    private void EditRelease(EditController e, Vector2 at)
    {
        var d = _editDrag;
        _editDrag = null;
        if (d is null || !d.Moved)
            return;
        if (d.OnObject && d.Target is { } target)
        {
            if (d.Copied && !CopyArrived(d.Object, d.StartPosition))
                _heldMove = (d.Object, d.StartPosition, target, d.CopiedAt);
            else
                e.SetPosition(target);
            return;
        }
        if (!d.OnObject && e.Tool == BuildTool.Edit)
            BoxSelect(e, d.From, at, d.Shift);
    }

    /// <summary>Drag-select: every object whose centre is inside the dragged box on screen.</summary>
    private void BoxSelect(EditController e, Vector2 a, Vector2 b, bool shift)
    {
        if (_interactFor is not { } s || _camera is null)
            return;
        var rect = new Rect2(a, Vector2.Zero).Expand(b);
        var inside = new List<uint>();
        foreach (var o in s.World.Snapshot().Objects.Values.Where(o => o.AttachmentPoint is null))
        {
            if (!Viewer.Core.Interaction.ObjectPicker.WorldPose(s.World, o, out var pos, out _))
                continue;
            var engine = new Vector3(pos.X, pos.Z, -pos.Y);
            if (_camera.IsPositionBehind(engine))
                continue;
            if (rect.HasPoint(_camera.UnprojectPosition(engine)))
                inside.Add(o.Id.LocalId);
        }
        e.SelectMany(inside, shift);
    }

    private uint RootLocal(ObjectState o)
    {
        var w = _interactFor?.World;
        for (int i = 0; i < 16 && o.ParentLocalId != 0 && w?.Object(new ObjectId(o.Id.Epoch, o.ParentLocalId)) is { } p; i++)
            o = p;
        return o.Id.LocalId;
    }

    /// <summary>The objects the outline marks: the edit selection while build mode has one, else the right-click selection.</summary>
    private IEnumerable<(ObjectState Root, ObjectId Highlight)> EditOutlined(WorldState world)
    {
        if (_edit is not { BuildMode: true } e)
            yield break;
        var all = world.Snapshot().Objects.Values.ToList();
        foreach (var id in e.Selection)
        {
            if (all.FirstOrDefault(o => o.Id.LocalId == id) is not { } o)
                continue;
            var root = o;
            for (int i = 0; i < 16 && root.ParentLocalId != 0 && all.FirstOrDefault(p => p.Id.LocalId == root.ParentLocalId) is { } p; i++)
                root = p;
            yield return (root, o.Id);
        }
    }

    /// <summary>The automation's edit command. state: build mode, the tool, the selection, the access, the window's texts and the primary object's
    /// place, size, rotation and shape; toggle (Ctrl+B); tool (text: focus, move, edit, create); click (the point x, y, z on screen; expect "shift"
    /// adds); select (text: local ids, comma-separated); linked (text: on or off); position, size, rotation (x, y, z); shape (text: key=value pairs:
    /// type, hollow, twist_begin, twist_end, path_begin, path_end, taper_x, taper_y); name (text); flags (text: physical,temporary,phantom present
    /// or not); locked (text: on or off); link; unlink; drag (Move tool: from the primary object to the point x, y, z; expect "shift" copies).</summary>
    public object? EditCall(string action, float x, float y, float z, string? text, string? expect)
    {
        if (_edit is not { } e || _interactFor is not { } s || _camera is null || _editPanel is null)
            return null;
        var p = new System.Numerics.Vector3(x, y, z);
        Vector2? Screen(System.Numerics.Vector3 v)
        {
            var engine = new Vector3(v.X, v.Z, -v.Y);
            return _camera.IsPositionBehind(engine) ? null : _camera.UnprojectPosition(engine);
        }
        bool shift = expect == "shift";
        object? done = null;
        switch (action)
        {
            case "toggle": ToggleBuild(); break;
            case "tool":
                if (Enum.TryParse<BuildTool>(text, true, out var tool)) ChooseTool(tool);
                break;
            case "click":
                if (Screen(p) is { } px) done = e.Click(PickAt(px), shift);
                break;
            case "select":
                e.SelectMany((text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => uint.TryParse(t.Trim(), out var v) ? v : 0).Where(v => v != 0), shift);
                break;
            case "linked": e.EditLinked = text == "on"; break;
            case "position": done = e.SetPosition(p); break;
            case "size": done = e.SetSize(p); break;
            case "rotation": done = e.SetRotation(p); break;
            case "name": done = e.SetName(text ?? ""); break;
            case "locked": done = e.SetLocked(text == "on"); break;
            case "flags":
            {
                var f = (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                done = e.SetFlags(f.Contains("physical"), f.Contains("temporary"), f.Contains("phantom"));
                break;
            }
            case "shape" when e.Object() is { Shape: { } current }:
            {
                var shape = current;
                foreach (var kv in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var parts = kv.Split('=', 2);
                    if (parts.Length != 2) continue;
                    if (parts[0] == "type" && Enum.TryParse<PrimKind>(parts[1], true, out var kind)) { shape = ShapeRules.OfKind(kind); continue; }
                    if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
                    double range = ShapeRules.TwistDegreesRange(shape);
                    shape = parts[0] switch
                    {
                        "hollow" => shape with { ProfileHollow = v / 100.0 },
                        "twist_begin" => shape with { PathTwistBegin = v / range },
                        "twist_end" => shape with { PathTwist = v / range },
                        "path_begin" => shape with { PathBegin = v },
                        "path_end" => shape with { PathEnd = v },
                        "taper_x" => shape with { PathScaleX = 1 - v },
                        "taper_y" => shape with { PathScaleY = 1 - v },
                        _ => shape,
                    };
                }
                done = e.SetShape(shape);
                break;
            }
            case "link": done = e.Link() ?? "sent"; break;
            case "unlink": done = e.Unlink(); break;
            case "drag" when e.Object() is { } o && Screen(o.Position) is { } from && Screen(p with { Z = o.Position.Z }) is { } to:
            {
                // the Move tool's drag, from the object's centre to the point at the object's height, in steps as a hand would
                if (e.Tool != BuildTool.Move) ChooseTool(BuildTool.Move);
                EditPress(e, from, shift);
                for (int i = 1; i <= 8; i++)
                    if (_editDrag is { } dr) EditMotion(e, dr, from.Lerp(to, i / 8f));
                var target = _editDrag?.Target;
                EditRelease(e, to);
                done = new { copied = target is not null && shift, target = target is { } t ? new[] { t.X, t.Y, t.Z } : null };
                break;
            }
        }
        _editPanel.Refresh();
        var prim = e.Object();
        var access = prim is null ? null : e.Access(prim.Id.LocalId);
        var shapeNow = prim?.Shape;
        return new
        {
            done,
            build_mode = e.BuildMode,
            tool = e.Tool.ToString().ToLowerInvariant(),
            edit_linked = e.EditLinked,
            window_open = _shell?.Shell.Floaters.IsOpen(EditPanel.FloaterId) ?? false,
            placing = _interact?.Placing ?? false,
            selection = e.Selection.ToArray(),
            primary = prim is null ? null : new
            {
                local = prim.Id.LocalId, id = prim.FullId, parent = prim.ParentLocalId,
                position = new[] { prim.Position.X, prim.Position.Y, prim.Position.Z },
                size = new[] { prim.Scale.X, prim.Scale.Y, prim.Scale.Z },
                rotation = new[] { EditNumbers.ToEulerDegrees(prim.Rotation).X, EditNumbers.ToEulerDegrees(prim.Rotation).Y, EditNumbers.ToEulerDegrees(prim.Rotation).Z },
                type = shapeNow is null ? null : ShapeRules.KindOf(shapeNow).ToString(),
                hollow = shapeNow?.ProfileHollow, twist_begin = shapeNow?.PathTwistBegin, twist_end = shapeNow?.PathTwist,
                read_only = access?.ReadOnly, can_place = access?.CanPlace, can_reshape = access?.CanReshape, locked = access?.Locked,
                prims_in_linkset = s.World.Snapshot().Objects.Values.Count(o => o.Id.LocalId == prim.Id.LocalId || o.ParentLocalId == prim.Id.LocalId),
            },
            window = _editPanel.Texts(),
        };
    }
}
