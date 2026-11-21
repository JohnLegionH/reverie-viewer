// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Core.Interaction;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// Clicking things in the world in the running viewer: the cursor over objects, the left click (touch, sit, open), the right-click menus,
/// the highlight around the selected object, floating text, the Inspect and About Land windows, the stand button and the seat camera.
/// The decisions are the <see cref="InteractionController"/>'s; this draws them and feeds it the mouse.
/// </summary>
public partial class Main
{
    private InteractionController? _interact;
    private SeatMouseLook? _seatMouseLook;
    private ViewerSession? _interactFor;
    private InteractFeature.Panels? _interactUi;
    private Presentation.HoverTextLayer? _hoverText;
    private Presentation.SelectionOutline? _outline;
    private readonly ConcurrentQueue<TransportEvent> _interactEvents = new();
    private readonly ConcurrentQueue<WorldChange> _textChanges = new();
    private readonly Dictionary<uint, ObjectState> _textObjects = new();
    private readonly PickGeometryCache _pickCache = new();
    private Presentation.IAttachmentFrames? _attachmentFrames;   // where the drawn avatars' attachment points are (the own worn objects are picked there)
    private Vector2 _rightDownAt;
    private bool _rightMoved;
    private bool _inspectingAvatar;
    private Guid _inspectedAvatar;
    private (uint Local, Guid Full)? _seatObject;
    private (System.Numerics.Vector3 Eye, System.Numerics.Vector3 Target)? _aim;

    /// <summary>The cursor shapes the engine has, one for each cursor the interaction rules name.</summary>
    private static Input.CursorShape CursorFor(HoverCursor c) => c switch
    {
        HoverCursor.Touch => Input.CursorShape.PointingHand,
        HoverCursor.Grab => Input.CursorShape.Drag,
        HoverCursor.Sit => Input.CursorShape.Move,
        HoverCursor.Open => Input.CursorShape.Help,
        HoverCursor.Pay or HoverCursor.Buy => Input.CursorShape.CanDrop,
        HoverCursor.Zoom => Input.CursorShape.Cross,
        _ => Input.CursorShape.Arrow,
    };

    private void OnInteractWorldChange(WorldChange c) => _textChanges.Enqueue(c);

