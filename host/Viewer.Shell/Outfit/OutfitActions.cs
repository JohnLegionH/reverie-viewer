// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Actions;
using Viewer.Core.Inventory;

namespace Viewer.Shell.Outfit;

/// <summary>What became of a request to change what is worn: refused here (nothing submitted), or submitted to the pipeline.</summary>
/// <param name="Code">The refusal's code (a string-table suffix after <c>outfit.notice.</c>) when refused; the pipeline's code otherwise (may be null).</param>
public sealed record OutfitActionResult(bool Submitted, string? Code);

/// <summary>
/// The wear, add, take off, attach, wear-outfit and save-outfit actions of the inventory and outfit windows. Each is planned
/// from the inventory by <see cref="OutfitPlanner"/> (which holds the official viewer's rules), refused with a reason when it
/// breaks one, and otherwise submitted through the action pipeline. When a change is sent, the current outfit folder is read
/// again so the grid's answer settles what is worn.
/// </summary>
public sealed class OutfitActions
{
    private readonly InventoryModel _model;
    private readonly Func<ActionIntent, ActionResult> _submit;

    private readonly Func<IReadOnlySet<Guid>>? _attached;

    /// <summary>Whether a worn object may be taken off now (restraints may lock one); null: every one may.</summary>
    private readonly Func<Guid, bool>? _canDetach;

    public OutfitActions(InventoryModel model, Func<ActionIntent, ActionResult> submit, Func<IReadOnlySet<Guid>>? attached = null, Func<Guid, bool>? canDetach = null)
    {
        _model = model;
        _submit = submit;
        _attached = attached;
        _canDetach = canDetach;
    }

    private bool Unlocked(Guid id) => _canDetach?.Invoke(id) ?? true;

    public bool IsWorn(Guid itemId) => OutfitPlanner.IsWorn(_model, itemId) || Attached().Contains(itemId);

    private IReadOnlySet<Guid> Attached() => _attached?.Invoke() ?? new HashSet<Guid>();

    // ---------------- what the menus offer ----------------

    public bool CanWear(Guid id) => OutfitPlanner.Wear(_model, id, add: false).Ok;

    /// <summary>Add stacks a clothing layer or an object; a body part has no layers, so it is not offered (the official viewer's rule).</summary>
    public bool CanAdd(Guid id)
    {
        var item = _model.Item(id);
        if (item?.LinkedId is { } t && item.Kind == ItemKind.Link)
            item = _model.Item(t);
        return item is { Kind: not ItemKind.Bodypart } && OutfitPlanner.Wear(_model, id, add: true).Ok;
    }

    public bool CanTakeOff(Guid id) => OutfitPlanner.TakeOff(_model, id).Ok;

    public bool CanAttach(Guid id) => OutfitPlanner.Attach(_model, id, 0, add: false).Ok;

    public bool CanWearOutfit(Guid folderId) => OutfitPlanner.WearOutfit(_model, folderId).Ok;

    // ---------------- the changes ----------------

    public OutfitActionResult Wear(Guid id) => Run(OutfitPlanner.Wear(_model, id, add: false));

    public OutfitActionResult Add(Guid id) =>
        CanAddKind(id) ? Run(OutfitPlanner.Wear(_model, id, add: true)) : new(false, "outfit.not_wearable");

    private bool CanAddKind(Guid id)
    {
        var item = _model.Item(id);
        if (item?.LinkedId is { } t && item.Kind == ItemKind.Link)
            item = _model.Item(t);
        return item is not null && item.Kind != ItemKind.Bodypart;
    }

    /// <summary>Whether the item is an object the avatar wears and can be taken off with Detach.</summary>
    public bool CanDetach(Guid id) => Unlocked(id) && OutfitPlanner.Detach(_model, id, Attached()).Ok;

    public OutfitActionResult Detach(Guid id) => Unlocked(id) ? Run(OutfitPlanner.Detach(_model, id, Attached())) : new(false, "rlv.detach");

    /// <summary>Takes off every worn object (the own menu's Detach All); a locked one stays on.</summary>
    public OutfitActionResult DetachAll()
    {
        var plan = OutfitPlanner.DetachAll(_model, Attached());
        if (_canDetach is null || plan.Change is not { } change)
            return Run(plan);
        // a locked object keeps its link in the current outfit folder and stays attached
        var kept = change with
        {
            Detach = change.Detach.Where(Unlocked).ToList(),
            RemoveLinks = change.RemoveLinks.Where(link => _model.Item(link)?.LinkedId is not { } target || Unlocked(target)).ToList(),
        };
        return kept.IsEmpty ? new(false, "rlv.detach") : Run(plan with { Change = kept });
    }

    public OutfitActionResult TakeOff(Guid id) => Run(OutfitPlanner.TakeOff(_model, id));

    public OutfitActionResult TakeOffAllClothes() => Run(OutfitPlanner.TakeOffAllClothes(_model));

    public OutfitActionResult Attach(Guid id, int point, bool add) => Run(OutfitPlanner.Attach(_model, id, point, add));

    public OutfitActionResult WearOutfit(Guid folderId)
    {
        var plan = OutfitPlanner.WearOutfit(_model, folderId);
        if (plan.Refusal == "outfit.not_loaded")
            if (!_model.EnsureLoaded(folderId))
                _model.Retry(folderId);   // ask for it (again); the user tries once more when it has arrived
        return Run(plan);
    }

    public OutfitActionResult SaveOutfit(string name)
    {
        var (plan, refusal) = OutfitPlanner.SaveOutfit(_model, name);
        if (plan is null)
            return new(false, refusal);
        return Submit(new OutfitSaveIntent(plan.CofId, plan.MyOutfitsId, plan.Name, plan.RootId), plan.CofId, plan.MyOutfitsId == Guid.Empty ? plan.RootId : plan.MyOutfitsId);
    }

    private OutfitActionResult Run(OutfitPlan plan) =>
        plan.Change is { } change ? Submit(new OutfitChangeIntent(change), change.CofId) : new(false, plan.Refusal);

    private OutfitActionResult Submit(ActionIntent intent, params Guid[] touched)
    {
        var result = _submit(intent);
        bool sent = result.Status is ActionStatus.Sent or ActionStatus.OutcomeUnknown;
        if (sent)
            foreach (var folder in touched.Distinct())
                if (_model.LoadState(folder) is FolderLoad.Loaded or FolderLoad.Loading)
                    _model.Invalidate(folder);
        return new OutfitActionResult(sent, result.Code ?? (result.Issues is { Count: > 0 } issues ? issues[0].Code : null));
    }
}
