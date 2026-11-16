// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Avatars;
using Viewer.Core.Interaction;
using Viewer.Core.Session;
using Viewer.Core.World;

namespace Viewer.Shell;

/// <summary>What choosing a person-menu entry does: the viewer's existing instant messages, friendship offers and teleports.</summary>
public sealed record SocialHooks(Action<Guid> InstantMessage, Action<Guid> AddFriend, Action<Guid> OfferTeleport, Action<Guid> RequestTeleport, Action SetHome,
    Action<MuteKind, Guid>? Block = null, Action<MuteKind, Guid>? Unblock = null, Func<MuteKind, Guid, bool>? IsBlocked = null);

/// <summary>What choosing an entry of the own avatar's menu does.</summary>
public sealed record SelfHooks(Action OpenAppearance, Action OpenInventory, Func<IReadOnlyList<WornEntry>> Worn, Action<Guid> TakeOff, Func<bool> IsFlying, Action ToggleFly,
    Action<Guid>? Detach = null, Func<IReadOnlyList<WornEntry>>? Attachments = null, Action? DetachAll = null, Action? TakeOffAllClothes = null,
    Func<Guid, bool>? CanDetachItem = null);

public enum MenuKind { None, Object, Avatar, Land, Self }

/// <summary>The items inside the object whose Contents window is open: not loaded while they are being read, or failed when the region did not answer.</summary>
public sealed record ObjectContentsView(Guid ObjectId, uint LocalId, IReadOnlyList<TaskItem>? Items, bool Failed)
{
    public bool Loaded => Items is not null;
}

/// <summary>A sale waiting for the buyer's answer: what is offered, its type (1 original, 2 copy, 3 contents) and price.</summary>
public sealed record BuyOffer(Guid ObjectId, uint LocalId, string Name, byte SaleType, int Price, bool CanBuy);

/// <summary>An object that takes money, with the prices it asked for.</summary>
public sealed record PayOffer(Guid ObjectId, uint LocalId, string Name, int DefaultPrice, IReadOnlyList<int> Buttons);

/// <summary>The seat camera in force: the object it hangs from, the offsets from it and whether the seat forces first-person view.</summary>
public sealed record SeatCameraState(Guid ObjectId, Vector3 EyeOffset, Vector3 AtOffset, bool ForceMouselook);

/// <summary>
/// Clicking things in the world: the cursor the mouse earns, what a left click does (touch, sit, open), the right-click menus and what
/// each entry runs, the selected object and the answers that arrive for it (properties, parcel, seat). Every action goes through the
/// action pipeline (<c>submit</c>). Behaviour after the official viewer's tool for clicking in the world (see the behaviour spec).
/// Used from the main thread only.
/// </summary>
public sealed class InteractionController
{
    private readonly WorldState _world;
    private readonly Func<ActionIntent, ActionResult> _submit;
    private readonly Guid _self;
    private readonly SocialHooks _social;
    private readonly Func<Guid, bool> _isFriend;
    private PickResult? _menuPick;
    private (Vector3 Origin, Vector3 At)? _menuCamera;   // the camera when the menu's pick was made (a sit's point is worked out from it)
    /// <summary>The ids of the own avatar's entries appended to an own worn object's menu (they are chosen as the own avatar's menu's).</summary>
    private HashSet<string> _selfPart = [];
    private (uint Local, TouchSurface Surface)? _grab;
    private bool _wasSitting;

    public InteractionController(WorldState world, Func<ActionIntent, ActionResult> submit, Guid selfAgent, SocialHooks social, Func<Guid, bool> isFriend)
    {
        _world = world;
        _submit = submit;
        _self = selfAgent;
        _social = social;
        _isFriend = isFriend;
    }

    /// <summary>Plays an interface sound (the object sounds of creating, rezzing, taking and deleting).</summary>
    public Action<global::Viewer.Shell.Sound.UiSoundSlot>? PlaySound { get; set; }

    /// <summary>The new-prim settings Create uses (Preferences, Building); the default box when not given.</summary>
    public Func<Viewer.Core.Building.NewPrimDefaults>? NewPrim { get; set; }

    /// <summary>Build, Create was chosen: the next left click in the world places the default box.</summary>
    public bool Placing { get; private set; }

    public void BeginCreate()
    {
        Placing = true;
        CloseMenu();
        Changed?.Invoke();
    }