    /// <summary>Every frame: attaches the controller to a new session, drops it from an ended one, and brings the cursor, the highlight,
    /// the floating texts and the windows up to date.</summary>
    private void UpdateInteract()
    {
        var session = Model?.Session;
        bool attach = session is not null && session != _interactFor && !session.IsEnded && session.AgentId != Guid.Empty && _shell is not null;
        bool drop = _interactFor is not null && (_interactFor != session || _interactFor.IsEnded);
        if (attach || drop)
        {
            if (_interactFor is not null)
            {
                _interactFor.World.Changed -= OnInteractWorldChange;
                _interactFor.Communication -= _interactEvents.Enqueue;
            }
            _interact = null;
            _interactFor = null;
            _textObjects.Clear();
            _seatObject = null;
            _seatMouseLook = null;
            _interactUi?.Menu.Close();
            ClearInteractLayers();
            if (attach)
            {
                var s = session!;
                if (_interactUi is null)
                {
                    _interactUi = InteractFeature.Install(_shell!);
                    WireTradeButtons(_interactUi);   // once: the buttons outlive a session
                    _shell!.Shell.Commands.Register("floater.about_land", () => _interact?.AboutLandHere());   // World > About Land
                    _interactUi.Menu.Dismissed += () => _interact?.CloseMenu();   // Esc: the menu closed with no choice, so its selection goes
                }
                var strings = _shell!.Shell.Strings;
                var hooks = new SocialHooks(
                    a => { _comms?.Ims.OpenWith(a); _shell.Shell.Floaters.Open(ChatFeature.ConversationsId); },
                    a => _comms?.Friends.Offer(a, ""),
                    a => _travel?.Teleport.OfferTo(a, strings.Get("interact.tp.offer")),
                    a => _travel?.Teleport.AskToBeTeleportedBy(a, strings.Get("interact.tp.request")),
                    () => _travel?.Teleport.SetHome(),
                    Block: (kind, id) => s.Block(kind, id, BlockName(s, kind, id)),
                    Unblock: (kind, id) => s.Unblock(id, BlockName(s, kind, id)),
                    IsBlocked: (kind, id) => s.Mutes.Find(id) is not null);
                _interact = new InteractionController(s.World, s.Actions.Submit, s.AgentId, hooks, a => Friends().Any(f => f.Id == a));
                _interact.ConfirmationRequested += question => _notes?.Model.Post(question);   // Return, Delete and Drop ask in the one shared dialog
                // the camera when a click is made, in the pick ray's frame: a sit request's offset is taken from it as the official viewer does
                _interact.Camera = () =>
                {
                    if (_camera is null) return null;
                    var o = _camera.GlobalPosition;
                    var f = -_camera.GlobalTransform.Basis.Z;
                    return (new System.Numerics.Vector3(o.X, -o.Z, o.Y), new System.Numerics.Vector3(f.X, -f.Z, f.Y));
                };
                // after a sit request the keyboard belongs to the world, so the arrow keys reach the avatar when it stands (lltoolpie.cpp)
                _interact.SitSent += () => GetViewport().GuiReleaseFocus();
                AttachBuild(s, _interact);
                var wornObjects = new Viewer.Shell.Outfit.WornObjects(s.World, s.AgentId, () => System.Environment.TickCount64);
                var outfitActions = new Viewer.Shell.Outfit.OutfitActions(s.Inventory, s.Actions.Submit, wornObjects.Items, RlvCanDetach);
                _interact.SelfHooks = new SelfHooks(
                    () => _shell.Shell.Floaters.Open(AppearanceFeature.FloaterId),   // the Appearance window holds the outfit window's panel as its Outfit tab
                    () => _shell.Shell.Floaters.Open(InventoryFeature.FloaterId),
                    () => Viewer.Shell.Outfit.WornList.Of(s.Inventory),
                    id => outfitActions.TakeOff(id),
                    () => _driver?.Input.Flying ?? false,
                    () => _driver?.Input.Press(Viewer.Motion.MoveCommand.ToggleFly, _cameraClock.Elapsed.TotalSeconds, true),
                    Detach: id => outfitActions.Detach(id),
                    Attachments: () => WornAttachments(s, wornObjects),
                    DetachAll: () => outfitActions.DetachAll(),
                    TakeOffAllClothes: () => outfitActions.TakeOffAllClothes(),
                    CanDetachItem: RlvCanDetach);
                _seatMouseLook = new SeatMouseLook(_mouseLook);
                _tradeMessage = null;
                _interact.OpenRequested += _ => { _tradeMessage = null; _shell.Shell.Floaters.Open(InteractFeature.ContentsId); RefreshInteractWindows(); };
                _interact.BuyOffered += _ => { _buyMessage = null; _shell.Shell.Floaters.Open(InteractFeature.BuyId); RefreshInteractWindows(); };
                _interact.BuyRefused += () => { _buyMessage = "buy.not_for_sale"; _shell.Shell.Floaters.Open(InteractFeature.BuyId); RefreshInteractWindows(); };
                _interact.PayOffered += o => { _interactUi.Pay.Show(o); _shell.Shell.Floaters.Open(InteractFeature.PayId); };
                _interact.EntriesChanged += () => { if (_interactUi.Menu.IsOpen) _interactUi.Menu.Update(_interact.Entries); };
                // Copy and Wear: the new folder's items are worn once the whole set has reached the inventory
                _interact.ItemsInFolder = f => s.Inventory.LoadState(f) == Viewer.Core.Inventory.FolderLoad.Loaded ? s.Inventory.ChildItems(f).Count : (s.Inventory.EnsureLoaded(f) ? 0 : 0);
                _interact.WearFolder = f => outfitActions.WearOutfit(f);
                _interact.ZoomRequested += ZoomOnObject;
                _interact.InspectRequested += _ => { _inspectingAvatar = false; _shell.Shell.Floaters.Open(InteractFeature.InspectId); RefreshInteractWindows(); };
                _interact.AvatarInspectRequested += a => { _inspectingAvatar = true; _inspectedAvatar = a; _shell.Shell.Floaters.Open(InteractFeature.InspectId); RefreshInteractWindows(); };
                _interact.AppearanceRequested += () => _shell.Shell.Floaters.Open(AppearanceFeature.FloaterId);
                _interact.AboutLandRequested += () => { _shell.Shell.Floaters.Open(InteractFeature.LandId); RefreshInteractWindows(); };
                _interact.BuildRequested += () => _shell.Shell.Commands.Execute(Viewer.Shell.BuildMenu.CreateCommand);
                _interact.Changed += RefreshInteractWindows;
                _interactUi.Stand.Pressed += () => _interact?.Stand();
                _interactUi.StopFlying.Pressed += () => _interact?.StopFlying();
                s.World.Changed += OnInteractWorldChange;
                s.Communication += _interactEvents.Enqueue;
                _interactFor = s;
            }
        }
        if (_interact is not { } ic || _interactFor is not { } sess)
            return;
        while (_interactEvents.TryDequeue(out var e))
        {
            if (e is ObjectPropertiesReceived named) _objectNames[named.ObjectId] = named.Name;   // (the development probe reads these)
            ic.Receive(e);
            _edit?.Receive(e);
            _scriptQueue?.Receive(e);
        }
        while (_textChanges.TryDequeue(out var c))
        {
            TrackText(sess.World, c);
            if (c.Kind == WorldChangeKind.ObjectAdded && sess.World.Object(c.Id) is { } added)
            {
                if (_newPrims is { Waiting: > 0 } made && made.Arrived(added))
                    _edit?.Created(added.Id.LocalId);   // the new box becomes the selection (it arrived made selected)
                else
                    _edit?.CopyArrived(added);          // a copy Duplicate asked for joins the selection
            }
        }
        ic.Tick();
        _seatMouseLook?.Update(ic.Sitting, ic.Seat is { ForceMouselook: true });   // a seat that asks for mouse-look puts the view into it
        _interactUi!.Stand.Visible = ic.Sitting;
        _interactUi.StopFlying.Visible = ic.StopFlyingVisible(_mouseLook.Active);
        if (_stage is not null && _camera is not null)
        {
            UpdateOutline(ic, sess.World);
            UpdateHandles();
            UpdateHoverText(sess.World);
        }
        if (!_interactUi.Menu.IsOpen && ic.Menu != MenuKind.None)
            ic.CloseMenu();
        Input.SetDefaultCursorShape(ic.Placing ? Input.CursorShape.Cross : CursorFor(ic.Cursor));
        _build?.Drop.Track(GetViewport());
    }

