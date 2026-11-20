// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Building;
using Viewer.Core.Interaction;
using Viewer.Core.Session;
using Viewer.Core.World;

namespace Viewer.Shell.Build;

/// <summary>
/// Duplicate (Ctrl+D), the create tool's copy of the selection, "when left-clicked", For sale and the land impact. Behaviour follows the
/// official viewer: llselectmgr.cpp (duplicate, selectDuplicate, selectDuplicateOnRay, selectionSetClickAction, selectionSetObjectSaleInfo,
/// getSelectedLinksetCost), llpanelpermissions.cpp and lltoolplacer.cpp, read as behaviour only.
/// </summary>
public sealed partial class EditController
{
    // ---------------- the create tool's options ----------------

    /// <summary>The shape the Create tool places (the official placer's object type; a cube until another button is pressed).</summary>
    public CreateShape ShapeChoice { get; private set; } = CreateShape.Cube;

    /// <summary>"Copy selection": the create tool places a copy of the selection instead of a new shape (CreateToolCopySelection, off).</summary>
    public bool CopySelection { get; set; }

    /// <summary>"Center Copy": the copy is centred on the click (CreateToolCopyCenters, on by default); only used with Copy selection.</summary>
    public bool CopyCenters { get; set; } = true;

    /// <summary>"Rotate Copy": the copy is turned to the surface (CreateToolCopyRotates, off by default); only used with Copy selection.</summary>
    public bool CopyRotates { get; set; }

    /// <summary>Pressing a shape button chooses that shape and turns Copy selection off (llfloatertools.cpp setObjectType).</summary>
    public void ChooseShape(CreateShape shape)
    {
        ShapeChoice = shape;
        CopySelection = false;
        Changed?.Invoke();
    }

    /// <summary>The create tool's click with Copy selection on: the selected roots are copied to where the click points. Nothing selected sends
    /// nothing and still counts as placed (the official placer hands back to the move tool either way). False when the click cannot place
    /// (nothing there, an avatar, too far) or something worn is selected.</summary>
    public bool PlaceCopy(PickResult pick, Vector3 cameraPosition, Vector3 cameraAt, Vector3 rayDirection, float nearClip)
    {
        if (BuildRules.CreatePlacement(pick, cameraPosition, cameraAt, rayDirection, nearClip) is not { } placement)
            return false;
        if (pick.Kind == PickKind.Object && pick.Hit is { } hit && Find(hit.Id.LocalId) is { AttachmentPoint: not null })
            return false;
        var roots = _selection.Select(RootOf).Where(r => r != 0).Distinct().ToArray();
        if (roots.Any(r => Find(r) is { AttachmentPoint: not null }))
            return false;
        if (roots.Length > 0)
            Send(new DuplicateOnRayIntent(roots, placement, CopyCenters, CopyRotates, 0));
        return true;
    }

    // ---------------- Duplicate ----------------

    /// <summary>The slop of a Move handle drag, in pixels (llmaniptranslate.cpp MOUSE_DRAG_SLOP).</summary>
    public const float DragSlopPixels = 2f;

    /// <summary>Whether the pointer has left the square round where it went down (a distance of 2 pixels or more on either axis).</summary>
    public static bool LeavesDragSlop(float downX, float downY, float x, float y) =>
        MathF.Abs(downX - x) >= DragSlopPixels || MathF.Abs(downY - y) >= DragSlopPixels;

    /// <summary>Where Duplicate puts the copy: half a metre east and north of the original (llselectmgr.cpp duplicate).</summary>
    public static readonly Vector3 DuplicateOffset = new(0.5f, 0.5f, 0f);

    /// <summary>How long a copy asked for is waited for before the window stops expecting it, in milliseconds.</summary>
    private const long CopyWaitMs = 10_000;

    private readonly List<Vector3> _copiesAt = new();
    private long _copiesUntil;

    /// <summary>Duplicate needs a selected object that may be copied (llselectmgr.cpp canDuplicate: getFirstCopyableObject).</summary>
    public bool CanDuplicate => _selection.Any(l => Find(l) is { AttachmentPoint: null } o && ((o.Interaction?.Flags ?? 0) & ObjectFlagBits.Copy) != 0);