    public void CancelCreate()
    {
        if (!Placing)
            return;
        Placing = false;
        Changed?.Invoke();
    }

    /// <summary>The click of the create tool: places the default box where the click points (lltoolplacer.cpp, read as behaviour). A click that
    /// cannot place (nothing there, an avatar, too far) leaves the tool waiting for another. True when the box was sent.</summary>
    public bool Place(PickResult pick, Vector3 cameraPosition, Vector3 cameraAt, Vector3 rayDirection, float nearClip)
    {
        if (!Placing || Viewer.Core.Building.BuildRules.CreatePlacement(pick, cameraPosition, cameraAt, rayDirection, nearClip) is not { } placement)
            return false;
        if (_submit(new CreateBoxIntent(placement)).Status != ActionStatus.Sent)
            return false;
        Placing = false;
        PlaySound?.Invoke(global::Viewer.Shell.Sound.UiSoundSlot.ObjectCreate);
        Changed?.Invoke();
        return true;
    }

    /// <summary>An inventory object dropped on the world: rezzes it where the drop points (lltooldraganddrop.cpp, read as behaviour). True when sent.</summary>
    public bool RezDrop(Guid itemId, PickResult pick, Vector3 cameraPosition)
    {
        if (Viewer.Core.Building.BuildRules.RezPlacement(pick, cameraPosition) is not { } placement)
            return false;
        if (_submit(new RezItemIntent(itemId, placement)).Status != ActionStatus.Sent)
            return false;
        PlaySound?.Invoke(global::Viewer.Shell.Sound.UiSoundSlot.ObjectRezIn);
        return true;
    }

    public HoverCursor Cursor { get; private set; }
    /// <summary>The right-click selection. When it moves to another object or goes, the region is told (ObjectDeselect), as the official
    /// viewer's selection manager does, so a selected physical object does not stay held still.</summary>
    public ObjectId? Selected
    {
        get => _selected;
        private set
        {
            if (_selected is { } old && old != value)
                _submit(new DeselectObjectsIntent([old.LocalId]));
            _selected = value;
        }
    }
    private ObjectId? _selected;
    public MenuKind Menu { get; private set; }
    public IReadOnlyList<ContextEntry> Entries { get; private set; } = [];
    public ObjectPropertiesReceived? Properties { get; private set; }
    public ParcelDetailsReceived? Parcel { get; private set; }
    public SeatCameraState? Seat { get; private set; }

    /// <summary>What the own avatar's menu does; null: the own avatar has no menu.</summary>
    public SelfHooks? SelfHooks { get; set; }

    /// <summary>Left clicks on objects whose action this viewer does not run (buy, pay, media, zoom).</summary>
    public int UnhandledClicks { get; private set; }

    /// <summary>The object whose Contents window is open, and what is known of its items.</summary>
    public ObjectContentsView? Contents { get; private set; } = new(Guid.Empty, 0, null, false);

    /// <summary>The sale waiting for an answer, if any.</summary>
    public BuyOffer? Offer { get; private set; }

    /// <summary>The pay window's content, if any.</summary>
    public PayOffer? PayWindow { get; private set; }

    /// <summary>The last copy of contents the region confirmed we sent (the new folder and how many items went into it).</summary>
    public ContentsCopied? LastCopy { get; private set; }

    /// <summary>How many items an inventory folder holds now (the host gives the inventory's answer).</summary>
    public Func<Guid, int>? ItemsInFolder { get; set; }

    /// <summary>Wears everything in an inventory folder (the host gives the outfit action).</summary>
    public Action<Guid>? WearFolder { get; set; }

    private ObjectId? _pendingBuy, _pendingPay;
    private (Guid Folder, int Count)? _pendingWear;

    /// <summary>The window can offer Copy To Inventory: the items are read and the object's name (the new folder's name) is known.</summary>
    public bool CanCopyContents =>
        Contents is { Loaded: true, Failed: false, Items.Count: > 0 } c && Properties is { } p && p.ObjectId == c.ObjectId && p.Name.Trim().Length > 0;

