// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;

namespace Viewer.Core.Protocol;

/// <summary>
/// Reading what is inside an object, copying it out, buying and paying, the protocol adapter's side. The library reads the contents
/// the way the official viewer does: through the region's task-inventory capability when it has one, otherwise the task-inventory
/// request and the file transfer that follows it.
/// </summary>
internal sealed partial class LmvTransport
{
    /// <summary>How long the contents of an object are waited for before the window says they could not be read.</summary>
    internal static readonly TimeSpan TaskInventoryTimeout = TimeSpan.FromSeconds(30);


    /// <summary>The core's record of an item inside an object.</summary>
    internal static TaskItem ToTaskItem(InventoryItem item) =>
        new(item.UUID.Guid, item.Name ?? "", (int)item.AssetType, (int)item.InventoryType, item.OwnerID.Guid,
            (uint)item.Permissions.OwnerMask, (uint)item.Permissions.EveryoneMask);

    /// <summary>The pay-price answer as the core's record.</summary>
    internal static PayPriceReceived ToPayPrice(UUID objectId, int defaultPrice, int[]? buttons) =>
        new(objectId.Guid, defaultPrice, buttons ?? []);

    private void OnPayPriceReply(object? sender, PayPriceReplyEventArgs e) => Raise(ToPayPrice(e.ObjectID, e.DefaultPrice, e.ButtonPrices));

    private async Task ReadTaskInventoryAsync(uint localId, Guid objectId)
    {
        var sim = _client.Network.CurrentSim;
        if (sim is null)
        {
            Raise(new TaskInventoryReceived(objectId, [], false));
            return;
        }
        using var cts = new CancellationTokenSource(TaskInventoryTimeout);
        try
        {
            var found = await _client.Inventory.GetTaskInventoryAsync(new UUID(objectId), localId, sim, cts.Token).ConfigureAwait(false);
            // a timeout ends the library's wait with an empty list: that is a failure, not an empty object
            if (cts.IsCancellationRequested)
            {
                Raise(new TaskInventoryReceived(objectId, [], false));
                return;
            }
            Raise(new TaskInventoryReceived(objectId, found.OfType<InventoryItem>().Select(ToTaskItem).ToArray()));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("command.failed", CoreLog.F("command", "task-inventory"), CoreLog.F("error", ex.GetType().Name));
            Raise(new TaskInventoryReceived(objectId, [], false));
        }
    }

    private CommandDispatch ExecuteTrade(TransportCommand command)
    {
        if (_client.Network.CurrentSim is not { } sim)
            return CommandDispatch.NotSent("no_region");
        var inv = _client.Inventory;
        UUID root = inv.Store?.RootFolder?.UUID ?? UUID.Zero;
        if (root == UUID.Zero && command is CopyTaskContentsCommand or BuyObjectCommand)
            return CommandDispatch.NotSent("no_inventory");
        switch (command)
        {
            case RequestTaskInventoryCommand read:
                Run("task-inventory", () => ReadTaskInventoryAsync(read.LocalId, read.ObjectId));
                break;
            case CopyTaskContentsCommand copy:
            {
                // a new folder named after the object (under the clothing folder when the items are to be worn, else at the top), then each
                // item is moved out of the object into it; the region copies the ones that are copies and moves the others
                var parent = copy.Wear ? inv.FindFolderForType(FolderType.Clothing) : root;
                var folder = inv.CreateFolder(parent, copy.FolderName, FolderType.None);
                foreach (var item in copy.ItemIds)
                    inv.MoveTaskInventory(copy.LocalId, new UUID(item), folder, sim);
                Raise(new ContentsCopied(copy.ObjectId, folder.Guid, copy.ItemIds.Count, copy.Wear));
                break;
            }
            case BuyObjectCommand buy:
            {
                // a bought object goes to the Objects folder; bought contents go to the top of the inventory (llfloaterbuy.cpp, llfloaterbuycontents.cpp)
                var folder = buy.SaleType == 3 ? root : inv.FindFolderForType(FolderType.Object);
                _client.Objects.BuyObject(sim, buy.LocalId, (SaleType)buy.SaleType, buy.Price, _client.Self.ActiveGroup, folder);
                break;
            }
            case RequestPayPriceCommand ask:
                _client.Objects.RequestPayPrice(sim, new UUID(ask.ObjectId));
                break;
            case PayObjectCommand pay:
                _client.Self.GiveObjectMoney(new UUID(pay.ObjectId), pay.Amount, pay.ObjectName);
                break;
        }
        return CommandDispatch.Handed;
    }
}
