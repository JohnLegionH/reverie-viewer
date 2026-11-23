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
        _edit.ReadyToOpen += OpenContentScript;
        _edit.KeepToolSelected = _settings?.Get(Viewer.Shell.Build.BuildSettings.KeepToolSelected) ?? false;
        _editDrag = null;
        // Create makes the box of the new-prim settings (Preferences, Building) and puts the rest of them on it when it arrives
        Viewer.Core.Building.NewPrimDefaults NewPrim() =>
            (_settings is { } st ? NewPrimSettings.Read(st) : Viewer.Core.Building.NewPrimDefaults.Default).Clamped(s.EditLimits);
        ic.NewPrim = NewPrim;
        _newPrims = new Viewer.Shell.Build.NewPrimTracker(s.Actions.Submit, NewPrim);
        ic.BoxCreated += () => _newPrims?.Expect();
        if (_editPanel is not null || _shell is null)
            return;
        var shell = _shell.Shell;
        bool InWorld() => _interactFor is { IsEnded: false } && _interact is not null;
        // people on the card and in the Contents section are named through the session's name cache (asked for in the next name request)
        _editPanel = new EditPanel(shell.Strings, () => _edit, () => _interactFor?.Inventory, _settings,
            () => _interactFor?.AgentId ?? Guid.Empty, id => _interactFor is { IsEnded: false } named ? named.Names.Get(id) : null);
        _editPanel.ContentTab.OpenRequested = OpenContentItem;
        _editPanel.ContentTab.Deleted = ContentItemDeleted;
        _editPanel.ContentTab.ScriptRegionOf = () => _interactFor is { IsEnded: false } s ? s.Scripts?.Region ?? Viewer.Core.Inventory.ScriptRegion.None : Viewer.Core.Inventory.ScriptRegion.None;
        _editPanel.ContentTab.Notice = new Viewer.Shell.Inventory.EarlyAccessNotice(UserSettings(null));
        _editPanel.ContentTab.SetEngine = async (itemId, target) =>
            _interactFor is { IsEnded: false, Scripts: { } scripts } s && _edit?.ScriptToOpen(itemId, s.AgentId) is { } access && await scripts.SetScriptEngineAsync(access, target, default) is { } r ? r : Viewer.Core.Inventory.ScriptEngineReply.NotSent;
        _editPanel.ToolChosen += ChooseTool;
        _editPanel.KeepToolChanged += on => _settings?.Set(Viewer.Shell.Build.BuildSettings.KeepToolSelected, on);
        _shell.AddFloater(new FloaterSpec(EditPanel.FloaterId, "edit.title", new UiSize(460, 680), new UiSize(400, 520)), _editPanel);
        _editPanel.TextureTab.AttachPickers(_shell);
        _takeFolder = new FolderPickPanel(shell.Strings, folder => _edit?.TakeIntoFolder(folder), () => shell.Floaters.Close(FolderPickPanel.FloaterId));
        _shell.AddFloater(new FloaterSpec(FolderPickPanel.FloaterId, "edit.take_folder.title", new UiSize(380, 460), new UiSize(320, 340)), _takeFolder);
        var timer = new global::Godot.Timer { Name = "edit_refresh", WaitTime = 0.25, Autostart = true };
        int costTicks = 0;
        // the land impact is asked for again every three seconds while something is selected (an edit changes it)
        timer.Timeout += () => { if (_editPanel.IsVisibleInTree()) { _editPanel.Refresh(); if (++costTicks % 12 == 0) _edit?.RefreshCosts(); } };
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
        BuildMenu.InstallSelectionHelpers(shell.Menus, InWorld, () => _edit);
        BuildMenu.InstallObject(shell.Menus, InWorld, () => _edit);
        shell.Commands.Register(BuildMenu.DuplicateCommand, () =>
        {
            if (_edit?.Duplicate() is { } why)
                _editPanel?.Say("edit." + why);
        });
        shell.Commands.Register(BuildMenu.NextPartCommand, () => _edit?.SelectNextPart(true));
        shell.Commands.Register(BuildMenu.PreviousPartCommand, () => _edit?.SelectNextPart(false));
        shell.Commands.Register(BuildMenu.OwnedOnlyCommand, () => { if (_edit is { } e) { e.SelectOwnedOnly = !e.SelectOwnedOnly; _editPanel?.Refresh(); } });
        shell.Commands.Register(BuildMenu.TakeIntoFolderCommand, OpenTakeIntoFolder);
        // Build › Scripts: on while something the agent may modify is chosen (EditableSelected)
        BuildMenu.InstallScripts(shell.Menus, InWorld, () => _edit is { } chosen && chosen.Selection.Any(l => chosen.Access(l).CanModify));
        shell.Commands.Register(BuildMenu.RecompileMonoCommand, () => StartScriptQueue(ScriptQueueAction.CompileMono));
        shell.Commands.Register(BuildMenu.RecompileLslCommand, () => StartScriptQueue(ScriptQueueAction.CompileLsl));
        shell.Commands.Register(BuildMenu.ResetScriptsCommand, () => StartScriptQueue(ScriptQueueAction.Reset));
        shell.Commands.Register(BuildMenu.SetRunningCommand, () => StartScriptQueue(ScriptQueueAction.SetRunning));
        shell.Commands.Register(BuildMenu.SetNotRunningCommand, () => StartScriptQueue(ScriptQueueAction.SetNotRunning));
        _queueWindow = new ScriptQueueWindow(shell.Strings, () => shell.Floaters.Close(ScriptQueueWindow.FloaterId));
        _shell.AddFloater(new FloaterSpec(ScriptQueueWindow.FloaterId, "queue.title.compile", new UiSize(300, 400), new UiSize(240, 240)), _queueWindow.Root);
        var queueTimer = new global::Godot.Timer { Name = "script_queue_refresh", WaitTime = 0.25, Autostart = true };
        queueTimer.Timeout += () => { if (_queueWindow.Root.IsVisibleInTree()) _queueWindow.Refresh(); };
        _queueWindow.Root.AddChild(queueTimer);
        // closing the progress window stops its queue, as the official queue ends with its floater
        shell.Floaters.Closed += id => { if (id == ScriptQueueWindow.FloaterId) _scriptQueueStop?.Cancel(); };
        // folder drops and takes into a folder go one at a time, at Firestorm's Z-take pace (0.66 s)
        var pump = new global::Godot.Timer { Name = "edit_queues", WaitTime = 0.66, Autostart = true };
        pump.Timeout += () => _edit?.PumpQueues();
        _editPanel.AddChild(pump);
    }

    private ScriptQueueWindow? _queueWindow;
    private ScriptQueue? _scriptQueue;
    private System.Threading.CancellationTokenSource? _scriptQueueStop;

    /// <summary>Build › Scripts: the queue for the chosen objects runs off the main thread and its window shows the lines as they come
    /// (llviewermenu.cpp LLToolsSelectedScriptAction and queue_actions); a selection with no script, or none the agent may modify, gets the
    /// official refusal instead. A new queue stops the one before.</summary>
    private void StartScriptQueue(ScriptQueueAction action)
    {
        if (_edit is not { } e || _interactFor is not { IsEnded: false } s || _shell is null || _queueWindow is null)
            return;
        _scriptQueueStop?.Cancel();
        var strings = _shell.Shell.Strings;
        _shell.SetFloaterTitle(ScriptQueueWindow.FloaterId, strings.Get(ScriptQueue.TitleKey(action)));
        var (objects, refusal) = e.ScriptQueueObjects();
        if (refusal is { } why)
        {
            _scriptQueue = null;
            _queueWindow.ShowRefusal(strings.Get(ScriptQueue.RefusalKey(action, why)));
            _shell.Shell.Floaters.Open(ScriptQueueWindow.FloaterId);
            Log.Info("script.queue-refused", Viewer.Core.Diagnostics.CoreLog.F("action", action.ToString()), Viewer.Core.Diagnostics.CoreLog.F("why", why.ToString()));
            return;
        }
        var stop = new System.Threading.CancellationTokenSource();
        _scriptQueueStop = stop;
        var queue = new ScriptQueue(action, objects, s.AgentId, s.Actions.Submit, s.Scripts);
        _scriptQueue = queue;
        _queueWindow.Show(queue);
        _shell.Shell.Floaters.Open(ScriptQueueWindow.FloaterId);
        Log.Info("script.queue", Viewer.Core.Diagnostics.CoreLog.F("action", action.ToString()), Viewer.Core.Diagnostics.CoreLog.F("objects", objects.Count));
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try { await queue.RunAsync(stop.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Warn("script.queue-failed", Viewer.Core.Diagnostics.CoreLog.F("error", ex.GetType().Name)); }
            if (queue.Done)
                Log.Info("script.queue-done", Viewer.Core.Diagnostics.CoreLog.F("handled", queue.Handled), Viewer.Core.Diagnostics.CoreLog.F("failed", queue.Failed));
        });
    }

    private FolderPickPanel? _takeFolder;
    private Viewer.Shell.Build.NewPrimTracker? _newPrims;

    /// <summary>Build, Take Into Folder: a window listing the inventory's folders, open until Take or Cancel; the chosen folder receives
    /// the selected objects, one at a time.</summary>
    private void OpenTakeIntoFolder()
    {
        if (_edit is not { Selection.Count: > 0 } || _interactFor?.Inventory is not { } inv || _takeFolder is null || _shell is null)
            return;
        _takeFolder.Begin(inv);
        _shell.Shell.Floaters.Open(FolderPickPanel.FloaterId);
    }

    /// <summary>True while the Take Into Folder window is open.</summary>
    private bool TakeFolderOpen => _takeFolder is not null && (_shell?.Shell.Floaters.IsOpen(FolderPickPanel.FloaterId) ?? false);

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
        if (_edit is not { } e || _shell is null || tool == BuildTool.Land && !EditController.LandToolBuilt)
            return;   // an unbuilt tool does nothing, not even opening the window
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
        _editPanel?.ContentTab.CommitPendingEdit();   // a rename being typed is kept while the item is still selected
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
                HandleMotion(e, m.Position, m.ShiftPressed);
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
    private Control? GridCloseButton() => CloseButtonOf(_gridPanel);

    /// <summary>The colour picker's square of saturation and value: the largest shown control inside it that is about as tall as it is wide
    /// (the engine builds it as an internal child with no name of its own), for the live check's pointer.</summary>
    private static Control? ColourSquareOf(Node picker)
    {
        Control? best = null;
        foreach (var child in picker.GetChildren(true))
        {
            if (child is Control c && c.IsVisibleInTree() && c is not Container && c.Size.X > 40 && c.Size.Y > 40
                && c.Size.X / c.Size.Y is > 0.75f and < 1.33f && (best is null || c.Size.X * c.Size.Y > best.Size.X * best.Size.Y))
                best = c;
            if (ColourSquareOf(child) is { } inner && (best is null || inner.Size.X * inner.Size.Y > best.Size.X * best.Size.Y))
                best = inner;
        }
        return best;
    }

    /// <summary>The close button of the window whose content <paramref name="content"/> is (its frame's), for the automation to click.</summary>
    private static Control? CloseButtonOf(Node? content)
    {
        for (Node? n = content; n is not null; n = n.GetParent())
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
                if (EditController.TryParseTool(text, out var tool)) ChooseTool(tool);
                break;
            case "click":
                if (Screen(p) is { } px) done = e.Click(PickAt(px), shift);
                break;
            case "select":
                e.SelectMany((text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => uint.TryParse(t.Trim(), out var v) ? v : 0).Where(v => v != 0), shift);
                break;
            case "linked": e.EditLinked = text == "on"; break;
            case "copy_selection": e.CopySelection = text == "on"; break;
            case "duplicate": done = e.Duplicate() ?? "sent"; break;
            case "click_action":
                done = Enum.TryParse<Viewer.Core.Interaction.ClickChoice>(text, true, out var choice) ? e.SetClickChoice(choice) is var (sentClick, noticeClick) ? new { sent = sentClick, notice = noticeClick } : null : null;
                break;
            case "sale":
            {
                // "off", or "copy:50", "contents:10", "original:99"
                var parts = (text ?? "off").Split(':');
                bool on = parts[0] != "off";
                var kind = on && Enum.TryParse<Viewer.Core.Building.SaleKind>(parts[0], true, out var k) ? k : Viewer.Core.Building.SaleKind.Copy;
                done = e.SetSale(on, kind, parts.Length > 1 && int.TryParse(parts[1], out var priced) ? priced : null);
                break;
            }
            case "land_impact_refresh": e.RefreshCosts(); break;
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
            // a PBR material asset put on the selected faces by its id (text), or taken off (no text): for a grid whose inventory holds
            // no material item to choose by hand
            case "material": done = Guid.TryParse(text, out var material) ? e.ApplyMaterial(material) : e.RemoveMaterial(); break;
            case "picker": done = _editPanel.TextureTab.Picker is { } pk ? pk.Names : null; break;
            // the picker windows: whether each is open, for the live check
            case "pickers":
                done = new { items = _editPanel.TextureTab.Pickers?.ItemsOpen ?? false, colour = _editPanel.TextureTab.Pickers?.ColoursOpen ?? false, folders = TakeFolderOpen };
                break;
            // a point in the colour picker's square of saturation and value, at the given fractions of its width and height (text "0.75,0.25")
            case "picker_colour_point":
                if (_editPanel.TextureTab.Pickers is { ColoursOpen: true } pc && pc.Colours.Square is { } square)
                {
                    var parts = (text ?? "0.75,0.25").Split(',');
                    float partX = parts.Length == 2 && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var shareX) ? shareX : 0.75f;
                    float partY = parts.Length == 2 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var shareY) ? shareY : 0.25f;
                    var inSquare = square.GetGlobalRect().Position + square.Size * new Vector2(partX, partY);
                    done = new[] { inSquare.X, inSquare.Y };
                }
                break;
            // where a picker window's own close button stands on screen: items, colour or folders
            case "picker_close":
            {
                Node? content = text switch { "items" => _editPanel.TextureTab.Pickers?.Items, "colour" => _editPanel.TextureTab.Pickers?.Colours, "folders" => _takeFolder, _ => null };
                done = CloseButtonOf(content) is { } cb ? new[] { cb.GetGlobalRect().GetCenter().X, cb.GetGlobalRect().GetCenter().Y } : null;
                break;
            }
            // any shown control of the shell by name (Preferences, the folder chooser), a top-bar title by its text, the item of an open
            // top-bar menu that has the keyboard, a row of the inventory window by id, a row of the Content tab or the folder chooser by its text
            case "ui_point": done = UiPoint(text ?? ""); break;
            case "menu_title":
                if (_shell?.TopBar.Menus is { } tm && tm.Titles.FirstOrDefault(t => t.Text == text) is { } title)
                    done = new[] { title.GetGlobalRect().GetCenter().X, title.GetGlobalRect().GetCenter().Y };
                break;
            case "menu_focus":
            {
                // the deepest open menu has the keyboard: a submenu (Build > Scripts) when one is open, else the top menu
                var open = _shell?.TopBar.Menus.Popups.FirstOrDefault(m => m.Visible);
                for (var sub = open; sub is not null; sub = sub.GetChildren().OfType<PopupMenu>().FirstOrDefault(c => c.Visible))
                    open = sub;
                if (open is not null && open.GetFocusedItem() is var fi && fi >= 0)
                    done = open.GetItemText(fi);
                break;
            }
            case "inventory_row":
                if (Guid.TryParse(text, out var rowId) && _inventoryPanel?.TreeView.PointOf(rowId) is { } ip)
                    done = new[] { ip.X, ip.Y };
                break;
            // the Contents list is scrolled into the Build window's view first, so the row's point is on screen
            case "content_row": _editPanel.ScrollTo("contents"); done = ListRow(_editPanel.ContentTab.List as ItemList, text); break;
            // a point on a window's title bar by the window's id (a third of the way along, clear of its buttons), to drag it as a person would
            // ("<id>:close" is its close button instead)
            case "floater_title":
            {
                var parts = (text ?? "").Split(':', 2);
                if (_shell is not null && _shell.Frames.TryGetValue(parts[0], out var frame) && frame.IsVisibleInTree())
                {
                    var tr = parts.Length == 2 && parts[1] == "close" ? frame.CloseButton.GetGlobalRect() : frame.TitleBar.GetGlobalRect();
                    done = parts.Length == 2 ? new[] { tr.GetCenter().X, tr.GetCenter().Y } : new[] { tr.Position.X + tr.Size.X / 3, tr.Position.Y + tr.Size.Y / 2 };
                }
                break;
            }
            case "folder_row": done = TakeFolderOpen ? ListRow(_takeFolder!.List, text) : null; break;
            case "picker_row":
                if (_editPanel.TextureTab.Picker is { } pr && int.TryParse(text, out int row) && row >= 0 && row < pr.List.ItemCount)
                {
                    pr.List.EnsureCurrentIsVisible();
                    var rect = pr.List.GetItemRect(row);
                    // a window of the shell (no window offset); the rectangle is in the list's content, so its scroll is taken off, as in ListRow
                    var at = pr.List.GetGlobalRect().Position + rect.Position + rect.Size / 2 - new Vector2(0, (float)pr.List.GetVScrollBar().Value);
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
            shape = e.ShapeChoice.ToString().ToLowerInvariant(), copy_selection = e.CopySelection, copy_centers = e.CopyCenters, copy_rotates = e.CopyRotates,
            click_choice = e.ClickChoiceShown?.ToString().ToLowerInvariant(), click_enabled = e.ClickChoiceEnabled,
            sale = e.SaleState() is { } sv ? new { for_sale = sv.ForSale, price = sv.Price, type = sv.Type.ToString().ToLowerInvariant(), box = sv.BoxEnabled, price_enabled = sv.PriceEnabled, type_enabled = sv.TypeEnabled } : null,
            land_impact = e.LandImpactShown() is var (impactRoots, impact) ? new { roots = impactRoots, impact } : null,
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
            owned_only = e.SelectOwnedOnly,
            pending = e.Pending,
            contents = e.ContentsKnown ? e.Contents.Select(i => i.Name).ToArray() : null,
            content_status = _editPanel.ContentTab.StatusText,
            features = e.Features() is { } ft ? new
            {
                flexible = ft.Flexible is { } fx ? new[] { fx.Softness, fx.Gravity, fx.Drag, fx.Wind, fx.Tension } : null,
                light = ft.Light is { } lt ? new[] { lt.Intensity, lt.Radius, lt.Falloff } : null,
                physics = new[] { ft.Physics.ShapeType, ft.Physics.Gravity, ft.Physics.Friction, ft.Physics.Density, ft.Physics.Restitution },
                material = ft.Material, can_flex = ft.CanFlex,
            } : null,
            folder_rows = TakeFolderOpen ? _takeFolder!.Paths : null,
            animations_folder = s.Inventory.FolderOfKind(Viewer.Core.Inventory.FolderKind.Animation),
            // the Contents section's properties box and the Build > Scripts window, for the live check (item names only, never their text)
            content_props = _editPanel.ContentTab.Properties.Visible ? _editPanel.ContentTab.PropertyLines : null,
            queue = _queueWindow is { } qw && (_shell?.Shell.Floaters.IsOpen(ScriptQueueWindow.FloaterId) ?? false)
                ? new { text = qw.Text, done = qw.Queue?.Done ?? true, close_enabled = !qw.Close.Disabled } : null,
            notecards_folder = s.Inventory.FolderOfKind(Viewer.Core.Inventory.FolderKind.Notecard),
            notecards_in_folder = s.Inventory.ChildItems(s.Inventory.FolderOfKind(Viewer.Core.Inventory.FolderKind.Notecard)).Count(i => i.Kind == Viewer.Core.Inventory.ItemKind.Notecard),
            notecard = s.Inventory.ChildItems(s.Inventory.FolderOfKind(Viewer.Core.Inventory.FolderKind.Notecard))
                .Where(i => i.Kind == Viewer.Core.Inventory.ItemKind.Notecard).Select(i => new { id = i.Id, name = i.Name }).FirstOrDefault(),
        };
    }

    /// <summary>A shown control of the shell by name, where its middle stands on screen (a spin box's text field; a control in a popup window
    /// placed in that window).</summary>
    private float[]? UiPoint(string name)
    {
        if (_shell?.FindChildren(name, "", true, false).OfType<Control>().FirstOrDefault(c => c.IsVisibleInTree()) is not { } ctl)
            return null;
        if (ctl is SpinBox sb)
            ctl = sb.GetLineEdit();
        var g = ctl.GetGlobalRect().GetCenter();
        if (ctl.GetWindow() is { } win && win != GetWindow())
            g += new Vector2(win.Position.X, win.Position.Y);
        return [g.X, g.Y];
    }

    /// <summary>
    /// The automation's ui command: a control of the shell by its node name (the first one shown, else the first found), how much of it is
    /// in view and where a press lands on it, in the coordinates the input command takes. <paramref name="reveal"/> first scrolls every
    /// scroll area above it to bring it into view, as a person scrolls; the next frame draws the scroll, so the caller reads again with find.
    /// </summary>
    public object? UiFindCall(string name, bool reveal)
    {
        if (_shell is null)
            return null;
        var all = _shell.FindChildren(name, "", true, false).OfType<Control>().ToList();
        if (all.FirstOrDefault(c => c.IsVisibleInTree()) is not { } ctl)
            return new { found = all.Count > 0, shown = false, reach = "outside", point = (float[]?)null, matches = all.Count };
        if (reveal)
            for (Node? n = ctl.GetParent(); n is Control pc; n = n.GetParent())
                if (pc is ScrollContainer sc)
                    sc.EnsureControlVisible(ctl);
        var r = ReachOf(ctl);
        var offset = ctl.GetWindow() is { } win && win != GetWindow() ? new Vector2(win.Position.X, win.Position.Y) : Vector2.Zero;
        var g = ctl.GetGlobalRect();
        return new
        {
            found = true, shown = true, matches = all.Count,
            text = ctl switch { Label l => l.Text, Button b => b.Text, LinkButton k => k.Text, LineEdit e => e.Text, _ => null },   // what the control says (development builds only)
            reach = r.Kind.ToString().ToLowerInvariant(),
            point = r.Point is { } p ? new[] { p.X + offset.X, p.Y + offset.Y } : null,
            rect = new[] { g.Position.X + offset.X, g.Position.Y + offset.Y, g.Size.X, g.Size.Y },
            shown_rect = new[] { r.Shown.X + offset.X, r.Shown.Y + offset.Y, r.Shown.W, r.Shown.H },
        };
    }

    /// <summary>How much of a shown control is in view: its rectangle against every clipping container above it (scroll areas clip)
    /// and the visible part of its viewport, all in the viewport's canvas coordinates (Viewer.Shell.UiReach decides).</summary>
    public static UiReachResult ReachOf(Control ctl)
    {
        static UiRect Of(Rect2 r) => new(r.Position.X, r.Position.Y, r.Size.X, r.Size.Y);
        var clips = new List<UiRect> { Of(ctl.GetViewportRect()) };
        for (Node? n = ctl.GetParent(); n is Control pc; n = n.GetParent())
            if (pc.ClipContents || pc is ScrollContainer)
                clips.Add(Of(pc.GetGlobalRect()));
        return UiReach.Classify(Of(ctl.GetGlobalRect()), clips);
    }

    /// <summary>Where a list's row with this text stands on screen, scrolled into view first; null when there is none.</summary>
    private static float[]? ListRow(ItemList? list, string? text, Window? popup = null)
    {
        if (list is null || !list.IsVisibleInTree())
            return null;
        for (int i = 0; i < list.ItemCount; i++)
            if (list.GetItemText(i) == text)
            {
                list.EnsureCurrentIsVisible();
                var r = list.GetItemRect(i);
                var at = list.GetGlobalRect().Position + r.Position + r.Size / 2 - new Vector2(0, (float)list.GetVScrollBar().Value);
                if (popup is not null)
                    at += new Vector2(popup.Position.X, popup.Position.Y);
                return [at.X, at.Y];
            }
        return null;
    }
}
