// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Session;

/// <summary>
/// A transport that can make plain folders and links in the agent's own inventory and wait for the grid to hold them. A
/// development aid: test tools use it to lay out inventory a test needs. User commands go through the action pipeline.
/// </summary>
public interface IInventoryBuilder
{
    /// <summary>Makes a plain folder under a folder of the agent's inventory; the new folder's id, or empty when the grid did not answer for it.</summary>
    Task<Guid> CreatePlainFolderAsync(Guid parent, string name);

    /// <summary>Makes links in a folder to items the inventory holds, each with a description; answers how many targets were found.</summary>
    Task<int> CreateItemLinksAsync(Guid folder, IReadOnlyList<(Guid Target, string Description)> links);
}