    /// <summary>Copies the object's items into a new folder named after the object (the clothing folder's child when <paramref name="wear"/>,
    /// else the inventory root; the items are worn once they arrive). False when nothing was sent: not ready, or the items may not be copied.</summary>
    public bool CopyContents(bool wear)
    {
        if (!CanCopyContents || Contents is not { } c || Properties is not { } p)
            return false;
        var items = c.Items!;
        bool owned = ObjectByFullId(c.ObjectId) is { Interaction: { } i } && (i.Flags & ObjectFlagBits.YouOwner) != 0;
        if (!ObjectContents.Plan(items, _self, owned).Accepted)
            return false;
        return _submit(new CopyContentsIntent(c.LocalId, c.ObjectId, p.Name, wear, items.Select(x => x.ItemId).ToArray())).Status == ActionStatus.Sent;
    }

    private ObjectState? ObjectByFullId(Guid id) => _world.Snapshot().Objects.Values.FirstOrDefault(o => o.FullId == id);

    /// <summary>The buyer said yes: sends the purchase at the price shown (a price of zero is sent as zero).</summary>
    public bool ConfirmBuy()
    {
        if (Offer is not { CanBuy: true } offer)
            return false;
        Offer = null;
        _pendingBuy = null;
        bool sent = _submit(new BuyObjectIntent(offer.LocalId, offer.SaleType, offer.Price)).Status == ActionStatus.Sent;
        Changed?.Invoke();
        return sent;
    }

    public void CancelBuy()
    {
        if (Offer is null && _pendingBuy is null)
            return;
        Offer = null;
        _pendingBuy = null;
        Changed?.Invoke();
    }

    public bool ConfirmPay(int amount)
    {
        if (PayWindow is not { } offer)
            return false;
        PayWindow = null;
        _pendingPay = null;
        bool sent = _submit(new PayObjectIntent(offer.ObjectId, amount, offer.Name)).Status == ActionStatus.Sent;
        Changed?.Invoke();
        return sent;
    }

    public void CancelPay()
    {
        PayWindow = null;
        _pendingPay = null;
        Changed?.Invoke();
    }

    /// <summary>Selects an object and makes sure its properties are asked for (they hold the name, the owner and the sale).</summary>
    private void Select(ObjectState root)
    {
        if (Selected != root.Id || Properties?.ObjectId != root.FullId)
        {
            Selected = root.Id;
            Properties = null;
            _submit(new ObjectPropertiesIntent(root.Id.LocalId, Hold: true));
        }
    }

    private void OpenContents(ObjectState root)
    {
        Select(root);
        Contents = new ObjectContentsView(root.FullId, root.Id.LocalId, null, false);
        _submit(new RequestContentsIntent(root.Id.LocalId, root.FullId));
        OpenRequested?.Invoke(root.Id);
    }

    private void StartBuy(ObjectState root)
    {
        Offer = null;
        Select(root);
        _pendingBuy = root.Id;
        ResolveBuy();
    }

    private void ResolveBuy()
    {
        if (_pendingBuy is not { } id || Properties is not { } p || _world.Object(id) is not { } root || root.FullId != p.ObjectId)
            return;
        _pendingBuy = null;
        if (!ObjectSale.CanBuy(p))
        {
            BuyRefused?.Invoke();
            return;
        }
        byte type = ObjectSale.TypeOf(p);
        Offer = new BuyOffer(p.ObjectId, id.LocalId, p.Name, type, p.SalePrice, true);
        if (type == SaleTypes.Contents)
        {
            // the buyer sees what is inside before paying
            Contents = new ObjectContentsView(p.ObjectId, id.LocalId, null, false);
            _submit(new RequestContentsIntent(id.LocalId, p.ObjectId));
        }
        BuyOffered?.Invoke(Offer);
    }

    private void StartPay(ObjectState root)
    {
        Select(root);
        _pendingPay = root.Id;
        PayWindow = null;
        _submit(new RequestPayPriceIntent(root.Id.LocalId, root.FullId));
    }

    public event Action<BuyOffer>? BuyOffered;
    public event Action? BuyRefused;
    public event Action<PayOffer>? PayOffered;

    /// <summary>The object menu's entries changed because the object's properties arrived (the sale decides whether Buy shows).</summary>
    public event Action? EntriesChanged;

    public event Action<ObjectId>? OpenRequested;
    public event Action<ObjectId>? InspectRequested;
    public event Action<Guid>? AvatarInspectRequested;

    /// <summary>The own avatar's menu asked for the Appearance window.</summary>
    public event Action? AppearanceRequested;
    public event Action? AboutLandRequested;
    public event Action? Changed;