    private string? _tradeMessage, _buyMessage;

    /// <summary>Connects the Contents, Buy and Pay buttons to the controller of the current session. Called once, when the windows are made.</summary>
    private void WireTradeButtons(InteractFeature.Panels ui)
    {
        ui.Contents.Copy.Pressed += () => PressCopy(false);
        ui.Contents.CopyWear.Pressed += () => PressCopy(true);
        ui.Buy.Buy.Pressed += () => { _interact?.ConfirmBuy(); _shell?.Shell.Floaters.Close(InteractFeature.BuyId); };
        ui.Buy.Cancel.Pressed += () => { _interact?.CancelBuy(); _shell?.Shell.Floaters.Close(InteractFeature.BuyId); };
        ui.Pay.PayPressed += amount => { _interact?.ConfirmPay(amount); _shell?.Shell.Floaters.Close(InteractFeature.PayId); };
    }

    /// <summary>Copy To Inventory or Copy and Wear: copies when the items may be copied, otherwise says why not.</summary>
    private void PressCopy(bool wear)
    {
        if (_interact is not { } ic)
            return;
        if (ic.CopyContents(wear))
        {
            _shell?.Shell.Floaters.Close(InteractFeature.ContentsId);   // the official window closes after copying
            return;
        }
        _tradeMessage = "contents.cannot_copy";
        RefreshInteractWindows();
    }

