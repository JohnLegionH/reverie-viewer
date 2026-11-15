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
/// tools (a press on the selection's handles drags them instead, see Main.Handles.cs), Esc leaving build mode, the grid's keys and Grid Options,
/// and the automation's edit command. What is selected and what each change sends is <see cref="EditController"/>'s; this side only feeds it
/// the pointer and draws its state.
/// </summary>
public partial class Main
{
    private EditController? _edit;
    private EditPanel? _editPanel;
    private EditDrag? _editDrag;

    /// <summary>A press with the Edit or Move tool off the handles, until it is released: a click selects, a drag draws a selection box (pressing
    /// on an object and dragging does not move it: with the official tools only the handles move the selection).</summary>
    private sealed class EditDrag
    {
        public Vector2 From;
        public bool Moved, Shift;
    }

    /// <summary>A new session's edit controller; the window and the menu entries are made once and outlive a session.</summary>
    private void AttachEdit(ViewerSession s, InteractionController ic)
    {
        // a typed position must lie in a region the viewer knows: the agent's own and its connected neighbours
        _edit = new EditController(s.World, s.Actions.Submit, () => s.EditLimits,
            regions: () => s.Worlds.Select(w => new RegionArea(w.Offset.X, w.Offset.Y, w.Region.SizeX, w.Region.SizeY)).ToList());
        _edit.Changed += () => _editPanel?.Refresh();
        _editDrag = null;
        if (_editPanel is not null || _shell is null)
            return;
        var shell = _shell.Shell;
        bool InWorld() => _interactFor is { IsEnded: false } && _interact is not null;
        _editPanel = new EditPanel(shell.Strings, () => _edit, () => _interactFor?.Inventory);
        _editPanel.ToolChosen += ChooseTool;
        _shell.AddFloater(new FloaterSpec(EditPanel.FloaterId, "edit.title", new UiSize(460, 680), new UiSize(400, 520)), _editPanel);
        var timer = new global::Godot.Timer { Name = "edit_refresh", WaitTime = 0.25, Autostart = true };
        timer.Timeout += () => { if (_editPanel.IsVisibleInTree()) _editPanel.Refresh(); };
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
        _gridPanel = new GridPanel(shell.Strings, () => _edit);
        _shell.AddFloater(new FloaterSpec(GridPanel.FloaterId, "grid.title", new UiSize(300, 360), new UiSize(260, 300)), _gridPanel);
        shell.Commands.Register(BuildMenu.GridSnapCommand, () => _edit?.ToggleSnap());
        shell.Commands.Register(BuildMenu.GridSnapXYCommand, () => _edit?.SnapSelectionXY());
        shell.Commands.Register(BuildMenu.GridUseSelectionCommand, () => _edit?.UseSelectionForGrid());
        shell.Commands.Register(BuildMenu.GridOptionsCommand, OpenGridOptions);
        shell.Commands.Register(BuildMenu.UndoCommand, () => _edit?.Undo());
        shell.Commands.Register(BuildMenu.RedoCommand, () => _edit?.Redo());
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

    /// <summary>Before logging out, both selections are let go on the region (the build tools' and the right-click one).</summary>
    public void LetGoOfSelections()
    {
        _edit?.BeforeLogout();
        _interact?.Deselect();
    }

    /// <summary>Esc or the window's close: build mode ends, Edit linked goes off, the selection clears and the window closes.</summary>
    private void LeaveBuild()
    {
        _edit?.CloseBuild();
        _interact?.CancelCreate();
        _editDrag = null;
        _handleDrag = null;
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
            case InputEventKey k when GridKey(e, k):
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } b when e.Tool is BuildTool.Edit or BuildTool.Move:
                if (b.Pressed)
                {
                    if (!HandlePress(e, b.Position, b.ShiftPressed))
                        EditPress(e, b.Position, b.ShiftPressed);
                }
                else if (_handleDrag is not null)
                    HandleRelease(e);
                else
                    EditRelease(e, b.Position);
                return true;
            case InputEventMouseMotion m when _handleDrag is not null:
                HandleMotion(e, m.Position);
                return true;
            case InputEventMouseMotion m when _editDrag is { } d:
                EditMotion(e, d, m.Position);
                return true;
            case InputEventMouseMotion m when e.Tool is BuildTool.Edit or BuildTool.Move:
                _handleHot = HandleAt(e, m.Position);   // the handle under the pointer is lit
                return false;
        }
        return false;
    }

    private void EditPress(EditController e, Vector2 at, bool shift)
    {
        e.Click(PickAt(at), shift);
        _editDrag = new EditDrag { From = at, Shift = shift };
    }

    private static void EditMotion(EditController e, EditDrag d, Vector2 at)
    {
        if (!d.Moved && (at - d.From).Length() > 2f)
            d.Moved = true;   // the official drag starts past a two-pixel slop
    }

    private void EditRelease(EditController e, Vector2 at)
    {
        var d = _editDrag;
        _editDrag = null;
        if (d is { Moved: true })
            BoxSelect(e, d.From, at, d.Shift);   // a drag draws a selection box (lltoolselectrect.cpp)
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

    /// <summary>The Grid Options window's own close button (its frame's), for the automation to click.</summary>
    private Control? GridCloseButton()
    {
        for (Node? n = _gridPanel; n is not null; n = n.GetParent())
            if (n.FindChild("close", true, false) is Control c && c.IsVisibleInTree() && c is BaseButton)
                return c;
        return null;
    }

    /// <summary>The automation's edit command. state: build mode, the tool, the selection, the access, the window's texts and the primary object's
    /// place, size, rotation and shape; toggle (Ctrl+B); tool (text: focus, move, edit, create); click (the point x, y, z on screen; expect "shift"
    /// adds); select (text: local ids, comma-separated); linked (text: on or off); position, size, rotation (x, y, z); shape (text: key=value pairs:
    /// type, hollow, twist_begin, twist_end, path_begin, path_end, taper_x, taper_y); name (text); flags (text: physical,temporary,phantom present
    /// or not); locked (text: on or off); link; unlink; handle (text: the handle, see <see cref="HandleCall"/>; expect "shift" copies); manip (text: move,
    /// rotate, scale); snap (text: on or off); snap_xy; use_selection_grid; grid (text: world, local or reference); grid_options (opens it).</summary>
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
            case "handle": done = HandleCall(e, text, x, y, z, shift); break;
            case "handle_points": done = HandlePoints(e, text, x, y, z); break;
            case "undo": done = e.Undo(); break;
            case "manip":
                if (Enum.TryParse<ManipMode>(text, true, out var manip)) e.Manip = manip;
                break;
            case "snap": e.SnapEnabled = text == "on"; break;
            case "snap_xy": done = e.SnapSelectionXY(); break;
            case "use_selection_grid": e.UseSelectionForGrid(); break;
            case "grid":
                if (Enum.TryParse<GridMode>(text, true, out var grid)) e.Grid = grid;
                break;
            case "grid_options": OpenGridOptions(); done = _shell?.Shell.Floaters.IsOpen(GridPanel.FloaterId); break;
            // where things are on screen, for the live check's own pointer and keys: a control of the Build window by name (scrolled into
            // view first; "tab:texture" for a tab), and a point of the world
            case "control": _editPanel.ScrollTo(text ?? ""); done = _editPanel.ControlPoint(text ?? ""); break;
            case "screen": done = Screen(p) is { } sp ? new[] { sp.X, sp.Y } : null; break;
            case "picker": done = _editPanel.TextureTab.Picker is { Visible: true } pk ? pk.Names : null; break;
            case "picker_row":
                if (_editPanel.TextureTab.Picker is { Visible: true } pr && int.TryParse(text, out int row) && row >= 0 && row < pr.List.ItemCount)
                {
                    pr.List.EnsureCurrentIsVisible();
                    var rect = pr.List.GetItemRect(row);
                    var at = pr.List.GetGlobalRect().Position + rect.Position + rect.Size / 2 + new Vector2(pr.Position.X, pr.Position.Y);
                    done = new[] { at.X, at.Y };
                }
                break;
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
            manip = e.Manip.ToString().ToLowerInvariant(),
            snap = e.SnapEnabled,
            grid = new { mode = e.Grid.ToString().ToLowerInvariant(), origin = new[] { e.GridOrigin.X, e.GridOrigin.Y, e.GridOrigin.Z }, unit = e.GridUnit },
            handles_drawn = _handles?.Visible ?? false,
            edit_linked = e.EditLinked,
            window_open = _shell?.Shell.Floaters.IsOpen(EditPanel.FloaterId) ?? false,
            grid_window_open = _shell?.Shell.Floaters.IsOpen(GridPanel.FloaterId) ?? false,
            dragging = _handleDrag is not null,
            can_undo = e.CanUndo, can_redo = e.CanRedo,
            grid_unit_field = _gridPanel?.FindChild("unit", true, false) is SpinBox sb && sb.IsVisibleInTree() ? new[] { sb.GetLineEdit().GetGlobalRect().GetCenter().X, sb.GetLineEdit().GetGlobalRect().GetCenter().Y } : null,
            grid_close = GridCloseButton() is { } gc ? new[] { gc.GetGlobalRect().GetCenter().X, gc.GetGlobalRect().GetCenter().Y } : null,
            focus = GetViewport().GuiGetFocusOwner()?.Name.ToString(),
            placing = _interact?.Placing ?? false,
            selection = e.Selection.ToArray(),
            select_faces = e.SelectFaces,
            faces = e.SelectedFaces.Select(f => new
            {
                local = f.Local, face = f.Face,
                settings = s.World.Snapshot().Objects.Values.FirstOrDefault(o => o.Id.LocalId == f.Local) is { } fo && FaceRules.Settings(fo, f.Face) is var st
                    ? new { texture = st.TextureId, repeats = new[] { st.RepeatU, st.RepeatV }, offset = new[] { st.OffsetU, st.OffsetV }, rotation = st.Rotation,
                        planar = st.TexGen == Viewer.Core.World.TexGen.Planar, material = fo.Materials?[f.Face].RenderMaterialId ?? Guid.Empty }
                    : null,
            }).ToArray(),
            handles_centre = _handlesDrawnAt is { } hc && (_handles?.Visible ?? false) ? new[] { hc.X, hc.Y, hc.Z } : null,
            face = e.FaceShown() is { } fs ? new
            {
                texture = fs.TextureId, colour = new[] { fs.Colour.R, fs.Colour.G, fs.Colour.B, fs.Colour.A }, glow = fs.Glow, fullbright = fs.Fullbright,
                planar = fs.TexGen == Viewer.Core.World.TexGen.Planar, repeats = new[] { fs.RepeatU, fs.RepeatV }, offset = new[] { fs.OffsetU, fs.OffsetV }, rotation = fs.Rotation,
                material = e.ShownMaterial,
            } : null,
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