    private AvatarState? Me() => _world.FindAvatar(_self);

    /// <summary>True while the own avatar sits on something, or on the ground: the official viewer counts an avatar that plays the
    /// ground-sit animation as sitting, though nothing is its parent (llvoavatar.cpp, processAnimationStateChanges, sitDown).</summary>
    public bool Sitting => Me() is { ParentLocalId: not 0 } || Viewer.Core.Avatars.AgentAnimations.IsSittingOnGround(_world.Animations(_self));

    private ObjectState Root(ObjectState o)
    {
        for (int i = 0; i < 8 && o.ParentLocalId != 0; i++)
        {
            if (_world.Object(new ObjectId(o.Id.Epoch, o.ParentLocalId)) is not { } parent)
                return o;
            o = parent;
        }
        return o;
    }

    private bool Pose(ObjectState o, out Vector3 position, out Quaternion rotation, int depth = 0)
    {
        position = o.Position; rotation = o.Rotation;
        if (o.ParentLocalId == 0)
            return true;
        if (depth > 8 || _world.Object(new ObjectId(o.Id.Epoch, o.ParentLocalId)) is not { } parent || !Pose(parent, out var pp, out var pr, depth + 1))
            return false;
        position = pp + Vector3.Transform(o.Position, pr);
        rotation = pr * o.Rotation;
        return true;
    }

    /// <summary>Takes back into the inventory each of the objects named by local id that the agent may take (the menu's rule), by its root, once
    /// per root and with no pick: the live checks use it to take back everything a run rezzed. Returns the roots whose take was sent.</summary>
    public IReadOnlyList<uint> TakeBack(IEnumerable<uint> localIds)
    {
        var wanted = localIds.ToHashSet();
        var roots = new List<uint>();
        foreach (var o in _world.Snapshot().Objects.Values.Where(o => wanted.Contains(o.Id.LocalId)).OrderBy(o => o.Id.LocalId))
        {
            var root = Root(o);
            if (roots.Contains(root.Id.LocalId) || root.AttachmentPoint is not null)
                continue;
            if (!Viewer.Core.Building.BuildRules.CanTake(root.Interaction?.Flags ?? 0, false, SittingOn(root)))
                continue;
            if (_submit(new TakeIntent(root.Id.LocalId, false)).Status == ActionStatus.Sent)
                roots.Add(root.Id.LocalId);
        }
        return roots;
    }

    private bool SittingOn(ObjectState root)
    {
        if (Me() is not { ParentLocalId: not 0 } me || _world.Object(new ObjectId(root.Id.Epoch, me.ParentLocalId)) is not { } seat)
            return false;
        return Root(seat).Id == root.Id;
    }

    private static TouchSurface SurfaceOf(PickHit h) => new(h.Uv, h.St, h.Face, h.Position, h.Normal, h.Binormal);

    private bool Resolve(PickResult pick, out ObjectState prim, out ObjectState root)
    {
        prim = root = null!;
        if (pick.Kind != PickKind.Object || pick.Hit is not { } hit || _world.Object(hit.Id) is not { } p)
            return false;
        prim = p;
        root = Root(p);
        return true;
    }

    public void Hover(PickResult pick)
    {
        var next = Resolve(pick, out var prim, out var root) ? ClickAction.Cursor(prim.Interaction, root.Interaction, false, Sitting) : HoverCursor.Arrow;
        if (next != Cursor)
        {
            Cursor = next;
            Changed?.Invoke();
        }
    }

    public void LeftDown(PickResult pick)
    {
        _grab = null;
        Deselect();
        if (!Resolve(pick, out var prim, out var root))
            return;
        switch (ClickAction.Left(prim.Interaction, root.Interaction, false, Sitting))
        {
            case LeftClick.Touch:
                var surface = SurfaceOf(pick.Hit!);
                var offset = Vector3.Zero;
                if (root.Id != prim.Id && Pose(root, out var rootPos, out var rootRot) && Pose(prim, out var primPos, out _))
                    offset = Vector3.Transform(rootPos - primPos, Quaternion.Conjugate(rootRot));
                if (_submit(new GrabIntent(prim.Id.LocalId, surface, offset)).Status == ActionStatus.Sent)
                    _grab = (prim.Id.LocalId, surface);
                break;
            case LeftClick.Sit:
                Sit(prim, pick.Hit!, Camera?.Invoke());
                break;
            case LeftClick.Open:
                OpenContents(root);
                break;
            case LeftClick.Buy:
                StartBuy(root);
                break;
            case LeftClick.Pay:
                StartPay(root);
                break;
            case LeftClick.Unhandled:
                UnhandledClicks++;
                break;
        }
    }

