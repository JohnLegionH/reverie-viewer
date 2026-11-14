// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Inventory;
using Viewer.Shell.Inventory;

namespace Viewer.Shell.Addons;

/// <summary>
/// Which notecards add-ons may read: only one the user handed over, by dropping it, or a folder holding it, on an add-on's drop
/// zone in this session, and only one the user could open in the notecard window (<see cref="NotecardRules.CanRead"/>). The text is
/// the card's plain text (embedded items as their names); add-ons never see the file or any asset.
/// </summary>
public sealed class AddonNotecards(InventoryModel model, Func<Guid, CancellationToken, Task<NotecardOpenResult>> open)
{
    private readonly InventoryModel _model = model;
    private readonly Func<Guid, CancellationToken, Task<NotecardOpenResult>> _open = open;

    private readonly HashSet<Guid> _handed = [];
    private readonly object _gate = new();

    /// <summary>The user dropped these items or folders on an add-on's drop zone.</summary>
    public void Dropped(IEnumerable<Guid> ids)
    {
        lock (_gate)
            _handed.UnionWith(ids.Where(id => id != Guid.Empty));
    }

    /// <summary>True when the item is a dropped notecard or lies inside a dropped folder.</summary>
    public bool IsHandedOver(Guid itemId)
    {
        Guid[] handed;
        lock (_gate)
            handed = [.. _handed];
        return handed.Any(h => h == itemId || (_model.Folder(h) is not null && _model.IsDescendantOf(itemId, h)));
    }

    public async Task<string?> ReadTextAsync(Guid itemId, CancellationToken ct = default)
    {
        if (!IsHandedOver(itemId))
            return null;
        var opened = await _open(itemId, ct).ConfigureAwait(false);
        return opened is { Status: NotecardOpenStatus.Ok, Document: { } doc } ? doc.PlainText : null;
    }
}