    /// <summary>Build > Object > Duplicate (Ctrl+D): each selected root is copied half a metre away and the copies become the selection. Null
    /// when sent, else why not: a worn object is never copied, and with nothing copyable the viewer says which object cannot be copied
    /// (the notification NoCopyPermsNoObject).</summary>
    public string? Duplicate()
    {
        if (_selection.Count == 0)
            return "duplicate.none";
        if (_selection.Any(l => Find(l) is { AttachmentPoint: not null }))
            return "duplicate.worn";
        if (!CanDuplicate)
            return "duplicate.no_copy";
        var roots = _selection.Select(RootOf).Where(r => r != 0).Distinct().ToArray();
        if (!Send(new DuplicateObjectsIntent(roots, DuplicateOffset, LmvDuplicateCreateSelected)))
            return "duplicate.not_sent";
        // the copies arrive selected, so the originals are let go (selectDuplicate with select_copy: deselectAll)
        _copiesAt.Clear();
        _copiesAt.AddRange(roots.Select(r => (Find(r)?.Position ?? Vector3.Zero) + DuplicateOffset));
        _copiesUntil = _clock() + CopyWaitMs;
        SendDeselect(_selection);
        _selection.Clear();
        _faceSelection.Clear();
        Changed?.Invoke();
        return null;
    }

    /// <summary>ObjectDuplicate's flag that makes the copy come selected.</summary>
    public const uint LmvDuplicateCreateSelected = 1u << 1;

    /// <summary>An object arrived: when it is a copy Duplicate asked for (the region sends it flagged as made selected, owned by the agent),
    /// it becomes part of the selection. True when it was one.</summary>
    public bool CopyArrived(ObjectState o)
    {
        if (_copiesAt.Count == 0 || _clock() > _copiesUntil)
        {
            _copiesAt.Clear();
            return false;
        }
        uint flags = o.Interaction?.Flags ?? 0;
        if (!BuildMode || o.ParentLocalId != 0 || o.AttachmentPoint is not null || (flags & ObjectFlagBits.YouOwner) == 0 || _selection.Contains(o.Id.LocalId))
            return false;
        // the region may or may not flag the copy as made selected (a stock OpenSim region does not): a new object of the agent's own where a
        // copy was asked for is taken as that copy
        int at = (flags & ObjectFlagBits.CreateSelected) != 0 ? 0 : _copiesAt.FindIndex(p => Vector3.Distance(p, o.Position) < 0.25f);
        if (at < 0)
            return false;
        _copiesAt.RemoveAt(at);
        Add(o.Id.LocalId);
        Changed?.Invoke();
        return true;
    }

    // ---------------- when left-clicked ----------------

    /// <summary>The click choice of the selection's first object, or null with nothing selected.</summary>
    public ClickChoice? ClickChoiceShown => Object() is { } o ? ClickChoices.ChoiceOf(_clickAsked.TryGetValue(o.Id.LocalId, out var asked) ? asked : o.Interaction?.ClickAction ?? 0) : null;

    // what a choice asked for, shown until the selection is let go: a region need not send an update after a click action changes (a stock
    // OpenSim region sends none), and the official viewer shows its own change at once too (llselectmgr.cpp setClickAction)
    private readonly Dictionary<uint, byte> _clickAsked = new();

    /// <summary>The list is on when every selected root may be modified and every selected object is a prim.</summary>
    public bool ClickChoiceEnabled =>
        _selection.Count > 0 && ClickChoices.Enabled(_selection.Select(RootOf).Distinct().All(r => Access(r).CanModify), _selection.All(l => Find(l)?.Shape is not null));

    /// <summary>Whether every selected root is for sale and whether any has a money() script (for the choice's checks).</summary>
    private bool SelectionForSale() => _selection.Select(RootOf).Distinct().All(r => PropertiesOf(r)?.ForSale == true);

    private bool SelectionPayable() => _selection.All(l =>
        (((Find(l)?.Interaction?.Flags ?? 0) | (Find(RootOf(l))?.Interaction?.Flags ?? 0)) & ObjectFlagBits.TakesMoney) != 0);

    /// <summary>Sets the click action of every selected object, parts included (selectionSetClickAction sends to each). Returns the notice
    /// to show, if any, and whether the action was sent: Buy is refused for an object not for sale; Pay is set with a warning when no
    /// script takes money.</summary>
    public (bool Sent, string? Notice) SetClickChoice(ClickChoice choice)
    {
        if (!ClickChoiceEnabled)
            return (false, null);
        var (set, notice) = ClickChoices.Check(choice, SelectionForSale(), SelectionPayable());
        if (!set)
            return (false, notice);
        var parts = PartsOfSelection();
        byte action = ClickChoices.ActionOf(choice);
        bool sent = Send(new SetClickActionIntent(parts, action));
        if (sent)
        {
            foreach (var l in parts)
                _clickAsked[l] = action;
            Changed?.Invoke();
        }
        return (sent, notice);
    }

