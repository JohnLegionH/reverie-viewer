// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using Viewer.Core.Session;

namespace Viewer.Core.Protocol;

/// <summary>The development aid of <see cref="IInventoryBuilder"/>: plain folders and links made through the library's inventory.</summary>
internal sealed partial class LmvTransport : IInventoryBuilder
{
    /// <summary>The longest a new folder is waited for.</summary>
    private static readonly TimeSpan BuiltFolderWait = TimeSpan.FromSeconds(20);

    public async Task<Guid> CreatePlainFolderAsync(Guid parent, string name)
    {
        if (_disposed || !_client.Network.Connected)
            return Guid.Empty;
        var inv = _client.Inventory;
        UUID id = inv.CreateFolder(new UUID(parent), name);
        var until = DateTime.UtcNow + BuiltFolderWait;
        while (DateTime.UtcNow < until && !_disposed)
        {
            // the folder is asked for until the grid answers for it
            var answer = await inv.RequestFolderContentsAsync(id, _client.Self.AgentID, true, true, InventorySortOrder.ByName, CancellationToken.None).ConfigureAwait(false);
            if (answer is not null && Stored(id.Guid) is InventoryFolder { Version: >= 0 })
                return id.Guid;
            await Task.Delay(250).ConfigureAwait(false);
        }
        return Guid.Empty;
    }

    public async Task<int> CreateItemLinksAsync(Guid folder, IReadOnlyList<(Guid Target, string Description)> links)
    {
        if (_disposed || !_client.Network.Connected)
            return 0;
        var items = new List<(InventoryBase, string)>();
        foreach (var (target, description) in links)
        {
            if (Stored(target) is { } held)
                items.Add((held, description));
        }
        if (items.Count > 0)
            await _client.Inventory.CreateLinksAsync(new UUID(folder), items, null, CancellationToken.None).ConfigureAwait(false);
        return items.Count;
    }
}