    /// <summary>The camera when a click is made: its origin and view direction in region coordinates (the pick ray's frame). Set by the
    /// host; null where there is none (checks), and the sit offset is then the clicked surface point.</summary>
    public Func<(Vector3 Origin, Vector3 At)?>? Camera { get; set; }

    /// <summary>The last sit offset sent, in region axes from the object's position (for the sit log line).</summary>
    public Vector3? LastSitOffset { get; private set; }

    /// <summary>The region's last answer to a sit request: the seat position and turn it states (for the sit log line). A seat with a sit
    /// target answers with the target's position; one without answers with the offset that was sent.</summary>
    public SitResponded? LastSitResponse { get; private set; }

    /// <summary>Raised after a sit request is sent, from a click or the menu: the keyboard goes back to the world, as the official viewer
    /// does after a sit (lltoolpie.cpp, gFocusMgr.setKeyboardFocus(NULL)), so the arrow keys reach the avatar once it stands.</summary>
    public event Action? SitSent;

    private void Sit(ObjectState prim, PickHit hit, (Vector3 Origin, Vector3 At)? camera)
    {
        _submit(new SitIntent(prim.FullId, SitOffset(prim, hit, camera)));
        SitSent?.Invoke();
    }

    /// <summary>The offset a sit request carries, as the official viewer makes it (<see cref="SeatPlacement.SitOffset"/>): a point in the
    /// object's box under the click, from the object's position, in region axes, worked out with the camera of the pick (the official
    /// viewer's pick keeps it as its object offset). A region without a sit target on the object seats the avatar from it.</summary>
    private Vector3 SitOffset(ObjectState prim, PickHit hit, (Vector3 Origin, Vector3 At)? camera)
    {
        if (!Pose(prim, out var pos, out var rot))
            return (LastSitOffset = Vector3.Zero).Value;
        var offset = camera is { } cam
            ? SeatPlacement.SitOffset(pos, rot, prim.Scale, cam.Origin, cam.At, hit.Position)
            : hit.Position - pos;
        LastSitOffset = offset;
        return offset;
    }

    public void LeftUp(PickResult pick)
    {
        if (_grab is not { } grab)
            return;
        _grab = null;
        var surface = pick.Kind == PickKind.Object && pick.Hit is { } h && h.LocalId == grab.Local ? SurfaceOf(h) : grab.Surface;
        _submit(new ReleaseIntent(grab.Local, surface));
    }

    public void RightClick(PickResult pick)
    {
        _menuPick = pick;
        _menuCamera = Camera?.Invoke();
        switch (pick.Kind)
        {
            case PickKind.Object when Resolve(pick, out var prim, out var root):
                Selected = root.Id;
                Properties = null;
                _submit(new ObjectPropertiesIntent(root.Id.LocalId, Hold: true));
                Menu = MenuKind.Object;
                Entries = ObjectMenu(prim, root);
                break;
            case PickKind.Avatar when pick.Avatar is { } a && a.AgentId == _self:
                Selected = null;
                if (SelfHooks is { } self)
                {
                    Menu = MenuKind.Self;
                    Entries = ContextMenus.ForSelf(new SelfMenuContext(Sitting, self.IsFlying(), self.Worn(), self.Attachments?.Invoke(), self.CanDetachItem));
                }
                else
                {
                    Menu = MenuKind.None;
                    Entries = [];
                }
                break;
            case PickKind.Avatar when pick.Avatar is { } a:
                Selected = null;
                Menu = MenuKind.Avatar;
                Entries = ContextMenus.ForAvatar(new AvatarMenuContext(false, _isFriend(a.AgentId), _social.IsBlocked?.Invoke(MuteKind.Resident, a.AgentId) == true));
                break;
            case PickKind.Land:
                Selected = null;
                Menu = MenuKind.Land;
                bool near = Me() is { } me && Vector2.Distance(new Vector2(me.Position.X, me.Position.Y), new Vector2(pick.Point.X, pick.Point.Y)) <= ContextMenus.SitReachMetres;
                Entries = ContextMenus.ForLand(new LandMenuContext(true, near));
                break;
            default:
                Selected = null;
                Menu = MenuKind.None;
                Entries = [];
                break;
        }
        Changed?.Invoke();
    }