    /// <summary>The name a block entry carries: a resident's from the name cache, an object's from its properties or the world.</summary>
    private string BlockName(ViewerSession s, MuteKind kind, Guid id)
    {
        if (kind == MuteKind.Resident)
            return s.Names.Get(id) ?? "";
        if (_interact?.Properties is { } p && p.ObjectId == id && p.Name.Length > 0)
            return p.Name;
        return s.Mutes.Find(id)?.Name ?? "";
    }

    private void TrackText(WorldState world, WorldChange c)
    {
        switch (c.Kind)
        {
            case WorldChangeKind.RegionReset:
                _textObjects.Clear();
                break;
            case WorldChangeKind.ObjectRemoved:
                _textObjects.Remove(c.Id.LocalId);
                break;
            case WorldChangeKind.ObjectAdded or WorldChangeKind.ObjectChanged:
                if (world.Object(c.Id) is { Interaction.HoverText.Length: > 0 } o)
                    _textObjects[c.Id.LocalId] = o;
                else
                    _textObjects.Remove(c.Id.LocalId);
                break;
        }
    }

    private void ClearInteractLayers()
    {
        _hoverText?.Update([], Vector3.Zero, Vector3.Forward);
        _outline?.Update([], Vector3.Zero, 60f);
        _faceHighlight?.Update([]);
    }

    private Presentation.FaceHighlight? _faceHighlight;

    /// <summary>Select Face: the chosen faces get the face marker, and their objects no whole-object outline (the official viewer outlines
    /// only what is selected, and in Select Face that is faces). Returns the objects whose faces are marked.</summary>
    private HashSet<uint> UpdateFaceHighlight(WorldState world)
    {
        if (_faceHighlight is null)
        {
            _faceHighlight = new Presentation.FaceHighlight { Name = "face-highlight" };
            _stage!.AddChild(_faceHighlight);
        }
        var marked = new List<Presentation.HighlightedFaces>();
        if (_edit is { BuildMode: true, SelectFaces: true } e)
            foreach (var group in e.SelectedFaces.GroupBy(f => f.Local))
                if (world.Object(new ObjectId(world.Epoch, group.Key)) is { } o && ObjectPicker.WorldPose(world, o, out var pos, out var rot))
                    marked.Add(new Presentation.HighlightedFaces(new Presentation.OutlinedPrim(o, pos, rot, o.ParentLocalId == 0), group.Select(f => f.Face).ToHashSet()));
        _faceHighlight.Update(marked);
        return marked.Select(m => m.Prim.Prim.Id.LocalId).ToHashSet();
    }

    private void UpdateHoverText(WorldState world)
    {
        if (_hoverText is null)
        {
            _hoverText = new Presentation.HoverTextLayer { Name = "hover-text" };
            _stage!.AddChild(_hoverText);
        }
        var texts = new List<Presentation.FloatingText>(_textObjects.Count);
        foreach (var o in _textObjects.Values)
            if (ObjectPicker.WorldPose(world, o, out var pos, out _))
                texts.Add(new Presentation.FloatingText(o.Id.LocalId, HoverTextRules.Anchor(pos, o.Scale.Z), o.Interaction!));
        _hoverText.Update(texts, _camera!.GlobalPosition, -_camera.GlobalTransform.Basis.Z);
    }

    private void UpdateOutline(InteractionController ic, WorldState world)
    {
        if (_outline is null)
        {
            _outline = new Presentation.SelectionOutline { Name = "selection-outline" };
            _stage!.AddChild(_outline);
        }
        var prims = new List<Presentation.OutlinedPrim>();
        var faceMarked = UpdateFaceHighlight(world);
        foreach (var (editRoot, highlight) in EditOutlined(world))
            foreach (var o in new[] { editRoot }.Concat(world.Linkset(editRoot.Id)))
                if (!faceMarked.Contains(highlight.LocalId) && ObjectPicker.WorldPose(world, o, out var pos, out var rot))
                    prims.Add(new Presentation.OutlinedPrim(o, pos, rot, o.Id == highlight));
        if (prims.Count == 0 && ic.Selected is { } sel && world.Object(sel) is { } root)
        {
            foreach (var o in new[] { root }.Concat(world.Linkset(sel)))
                if (ObjectPicker.WorldPose(world, o, out var pos, out var rot))
                    prims.Add(new Presentation.OutlinedPrim(o, pos, rot, o.Id == sel));
        }
        _outline.Update(prims, _camera!.GlobalPosition, _camera.Fov);
    }

