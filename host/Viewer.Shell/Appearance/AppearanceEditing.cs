// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Actions;
using Viewer.Core.Appearance;
using Viewer.Core.Assets;
using Viewer.Core.Inventory;

namespace Viewer.Shell.Appearance;

public enum EditState { Closed, Loading, Ready, Failed }

public enum CloseVerdict { Closed, NeedsDecision }

public enum EditSaveState { Idle, Saving, Saved, SavedNotRebuilt, Failed }

/// <summary>
/// The Appearance window's Edit tab without the engine: which worn wearables can be opened, the wearable being edited, whether
/// it can be saved, what closing with unsaved changes needs, and how a save went. Opening reads the wearable file through the
/// session; saving goes through the action pipeline; the avatar preview is told through <see cref="PreviewChanged"/>. Used from
/// the interface thread only. Behaviour follows the official viewer's wearable editor (indra/newview/llpaneleditwearable.cpp:
/// no save when nothing changed, Save As needs a copyable item, closing with changes asks first, Don't Save reverts).
/// </summary>
public sealed class AppearanceEditing
{
    private readonly InventoryModel _model;
    private readonly Func<Guid, WearableItemInfo?> _itemInfo;
    private readonly Func<Guid, int, CancellationToken, Task<AssetFetchResult>> _fetch;
    private readonly Func<bool> _isMale;
    private readonly Func<ActionIntent, ActionResult> _submit;
    private int _generation;
    private bool _canCopy, _savingAsCopy;
    private string _pendingName = "";

    public AppearanceEditing(InventoryModel model, Func<Guid, WearableItemInfo?> itemInfo,
        Func<Guid, int, CancellationToken, Task<AssetFetchResult>> fetch, EditParamCatalog? catalog, Func<bool> isMale,
        Func<ActionIntent, ActionResult> submit)
    {
        _model = model;
        _itemInfo = itemInfo;
        _fetch = fetch;
        Catalog = catalog ?? EditParamCatalog.Empty;
        _isMale = isMale;
        _submit = submit;
    }

    /// <summary>Raised when the state, the editor's contents or the save state change.</summary>
    public event Action? Changed;

    /// <summary>Raised with the weights the avatar should be drawn with while an edit is open, and with null when the preview ends.</summary>
    public event Action<IReadOnlyDictionary<int, float>?>? PreviewChanged;

    /// <summary>The worn body parts and clothing (attachments are not wearables), as the outfit view lists them.</summary>
    public IReadOnlyList<OutfitEntry> Worn() => OutfitPlanner.Current(_model).Where(e => e.Section is 1 or 2).ToList();

    public EditState State { get; private set; } = EditState.Closed;
    public string Reason { get; private set; } = "";
    public Guid ItemId { get; private set; }
    public WearableEditor? Editor { get; private set; }
    public EditParamCatalog Catalog { get; }
    public bool IsMale => _isMale();
    public EditSaveState SaveState { get; private set; } = EditSaveState.Idle;
    public string SaveReason { get; private set; } = "";

    public bool CanSave => State == EditState.Ready && Editor is { CanModify: true } && SaveState != EditSaveState.Saving;
    public bool CanSaveAs => State == EditState.Ready && Editor is not null && _canCopy && SaveState != EditSaveState.Saving;
    public bool HasUnsavedChanges => Editor?.IsDirty ?? false;