    /// <summary>The object menu for a prim and its root, with what is known of the sale (the properties arrive after the click).</summary>
    private IReadOnlyList<ContextEntry> ObjectEntries(ObjectState prim, ObjectState root)
    {
        bool worn = root.AttachmentPoint is not null && root.WearerAgentId == _self;
        var sale = Properties is { } p && p.ObjectId == root.FullId ? p : null;
        return ContextMenus.ForObject(new ObjectMenuContext(prim.Interaction, root.Interaction, worn, SittingOn(root), Sitting,
            _social.IsBlocked?.Invoke(MuteKind.Object, root.FullId) == true, CanDetach: worn && SelfHooks?.Detach is not null && root.AttachItemId is { } item && item != Guid.Empty
                && (SelfHooks.CanDetachItem?.Invoke(item) ?? true),
            ForSale: sale is not null && ObjectSale.IsForSale(sale), CanBuy: sale is not null && ObjectSale.CanBuy(sale)));
    }

    /// <summary>An object's menu: its own entries and, for an own worn object, the own avatar's part after them, Take Off and its Clothes
    /// menu included (menu_attachment_self.xml): a mesh body or alpha layers can leave nothing else of the avatar to right-click.</summary>
    private IReadOnlyList<ContextEntry> ObjectMenu(ObjectState prim, ObjectState root)
    {
        var entries = ObjectEntries(prim, root);
        _selfPart = [];
        if (root.AttachmentPoint is null || root.WearerAgentId != _self || SelfHooks is not { } own)
            return entries;
        var taken = entries.Select(e => e.Id).ToHashSet();
        var part = ContextMenus.ForSelf(new SelfMenuContext(Sitting, own.IsFlying(), own.Worn(), own.Attachments?.Invoke(), own.CanDetachItem))
            .Where(e => !taken.Contains(e.Id)).ToArray();
        foreach (var e in part) Collect(e, _selfPart);
        _selfPart.ExceptWith(taken);   // an id the object's own entries use stays the object's (its Detach)
        return [.. entries, .. part];
    }

    private static void Collect(ContextEntry e, HashSet<string> into)
    {
        into.Add(e.Id);
        foreach (var c in e.Children ?? []) Collect(c, into);
    }