    /// <summary>The Inspect and About Land windows show what the controller holds.</summary>
    private void RefreshInteractWindows()
    {
        if (_interactUi is null || _interact is not { } ic || _interactFor is not { } s || _shell is null)
            return;
        string Who(Guid id) => id == Guid.Empty ? "-" : s.Names.Get(id) ?? id.ToString();
        if (_inspectingAvatar)
            _interactUi.Inspect.Set([("inspect.name", Who(_inspectedAvatar)), ("inspect.id", _inspectedAvatar.ToString())]);
        else if (ic.Properties is { } p)
            _interactUi.Inspect.Set([("inspect.name", p.Name), ("inspect.description", p.Description), ("inspect.owner", Who(p.OwnerId)), ("inspect.creator", Who(p.CreatorId))]);
        else
            _interactUi.Inspect.Set([("inspect.name", "inspect.loading")]);
        _interactUi.Contents.Show(ic.Contents, ic.CanCopyContents, _tradeMessage);
        _interactUi.Buy.Show(ic.Offer, _buyMessage, ic.Contents);
        if (ic.Parcel is { } parcel)
            _interactUi.Land.Set(LandInfo.Rows(parcel, id => s.Names.Get(id)).Select(r => (r.LabelKey, r.Value)));
        else
            _interactUi.Land.Set([("land.name", "land.loading")]);
    }

    // ---------------- the mouse ----------------

    /// <summary>What is under a point of the view, in region terms.</summary>
    private PickResult PickAt(Vector2 screen, bool includeSelf = false)
    {
        if (_camera is null || _interactFor is not { } s)
            return new PickResult(PickKind.None, null, null, default, 0f);
        var o = _camera.ProjectRayOrigin(screen);
        var d = _camera.ProjectRayNormal(screen);
        var ray = new PickRay(new System.Numerics.Vector3(o.X, -o.Z, o.Y), new System.Numerics.Vector3(d.X, -d.Z, d.Y));
        var world = s.World;
        // a right click may land on the own avatar (its own menu); the objects the own avatar wears are found where the avatar's skeleton puts them
        return ObjectPicker.Pick(world, ray, (x, y) => Presentation.BanWallLayer.GroundFrom(world, x, y), MathF.Max(256f, s.DrawDistance), includeSelf ? null : s.AgentId, _pickCache,
            o => OwnAttachmentFrame(s.AgentId, o),
            skip: o => o.Interaction?.ClickAction == Viewer.Core.Interaction.ClickAction.Ignore && _edit is not { BuildMode: true });
    }

    /// <summary>The objects the own avatar wears that the outfit does not list (one put on from the ground has no link in the current outfit),
    /// by the name the object states; the Take Off menu offers them under Detach.</summary>
    private static IReadOnlyList<WornEntry> WornAttachments(Viewer.Core.Session.ViewerSession s, Viewer.Shell.Outfit.WornObjects worn)
    {
        var listed = Viewer.Core.Inventory.OutfitPlanner.Current(s.Inventory).Select(e => e.TargetId).ToHashSet();
        return worn.Items().Where(id => !listed.Contains(id)).Select(id => new WornEntry(id, s.Inventory.Item(id)?.Name ?? id.ToString())).ToArray();
    }

    /// <summary>The frame of the attachment point an object of the own avatar is worn on, as the avatar is drawn now; null for anything else.</summary>
    private System.Numerics.Matrix4x4? OwnAttachmentFrame(Guid self, ObjectState o) =>
        o.WearerAgentId == self && o.AttachmentPoint is { } point && _attachmentFrames is { } frames && frames.TryPoint(o.ParentLocalId, point, out var m) ? m : null;

    private bool OverWorld() => GetViewport().GuiGetHoveredControl() is null;

