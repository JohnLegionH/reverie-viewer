// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse.StructuredData;

namespace Viewer.Core.Protocol;

/// <summary>
/// Reads the runtime an inventory item names for its script out of an inventory answer. The official viewer keeps it in the item's metadata
/// (indra/llinventory/llinventory.cpp: metadata.script.runtime) and its script editor starts a script in the inventory with it; the protocol
/// library does not keep it, so the tap reads it from the answer on its way past.
/// </summary>
internal static class LmvInventoryRuntime
{
    private const int MaxDepth = 12, MaxNodes = 50_000;

    /// <summary>Every item in the answer that names a script runtime, wherever the items sit in it (a folder's list, an embedded list).</summary>
    internal static IReadOnlyList<(Guid ItemId, string Runtime)> Read(OSD answer)
    {
        var found = new List<(Guid, string)>();
        int nodes = 0;
        void Walk(OSD node, int depth)
        {
            if (depth > MaxDepth || ++nodes > MaxNodes)
                return;
            switch (node)
            {
                case OSDMap map:
                    if (map.TryGetValue("item_id", out var id) && map.TryGetValue("metadata", out var meta) && meta is OSDMap m
                        && m.TryGetValue("script", out var script) && script is OSDMap s && s.TryGetValue("runtime", out var runtime)
                        && runtime.AsString() is { Length: > 0 } text && id.AsUUID().Guid != Guid.Empty)
                        found.Add((id.AsUUID().Guid, text));
                    foreach (var child in map.Values)
                        Walk(child, depth + 1);
                    break;
                case OSDArray array:
                    foreach (var child in array)
                        Walk(child, depth + 1);
                    break;
            }
        }
        Walk(answer, 0);
        return found;
    }
}
