// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Appearance;
using Viewer.Core.Inventory;
using Viewer.Core.Session;

namespace Viewer.Godot;

/// <summary>
/// Inventory › New in the running viewer, as the official viewer does it (llviewerinventory.cpp menu_create_inventory_item and its
/// callbacks): the item goes into the folder chosen (the folder selected, or the folder of the item selected), else into the account's
/// folder for its type; a new gesture is activated and opens in its editor (create_gesture_cb), a new notecard or script opens nothing
/// (create_notecard_cb, create_script_cb); a new clothing or body part item opens in the appearance editor where this viewer has one for
/// that type, and otherwise the inventory window says it was made and that there is no editor for it yet.
/// </summary>
public partial class Main
{
    private bool _newItemsHooked;

    private void AttachNewItems()
    {
        if (_inventoryPanel is null || _newItemsHooked)
            return;
        _inventoryPanel.NewRequested += OnNewRequested;
        _newItemsHooked = true;
    }

    /// <summary>The types the appearance editor has a panel for (EditParamCatalog's tabs); alpha, tattoo and universal it has not yet.</summary>
    private static bool HasWearableEditor(WearableType t) => t is not (WearableType.Alpha or WearableType.Tattoo or WearableType.Universal);

    private void OnNewRequested(string id, Guid selected)
    {
        if (_inventoryFor is not { IsEnded: false } session || _inventoryPanel is null || _strings is null || NewItemCatalog.Of(id) is not { } entry)
            return;
        if (session.ItemCreation is not { } transport)
        {
            _inventoryPanel.StatusLabel.Text = _strings.Get("inventory.new.failed", entry.DefaultName);
            return;
        }
        var inv = session.Inventory;
        Guid folder = inv.Folder(selected) is not null ? selected : inv.Item(selected)?.ParentId ?? Guid.Empty;
        if (folder == Guid.Empty || inv.IsInLibrary(folder))
            folder = transport.FolderForAssetType(entry.AssetType);
        if (folder == Guid.Empty)
            folder = inv.RootId;
        byte[]? data = entry.IsWearable ? NewItemCatalog.DefaultWearable(entry.Wearable, entry.DefaultName, EditCatalog() ?? EditParamCatalog.Empty, session.AgentId) : null;
        var request = new NewItemRequest(folder, entry.DefaultName, NewItemCatalog.DescriptionFor(entry, DateTime.Now), entry.AssetType, entry.InventoryType, entry.Wearable, entry.NextOwnerMask, data);
        _inventoryPanel.StatusLabel.Text = _strings.Get("inventory.new.making", entry.DefaultName);
        Log.Info("inventory.new", Viewer.Core.Diagnostics.CoreLog.F("entry", entry.Id));
        _ = transport.CreateItemAsync(request, CancellationToken.None).ContinueWith(t =>
        {
            Guid made = t.IsCompletedSuccessfully ? t.Result : Guid.Empty;
            Callable.From(() => NewItemMade(session, entry, folder, made)).CallDeferred();
        }, TaskScheduler.Default);
    }

    private void NewItemMade(ViewerSession session, NewItemEntry entry, Guid folder, Guid made)
    {
        if (session.IsEnded || _inventoryPanel is null || _strings is null)
            return;
        if (made == Guid.Empty)
        {
            _inventoryPanel.StatusLabel.Text = _strings.Get("inventory.new.failed", entry.DefaultName);
            return;
        }
        session.Inventory.Invalidate(folder);   // the folder's contents are read again, so the new item shows with the grid's own record of it
        // the official viewer opens a new gesture (create_gesture_cb) and nothing else; a new wearable opens in the appearance editor here
        bool opens = entry.Kind is ItemKind.Gesture || entry.IsWearable && HasWearableEditor(entry.Wearable);
        _inventoryPanel.StatusLabel.Text = _strings.Get(opens || !entry.IsWearable ? "inventory.new.made" : "inventory.new.made_no_editor", entry.DefaultName);
        Log.Info("inventory.new-made", Viewer.Core.Diagnostics.CoreLog.F("entry", entry.Id), Viewer.Core.Diagnostics.CoreLog.F("opens", opens));
        if (opens)
            OpenWhenKnown(session, entry, made, 0);
    }

    /// <summary>Opens a new item in its editor once the inventory knows it (its folder is being read again), trying for up to ten seconds.</summary>
    private void OpenWhenKnown(ViewerSession session, NewItemEntry entry, Guid item, int tries)
    {
        if (session.IsEnded || _shell is null)
            return;
        if (session.Inventory.Item(item) is null)
        {
            if (tries < 20)
                GetTree().CreateTimer(0.5).Timeout += () => OpenWhenKnown(session, entry, item, tries + 1);
            return;
        }
        switch (entry.Kind)
        {
            case ItemKind.Notecard:
                _notecards ??= NewNotecards(_shell);
                _notecards.OpenItem(item, entry.DefaultName);
                break;
            case ItemKind.Script:
                Scripts().OpenInventory(item, entry.DefaultName);
                break;
            case ItemKind.Gesture:
                ActivateNewGesture(item);
                OpenGesture(item);
                break;
            default:
                if (_appearancePanel is { } panel)
                {
                    _shell.Shell.Floaters.Open(AppearanceFeature.FloaterId);
                    _ = panel.OpenAsync(item);
                }
                break;
        }
    }
}