    /// <summary>Handles a mouse event for the world. True when it was used.</summary>
    private bool InteractInput(InputEvent e)
    {
        if (_interact is not { } ic || _interactUi is null || _camera is null)
            return false;
        if (EditInput(e))
            return true;
        switch (e)
        {
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape } when ic.Placing:
                ic.CancelCreate();
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } b when ic.Placing:
                PlaceBox(ic, b.Position);
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } b:
                if (b.Pressed)
                {
                    _interactUi.Menu.Close();
                    ic.CloseMenu();
                    ic.LeftDown(PickAt(b.Position));
                }
                else
                    ic.LeftUp(PickAt(b.Position));
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right } b:
                if (b.Pressed)
                {
                    _interactUi.Menu.Close();
                    ic.CloseMenu();
                    _rightDownAt = b.Position;
                    _rightMoved = false;
                }
                else if (!_rightMoved)
                    OpenContextMenu(b.Position);
                return false;   // the camera's own handling of the right button goes on
            case InputEventMouseMotion m:
                if (_orbiting && (m.Position - _rightDownAt).Length() > 4f)
                    _rightMoved = true;
                else if (!_orbiting && OverWorld())
                    ic.Hover(PickAt(m.Position));
                return false;
        }
        return false;
    }

    private void OpenContextMenu(Vector2 at)
    {
        if (_interact is not { } ic || _interactUi is null)
            return;
        ic.RightClick(PickAt(at, includeSelf: true));
        if (ic.Entries.Count == 0)
            return;
        _interactUi.Menu.Open(at, ic.Entries, id => { ic.Choose(id); ic.CloseMenu(); }, GetViewport().GetVisibleRect().Size);
    }

    // ---------------- the seat camera ----------------

    /// <summary>The camera a seat asks for (region coordinates), or null when none is in force.</summary>
    private (System.Numerics.Vector3 Eye, System.Numerics.Vector3 Target)? SeatCameraPose(System.Numerics.Vector3 soles, float yaw, float height)
    {
        if (_aim is { } aim)
            return aim;
        if (_interact is not { Seat: { } seat, Sitting: true } || _interactFor is not { } s)
            return null;
        if (seat.ForceMouselook)
        {
            var eye = soles + new System.Numerics.Vector3(0, 0, height * 0.95f);
            return (eye, eye + new System.Numerics.Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0f));
        }
        if (_seatObject is not { } known || known.Full != seat.ObjectId || s.World.Object(new ObjectId(s.World.Epoch, known.Local)) is not { } o || o.FullId != seat.ObjectId)
        {
            var found = s.World.Snapshot().Objects.Values.FirstOrDefault(x => x.FullId == seat.ObjectId);
            if (found is null)
                return null;
            _seatObject = (found.Id.LocalId, found.FullId);
            o = found;
        }
        if (!ObjectPicker.WorldPose(s.World, o, out var pos, out var rot) || SitCamera.Compute(pos, rot, seat.EyeOffset, seat.AtOffset) is not { } cam)
            return null;
        return cam;
    }