    /// <summary>Every selected prim and, for a selected root, its whole linkset.</summary>
    private uint[] PartsOfSelection()
    {
        var roots = _selection.Select(RootOf).Where(r => r != 0).ToHashSet();
        var all = new HashSet<uint>(_selection);
        foreach (var o in _world.Snapshot().Objects.Values)
            if (roots.Contains(o.ParentLocalId) && !_editLinked)
                all.Add(o.Id.LocalId);
        return [.. all];
    }

    // ---------------- for sale ----------------

    private IReadOnlyList<uint> SelectedRoots() => _selection.Select(RootOf).Where(r => r != 0).Distinct().ToArray();

    /// <summary>What the For sale controls show for the selected roots; null with nothing selected or before the region has sent their properties.</summary>
    public SaleView? SaleState()
    {
        var roots = SelectedRoots();
        if (roots.Count == 0 || roots.Any(r => PropertiesOf(r) is null))
            return null;
        var nodes = roots.Select(r => PropertiesOf(r)!).Select(p => new SaleNode(p.ForSale ? (SaleKind)Math.Clamp((int)p.SaleType, 1, 3) : SaleKind.NotForSale, p.SalePrice, p.OwnerMask)).ToArray();
        bool owned = roots.All(r => Access(r).Owned);
        bool allTransfer = roots.All(r => ((Find(r)?.Interaction?.Flags ?? 0) & ObjectFlagBits.Transfer) != 0);
        return Viewer.Core.Building.SaleRules.ViewOf(nodes, owned, allTransfer);
    }

    /// <summary>The For Sale box, the sale type or the price was committed: every selected root gets the new sale (setAllSaleInfo). When every
    /// selected object was still on Touch or Buy, its click action follows the sale (Buy for sale, Touch not).</summary>
    public bool SetSale(bool boxTicked, SaleKind chosen, int? price)
    {
        var roots = SelectedRoots();
        if (roots.Count == 0 || SaleState() is not { BoxEnabled: true })
            return false;
        bool wasForSale = roots.Any(r => PropertiesOf(r)?.ForSale == true);
        var (type, p) = Viewer.Core.Building.SaleRules.Commit(boxTicked, chosen, price);
        if (!Send(new SetObjectSaleIntent(roots, (byte)type, p)))
            return false;
        // the region answers a change with the properties; until then show what was asked
        foreach (var r in roots)
            if (Find(r) is { } o && _properties.TryGetValue(o.FullId, out var old))
                _properties[o.FullId] = old with { ForSale = type != SaleKind.NotForSale, SaleType = (byte)type, SalePrice = p };
        if (roots.All(r => Access(r).CanModify) && ClickChoices.AfterSaleChange(PartsOfSelection().Select(l => Find(l)?.Interaction?.ClickAction ?? (byte)0),
                wasForSale, type != SaleKind.NotForSale) is { } action)
            Send(new SetClickActionIntent(PartsOfSelection(), action));
        Changed?.Invoke();
        return true;
    }

    // ---------------- land impact ----------------

    private readonly Dictionary<Guid, ObjectCostReceived> _costs = new();

    /// <summary>Asks the region for the land impact of the selected roots (it answers through GetObjectCost).</summary>
    public void RefreshCosts()
    {
        var ids = SelectedRoots().Select(r => Find(r)?.FullId ?? Guid.Empty).Where(i => i != Guid.Empty).ToArray();
        if (ids.Length > 0)
            Send(new RequestObjectCostIntent(ids));
    }

    private void ReceiveCost(TransportEvent e)
    {
        if (e is ObjectCostReceived c)
        {
            _costs[c.ObjectId] = c;
            if (SelectedRoots().Any(r => Find(r)?.FullId == c.ObjectId))
                Changed?.Invoke();
        }
    }

    /// <summary>The selection's land impact as the Build window shows it, with the number of selected roots; null with nothing selected.</summary>
    public (int Roots, int Impact)? LandImpactShown()
    {
        var roots = SelectedRoots();
        if (roots.Count == 0)
            return null;
        float total = LandImpact.Total(roots.Select(r => (r, Find(r) is { AttachmentPoint: not null })),
            r => Find(r) is { } o && _costs.TryGetValue(o.FullId, out var c) ? c.LinksetCost : null);
        return (roots.Count, LandImpact.Shown(total));
    }
}