    public bool Choose(string entryId)
    {
        var entry = FindEntry(Entries, entryId);
        if (entry is not { Enabled: true } || _menuPick is not { } pick)
            return false;
        if (entry.Id is "drop" || (entry.Id == "delete" && entry.Children is not null))
            return false;   // the question's header: only its confirm entry drops or deletes
        var kind = Menu;
        _menuChoiceKeepsSelection = kind == MenuKind.Object && entryId is "inspect" or "open" or "buy" or "pay";
        if (kind == MenuKind.Object && _selfPart.Contains(entryId))
            kind = MenuKind.Self;   // an own avatar entry in an own worn object's menu
        switch (kind)
        {
            case MenuKind.Object when Resolve(pick, out var prim, out var root):
                switch (entryId)
                {
                    case "touch":
                        var surface = SurfaceOf(pick.Hit!);
                        _submit(new GrabIntent(prim.Id.LocalId, surface));
                        _submit(new ReleaseIntent(prim.Id.LocalId, surface));
                        break;
                    case "sit": Sit(prim, pick.Hit!, _menuCamera); break;
                    case "stand": Stand(); break;
                    case "open": OpenContents(root); break;
                    case "buy": StartBuy(root); break;
                    case "pay": StartPay(root); break;
                    case "inspect": InspectRequested?.Invoke(root.Id); break;
                    // the official viewer plays the take sound once the take is sent (llviewermenu.cpp, derez)
                    case "take" or "take_copy":
                        if (_submit(new TakeIntent(root.Id.LocalId, entryId == "take_copy")).Status == ActionStatus.Sent)
                            PlaySound?.Invoke(global::Viewer.Shell.Sound.UiSoundSlot.ObjectRezOut);
                        break;
                    case "delete" when entry.Children is null:
                    case "delete/confirm":
                        // no sound: the official viewer only preloads its delete sound and never plays it (llvieweraudio.cpp)
                        _submit(new DeleteObjectIntent(root.Id.LocalId));
                        break;
                    case "detach" when root.AttachItemId is { } wornItem && wornItem != Guid.Empty:
                        SelfHooks?.Detach?.Invoke(wornItem);
                        break;
                    case "drop/confirm" when root.AttachmentPoint is not null && root.WearerAgentId == _self:
                        _submit(new DropAttachmentIntent(root.Id.LocalId));
                        break;
                    case "block": _social.Block?.Invoke(MuteKind.Object, root.FullId); break;
                    case "unblock": _social.Unblock?.Invoke(MuteKind.Object, root.FullId); break;
                    case var attach when attach.StartsWith("attach/", StringComparison.Ordinal) && int.TryParse(attach["attach/".Length..], out int point):
                        _submit(new AttachWorldObjectIntent(root.Id.LocalId, point));
                        break;
                }
                break;
            case MenuKind.Avatar when pick.Avatar is { } a:
                switch (entryId)
                {
                    case "appearance": AppearanceRequested?.Invoke(); break;
                    case "im": _social.InstantMessage(a.AgentId); break;
                    case "add_friend": _social.AddFriend(a.AgentId); break;
                    case "offer_teleport": _social.OfferTeleport(a.AgentId); break;
                    case "request_teleport": _social.RequestTeleport(a.AgentId); break;
                    case "inspect": AvatarInspectRequested?.Invoke(a.AgentId); break;
                    case "block": _social.Block?.Invoke(MuteKind.Resident, a.AgentId); break;
                    case "unblock": _social.Unblock?.Invoke(MuteKind.Resident, a.AgentId); break;
                }
                break;
            case MenuKind.Self when SelfHooks is { } self:
                switch (entryId)
                {
                    case "appearance": self.OpenAppearance(); break;
                    case "inventory": self.OpenInventory(); break;
                    case "stand": Stand(); break;
                    case "fly" or "stop_fly": self.ToggleFly(); break;
                    case "detach_all": self.DetachAll?.Invoke(); break;
                    case "take_off_all" when self.TakeOffAllClothes is { } all:
                        all();
                        break;
                    case var byType when byType.StartsWith("take_off_type/", StringComparison.Ordinal):
                        // the top layer of that type: the one worn last (llviewermenu.cpp, LLEditTakeOff takes the type's last index)
                        string name = byType["take_off_type/".Length..];
                        var top = self.Worn().Where(w => !w.IsObject && ContextMenus.TypeName(w.Type) == name && ContextMenus.ClothesOrder.Contains(w.Type))
                            .OrderByDescending(w => w.Layer).Select(w => (WornEntry?)w).FirstOrDefault();
                        if (top is not { } layer)
                            return false;
                        self.TakeOff(layer.Id);
                        break;
                    case var off when off.StartsWith("take_off/", StringComparison.Ordinal) && Guid.TryParse(off["take_off/".Length..], out var worn):
                        self.TakeOff(worn);
                        break;
                    case var det when det.StartsWith("detach/", StringComparison.Ordinal) && Guid.TryParse(det["detach/".Length..], out var attached):
                        self.Detach?.Invoke(attached);
                        break;
                }
                break;
            case MenuKind.Land:
                switch (entryId)
                {
                    case "sit_here": _submit(new SitGroundIntent()); break;
                    case "set_home": _social.SetHome(); break;
                    case "about_land":
                        Parcel = null;
                        _submit(new ParcelInfoIntent(pick.Point.X, pick.Point.Y));
                        AboutLandRequested?.Invoke();
                        break;
                }
                break;
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>An entry by id, looking inside sub-menus too.</summary>
    private static ContextEntry? FindEntry(IReadOnlyList<ContextEntry> entries, string id)
    {
        foreach (var e in entries)
        {
            if (e.Id == id)
                return e;
            if (e.Children is { } kids && FindEntry(kids, id) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>Closes the right-click menu (the view calls it after drawing the choice, or when the user clicks away).</summary>
    /// <summary>
    /// Closes the menu. The object a right-click selected stays selected only while its menu is open, as in the official viewer (the
    /// menu holder lets go of its selection when the menus hide, llviewermenu.cpp, and a selection nothing holds is then cleared,
    /// llviewerwindow.cpp): after Sit Here, Touch or a menu closed with no choice the highlight goes. A choice that opened a window
    /// about the object (Inspect, Open, Buy, Pay) keeps it for that window.
    /// </summary>
    public void CloseMenu()
    {
        if (Menu == MenuKind.None)
            return;
        bool release = Menu == MenuKind.Object && !_menuChoiceKeepsSelection;
        _menuChoiceKeepsSelection = false;
        Menu = MenuKind.None;
        Entries = [];
        if (release)
        {
            Selected = null;
            Properties = null;
        }
        Changed?.Invoke();
    }

    private bool _menuChoiceKeepsSelection;   // the object menu's choice opened a window about the selected object

    /// <summary>Sit on the ground where the avatar stands (what the land menu's Sit Here sends).</summary>
    public void SitOnGround() => _submit(new SitGroundIntent());

    /// <summary>Whether the Stop Flying button shows: while the avatar flies and is not sitting, and never in mouse-look (the official
    /// viewer's stand/stop-flying panel, llmoveview.cpp, LLPanelStandStopFlying).</summary>
    public bool StopFlyingVisible(bool mouselook) => !mouselook && !Sitting && SelfHooks?.IsFlying() == true;

    /// <summary>The Stop Flying button: ends the flight (the official viewer's LLAgent::setFlying(false)).</summary>
    public void StopFlying()
    {
        if (SelfHooks is { } hooks && hooks.IsFlying())
            hooks.ToggleFly();
    }

    public void Stand()
    {
        _submit(new StandIntent());
        Seat = null;
        Changed?.Invoke();
    }

    public void Deselect()
    {
        if (Selected is null && Properties is null)
            return;
        Selected = null;
        Properties = null;
        Changed?.Invoke();
    }

    /// <summary>Every frame: forgets the seat camera once the avatar has stood up.</summary>
    public void Tick()
    {
        bool sitting = Sitting;
        if (_wasSitting && !sitting && Seat is not null)
        {
            Seat = null;
            Changed?.Invoke();
        }
        _wasSitting = sitting;
        // Copy and Wear: the items reach the inventory some moments after the copy; they are worn once the whole set is there
        if (_pendingWear is { } wear && ItemsInFolder?.Invoke(wear.Folder) >= wear.Count)
        {
            _pendingWear = null;
            WearFolder?.Invoke(wear.Folder);
        }
    }

    public void Receive(TransportEvent e)
    {
        switch (e)
        {
            case ObjectPropertiesReceived p when Selected is { } sel && _world.Object(sel) is { } o && o.FullId == p.ObjectId:
                Properties = p;
                if (Menu == MenuKind.Object && _menuPick is { } menuPick && Resolve(menuPick, out var menuPrim, out var menuRoot) && menuRoot.Id == sel)
                {
                    Entries = ObjectMenu(menuPrim, menuRoot);
                    EntriesChanged?.Invoke();
                }
                ResolveBuy();
                Changed?.Invoke();
                break;
            case TaskInventoryReceived t when Contents is { } c && c.ObjectId == t.ObjectId:
                Contents = c with { Items = t.Ok ? t.Items : null, Failed = !t.Ok };
                Changed?.Invoke();
                break;
            case ContentsCopied copied:
                LastCopy = copied;
                if (copied.Wear && copied.ItemCount > 0)
                    _pendingWear = (copied.FolderId, copied.ItemCount);
                Changed?.Invoke();
                break;
            case PayPriceReceived price when _pendingPay is { } payId && _world.Object(payId) is { } payRoot && payRoot.FullId == price.ObjectId:
                PayWindow = new PayOffer(price.ObjectId, payId.LocalId, Properties is { } pp && pp.ObjectId == price.ObjectId ? pp.Name : "", price.DefaultPrice, price.ButtonPrices);
                PayOffered?.Invoke(PayWindow);
                Changed?.Invoke();
                break;
            case SitResponded s:
                Seat = new SeatCameraState(s.ObjectId, s.EyeOffset, s.AtOffset, s.ForceMouselook);
                LastSitResponse = s;
                Changed?.Invoke();
                break;
            case ParcelDetailsReceived d:
                Parcel = d;
                Changed?.Invoke();
                break;
        }
    }
}