#if NV_AUTOMATION
    /// <summary>The interaction command (development builds only): aim the camera, hover, click or right-click an object, a person or
    /// a spot of ground through the same picking the mouse uses, choose a menu entry, stand, and report the state. Answers ids, flags,
    /// keys and counts; a name is only ever compared (<c>expect</c>), never returned.</summary>
    public object? InteractCall(string action, Guid id, float x, float y, float z, string? entry, string? expect, float distance)
    {
        if (_interact is not { } ic || _interactFor is not { } s || _camera is null || _interactUi is null)
            return null;
        _interact.Tick();
        var world = s.World;
        System.Numerics.Vector3? Where()
        {
            if (id != Guid.Empty)
            {
                var o = world.Snapshot().Objects.Values.FirstOrDefault(v => v.FullId == id);
                if (o is not null && ObjectPicker.WorldPose(world, o, out var pos, out _))
                    return pos;
                if (o is not null && OwnAttachmentFrame(s.AgentId, o) is { } frame)   // an object the own avatar wears is where its attachment point puts it
                    return (Viewer.Core.Avatars.AttachmentPlacement.ObjectInPoint(o.Position, o.Rotation) * frame).Translation;
                var a = world.FindAvatar(id);
                if (a is not null)
                    return a.Position;
                return null;
            }
            return new System.Numerics.Vector3(x, y, z);
        }
        Vector2? Screen(System.Numerics.Vector3 p)
        {
            var engine = new Vector3(p.X, p.Z, -p.Y);
            return _camera.IsPositionBehind(engine) ? null : _camera.UnprojectPosition(engine);
        }
        object Describe(PickResult r) => new
        {
            kind = r.Kind.ToString(), local = r.Hit?.LocalId ?? 0u, root = r.Hit?.RootLocalId ?? 0u, face = r.Hit?.Face ?? -1,
            u = r.Hit?.Uv.X ?? -1f, v = r.Hit?.Uv.Y ?? -1f, s = r.Hit?.St.X ?? -1f, t = r.Hit?.St.Y ?? -1f,
            avatar = r.Avatar?.AgentId.ToString(), distance = r.Distance,
        };
        switch (action)
        {
            case "aim":   // {x,y,z, distance}: the camera looks at the point from this far away, a little above, until aim_off
            {
                if (distance <= 0f) { _aim = null; return new { aimed = false }; }
                var target = new System.Numerics.Vector3(x, y, z);
                var me = world.FindAvatar(s.AgentId);
                var from = me is null ? target + new System.Numerics.Vector3(distance, 0, 0) : target + System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(me.Position.X - x, me.Position.Y - y, 0) + new System.Numerics.Vector3(0.001f, 0, 0)) * distance;
                _aim = (from + new System.Numerics.Vector3(0, 0, distance * 0.35f), target);
                return new { aimed = true };
            }
            case "hover" or "click" or "rightclick":
            {
                if (Where() is not { } at || Screen(at) is not { } px)
                    return new { error = "not_in_view" };
                var pick = PickAt(px, includeSelf: action == "rightclick");
                switch (action)
                {
                    case "hover": ic.Hover(pick); break;
                    case "click": ic.LeftDown(pick); ic.LeftUp(pick); break;
                    default:
                        ic.RightClick(pick);
                        if (ic.Entries.Count > 0)
                            _interactUi.Menu.Open(px, ic.Entries, mid => { ic.Choose(mid); ic.CloseMenu(); }, GetViewport().GetVisibleRect().Size);
                        break;
                }
                return new { screen_x = px.X, screen_y = px.Y, pick = Describe(pick), cursor = ic.Cursor.ToString(), menu = ic.Menu.ToString(), entries = ic.Entries.Select(e2 => new { e2.Id, e2.Enabled }).ToArray() };
            }
            case "choose":
            {
                bool ok = ic.Choose(entry ?? "");
                _interactUi.Menu.Close();
                ic.CloseMenu();
                return new { chosen = ok };
            }
            case "stand":
                ic.Stand();
                return new { sent = true };
            case "press_stop_flying":   // the Stop Flying button itself is pressed, so its own wiring runs
                if (!_interactUi.StopFlying.Visible)
                    return new { sent = false };
                _interactUi.StopFlying.EmitSignal(BaseButton.SignalName.Pressed);
                return new { sent = true };
            case "scan":   // {distance}: the objects within reach of the own avatar whose root has contents or whose click action is Buy (ids, flags and distances only)
            {
                var me2 = world.FindAvatar(s.AgentId);
                if (me2 is null)
                    return new { error = "no_avatar" };
                float limit = distance > 0f ? distance : 60f;
                var found = world.Snapshot().Objects.Values
                    .Where(o => o.ParentLocalId == 0 && o.Interaction is { } i && ((i.Flags & ObjectFlagBits.InventoryEmpty) == 0 || i.ClickAction == ClickAction.Buy) && ObjectPicker.WorldPose(world, o, out _, out _))
                    .Select(o => (o, d: System.Numerics.Vector3.Distance(o.Position, me2.Position)))
                    .Where(t => t.d <= limit).OrderBy(t => t.d).Take(40)
                    .Select(t => new { id = t.o.FullId, local = t.o.Id.LocalId, flags = t.o.Interaction!.Flags, click_action = t.o.Interaction.ClickAction, distance = t.d, x = t.o.Position.X, y = t.o.Position.Y, z = t.o.Position.Z })
                    .ToArray();
                return new { count = found.Length, objects = found };
            }
            case "press_copy":   // the Contents window's own buttons ("wear" in entry: Copy and Wear)
            {
                var button = entry == "wear" ? _interactUi.Contents.CopyWear : _interactUi.Contents.Copy;
                if (button.Disabled)
                    return new { sent = false };
                button.EmitSignal(BaseButton.SignalName.Pressed);
                return new { sent = true };
            }
            case "press_buy":   // the Buy window's own button
                if (_interactUi.Buy.Buy.Disabled)
                    return new { sent = false };
                _interactUi.Buy.Buy.EmitSignal(BaseButton.SignalName.Pressed);
                return new { sent = true };
            case "sit_ground":   // what the land menu's Sit Here sends, without needing a land point in reach
                ic.SitOnGround();
                return new { sent = true };
            case "state":
            {
                var p = ic.Properties;
                var parcel = ic.Parcel;
                return new
                {
                    cursor = ic.Cursor.ToString(), menu = ic.Menu.ToString(), menu_open = _interactUi.Menu.IsOpen, entries = ic.Entries.Select(e2 => e2.Id).ToArray(),
                    selected = ic.Selected is not null, sitting = ic.Sitting, stand_button = _interactUi.Stand.Visible, stop_flying_button = _interactUi.StopFlying.Visible, unhandled_clicks = ic.UnhandledClicks,
                    seat = ic.Seat is { } seat ? new { camera = true, mouselook = seat.ForceMouselook, seat_object = seat.ObjectId } : null,
                    text_objects = _textObjects.Count, text_shown = _hoverText?.Shown ?? 0, outline_pieces = _outline?.Pieces ?? 0,
                    properties = p is null ? null : new
                    {
                        name_ok = expect is null ? (bool?)null : p.Name == expect, owner_is_me = p.OwnerId == s.AgentId, creator_is_me = p.CreatorId == s.AgentId,
                        has_description = p.Description.Length > 0, object_id = p.ObjectId,
                        sale_type = ObjectSale.TypeOf(p), price = p.SalePrice, can_buy = ObjectSale.CanBuy(p), can_copy_me = (p.OwnerMask & PermissionBits.Copy) != 0,
                    },
                    parcel = parcel is null ? null : new
                    {
                        owner_is_me = parcel.OwnerId == s.AgentId, area = parcel.Area, use_access_list = parcel.UseAccessList, use_ban_list = parcel.UseBanList,
                        group_only = parcel.GroupAccessOnly, name_ok = expect is null ? (bool?)null : parcel.Name == expect,
                    },
                    contents = new
                    {
                        loaded = ic.Contents?.Loaded ?? false, failed = ic.Contents?.Failed ?? false, items = ic.Contents?.Items?.Count ?? 0,
                        can_copy = ic.CanCopyContents, window_rows = _interactUi.Contents.Texts().Count, status = _interactUi.Contents.StatusText,
                        copy_enabled = !_interactUi.Contents.Copy.Disabled, wear_enabled = !_interactUi.Contents.CopyWear.Disabled,
                    },
                    copy = ic.LastCopy is not { } lc ? null : new
                    {
                        folder_known = s.Inventory.Folder(lc.FolderId) is not null,
                        name_matches = s.Inventory.Folder(lc.FolderId) is { } lf && p is not null && string.Join(' ', lf.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)) == string.Join(' ', p.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
                        items_in_folder = s.Inventory.ChildItems(lc.FolderId).Count, items_sent = lc.ItemCount, wear = lc.Wear,
                    },
                    buy = new
                    {
                        offered = ic.Offer is not null, sale_type = ic.Offer?.SaleType ?? 0, price = ic.Offer?.Price ?? -1,
                        window_rows = _interactUi.Buy.Rows.Texts().Count, items = _interactUi.Buy.Items.Texts().Count, status = _interactUi.Buy.StatusText,
                        buy_enabled = !_interactUi.Buy.Buy.Disabled,
                    },
                    inspect_rows = _interactUi.Inspect.Texts().Count, land_rows = _interactUi.Land.Texts().Count,
                };
            }
        }
        return new { error = "unknown action" };
    }
#endif
}
