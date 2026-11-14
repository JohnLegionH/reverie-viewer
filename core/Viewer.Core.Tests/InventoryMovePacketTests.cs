// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Viewer.Core.Protocol;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// An inventory move goes to the grid as the official viewer sends it: the MoveInventoryItem or MoveInventoryFolder message
/// (llviewerinventory.cpp, updateParentOnServer: Stamp false from a drag, the item or folder and its new parent, and an
/// empty new name), never as an inventory-capability PATCH of the item's parent. A grid may answer such a PATCH with
/// success and leave the item where it was. Pure functions and a read of the source, so the class runs in parallel.
/// </summary>
public class InventoryMovePacketTests
{
    private static readonly UUID Agent = new("6f1c2a64-1d5b-4d2e-9a43-0b8f2d6c7e11");
    private static readonly UUID Session = new("2b7d9e10-4c3a-4f58-8e21-5a6b7c8d9e02");
    private static readonly Guid ItemId = Guid.Parse("8c0e4a71-2f3b-4d6c-9e15-7a2b3c4d5e63");
    private static readonly Guid FolderId = Guid.Parse("1e2d3c4b-5a69-4788-96a5-b4c3d2e1f004");

    [Fact]
    public void An_item_move_is_one_MoveInventoryItem_block_with_no_stamp_and_an_empty_name()
    {
        var p = InventoryMovePackets.Item(Agent, Session, ItemId, FolderId, null);
        Assert.Equal(Agent, p.AgentData.AgentID);
        Assert.Equal(Session, p.AgentData.SessionID);
        Assert.False(p.AgentData.Stamp);
        var block = Assert.Single(p.InventoryData);
        Assert.Equal(new UUID(ItemId), block.ItemID);
        Assert.Equal(new UUID(FolderId), block.FolderID);
        Assert.Equal("", Utils.BytesToString(block.NewName ?? Array.Empty<byte>()));
        Assert.True(p.Header.Reliable);
    }

    [Fact]
    public void An_item_move_with_a_new_name_carries_the_name()
    {
        var p = InventoryMovePackets.Item(Agent, Session, ItemId, FolderId, "Memo");
        var block = Assert.Single(p.InventoryData);
        Assert.Equal("Memo", Utils.BytesToString(block.NewName));
    }

    [Fact]
    public void A_folder_move_is_one_MoveInventoryFolder_block_with_no_stamp()
    {
        var p = InventoryMovePackets.Folder(Agent, Session, ItemId, FolderId);
        Assert.Equal(Agent, p.AgentData.AgentID);
        Assert.Equal(Session, p.AgentData.SessionID);
        Assert.False(p.AgentData.Stamp);
        var block = Assert.Single(p.InventoryData);
        Assert.Equal(new UUID(ItemId), block.FolderID);
        Assert.Equal(new UUID(FolderId), block.ParentID);
        Assert.True(p.Header.Reliable);
    }

    [Fact]
    public void The_protocol_adapter_never_moves_through_the_library_whose_moves_go_by_the_inventory_capability()
    {
        string dir = Path.Combine(TestPaths.RepoRoot, "core", "Viewer.Core", "Protocol");
        var calls = new Regex(@"\b(?:Inventory|inv)\.(MoveItem|MoveItems|MoveFolder|MoveFolders)\s*\(");
        var found = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadAllLines(f).Select((line, n) => (f, n, line)))
            .Where(x => calls.IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.n + 1}")
            .ToList();
        Assert.Empty(found);
        string sources = string.Concat(Directory.GetFiles(dir, "*.cs").Select(File.ReadAllText));
        Assert.Contains("InventoryMovePackets.Item(", sources);
        Assert.Contains("InventoryMovePackets.Folder(", sources);
    }
}