    public async Task OpenAsync(Guid itemId)
    {
        int generation = ++_generation;
        Drop(clearPreview: true);
        State = EditState.Loading;
        ItemId = itemId;
        Reason = "";
        SaveState = EditSaveState.Idle;
        SaveReason = "";
        Changed?.Invoke();
        var info = _itemInfo(itemId);
        if (info is null)
        {
            Fail(generation, "unknown_item");
            return;
        }
        AssetFetchResult fetched;
        try
        {
            fetched = await _fetch(info.AssetId, info.AssetType, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Fail(generation, "asset_unavailable");
            return;
        }
        if (generation != _generation)
            return;   // another wearable was opened meanwhile
        if (fetched.Status != AssetFetchStatus.Ok)
        {
            Fail(generation, "asset_unavailable");
            return;
        }
        if (!WearableAsset.TryParse(fetched.Bytes.Span, out var asset, out _))
        {
            Fail(generation, "bad_asset");
            return;
        }
        _canCopy = info.CanCopy;
        var editor = new WearableEditor(asset, Catalog, info.CanModify);
        editor.Changed += OnEditorChanged;
        Editor = editor;
        State = EditState.Ready;
        Changed?.Invoke();
    }

    private void Fail(int generation, string reason)
    {
        if (generation != _generation)
            return;
        State = EditState.Failed;
        Reason = reason;
        Changed?.Invoke();
    }

    private void OnEditorChanged()
    {
        if (Editor is { } e)
            PreviewChanged?.Invoke(e.Values);
        Changed?.Invoke();
    }

    /// <summary>Lets go of the editor (and the preview, when asked) without changing its contents.</summary>
    private void Drop(bool clearPreview)
    {
        if (Editor is { } e)
            e.Changed -= OnEditorChanged;
        Editor = null;
        _savingAsCopy = false;
        if (clearPreview)
            PreviewChanged?.Invoke(null);
    }

    /// <summary>Close the editor: at once when nothing is unsaved, otherwise the caller asks Save, Don't Save or Cancel.</summary>
    public CloseVerdict RequestClose()
    {
        if (HasUnsavedChanges)
            return CloseVerdict.NeedsDecision;
        CloseNow();
        return CloseVerdict.Closed;
    }

    /// <summary>Don't Save: the edits are thrown away, the avatar returns to its saved look and the editor closes. Nothing is sent.</summary>
    public void DiscardAndClose()
    {
        Editor?.Revert();
        CloseNow();
    }

    private void CloseNow()
    {
        _generation++;
        Drop(clearPreview: true);
        State = EditState.Closed;
        Reason = "";
        Changed?.Invoke();
    }

    public bool Save()
    {
        if (!CanSave || Editor is not { IsDirty: true } e)
            return false;
        _savingAsCopy = false;
        return Send(new WearableSaveIntent(ItemId, e.Name, e.Type, e.ToBytes()));
    }

    public bool SaveAs(string name)
    {
        if (!CanSaveAs || Editor is not { } e)
            return false;
        var copy = e.Current.Clone();
        copy.Name = name;
        _pendingName = name;
        _savingAsCopy = true;
        return Send(new WearableSaveAsIntent(ItemId, FolderForCopy(e.Type), name, e.Type, copy.ToBytes()));
    }

    /// <summary>The folder a copy goes in: the original's, or the clothing / body part folder when the original sits in the Library.</summary>
    private Guid FolderForCopy(WearableType type)
    {
        var parent = _model.Item(ItemId)?.ParentId ?? Guid.Empty;
        if (parent != Guid.Empty && !_model.IsInLibrary(parent))
            return parent;
        return _model.FolderOfKind(OutfitPlanner.IsBodyPart(type) ? FolderKind.Bodypart : FolderKind.Clothing);
    }

    private bool Send(ActionIntent intent)
    {
        var result = _submit(intent);
        if (result.Sent)
        {
            SaveState = EditSaveState.Saving;
            SaveReason = "";
        }
        else
        {
            _savingAsCopy = false;
            SaveState = EditSaveState.Failed;
            SaveReason = result.Code ?? (result.Issues is { Count: > 0 } issues ? issues[0].Code : "failed");
        }
        Changed?.Invoke();
        return result.Sent;
    }

    /// <summary>The session says a save finished. Taken only for the save this model started.</summary>
    public void OnSaveFinished(WearableSaveFinished done)
    {
        if (State != EditState.Ready || Editor is not { } e || SaveState != EditSaveState.Saving)
            return;
        if (_savingAsCopy != done.AsCopy || (!done.AsCopy && done.ItemId != ItemId))
            return;
        if (done.Saved)
        {
            if (done.AsCopy)
            {
                ItemId = done.ItemId;
                e.Name = _pendingName;
            }
            e.Commit();
            SaveState = done.Rebaked ? EditSaveState.Saved : EditSaveState.SavedNotRebuilt;
            SaveReason = done.Reason;
        }
        else
        {
            SaveState = EditSaveState.Failed;
            SaveReason = done.Reason;
        }
        _savingAsCopy = false;
        Changed?.Invoke();
    }
}
