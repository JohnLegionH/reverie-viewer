// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using Viewer.Core.Diagnostics;
using Viewer.Core.Inventory;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Xunit;
using LmvStore = LibreMetaverse.Inventory;

namespace Viewer.Core.Tests;

/// <summary>
/// The adapter's translation of the library's inventory objects into the core's records, the login skeleton built from the
/// library's store, and the folder commands. Library objects are built by hand; no grid. Every member builds its own
/// objects, so the class runs in parallel.
/// </summary>
public class InventoryAdapterTests
{
    private static readonly UUID Agent = new("11111111-2222-3333-4444-555555555555");
    private static readonly UUID LibOwner = new("99999999-8888-7777-6666-555555555555");
    private static readonly UUID Root = new("00000000-0000-4000-8000-000000000001");
    private static readonly UUID Sub = new("00000000-0000-4000-8000-000000000002");
    private static readonly UUID Deep = new("00000000-0000-4000-8000-000000000003");
    private static readonly UUID LibRoot = new("00000000-0000-4000-8000-0000000000a1");
    private static readonly UUID LibSub = new("00000000-0000-4000-8000-0000000000a2");

    [Fact]
    public void A_folder_keeps_its_ids_name_kind_version_and_count()
    {
        var f = new InventoryFolder(Sub) { ParentUUID = Root, Name = "Textures", PreferredType = FolderType.Texture, Version = 3, DescendentCount = 12 };
        var d = LmvTransport.ToFolder(f);
        Assert.Equal(new InventoryFolderData(Sub.Guid, Root.Guid, "Textures", FolderKind.Texture, 3, 12), d);
    }

    [Fact]
    public void A_folder_kind_the_viewer_does_not_know_is_an_ordinary_folder()
    {
        var f = new InventoryFolder(Sub) { ParentUUID = Root, Name = "Odd", PreferredType = (FolderType)31 };
        Assert.Equal(FolderKind.Other, LmvTransport.ToFolder(f).Kind);
    }

    [Fact]
    public void An_item_keeps_its_type_flags_and_time_and_a_link_names_its_target()
    {
        var target = new UUID("00000000-0000-4000-8000-0000000000d1");
        var note = new InventoryNotecard(new UUID("00000000-0000-4000-8000-0000000000c1")) { ParentUUID = Sub, Name = "Note", AssetType = AssetType.Notecard, InventoryType = InventoryType.Notecard, Flags = 5, CreationDate = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc) };
        var d = LmvTransport.ToItem(note);
        Assert.Equal((ItemKind.Notecard, 7, 7, 5u, null), (d.Kind, d.AssetType, d.InventoryType, d.Flags, d.LinkedId));
        Assert.Equal(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero), d.CreatedAt);
        Assert.Equal("Note", d.Name);

        var link = new InventoryNotecard(new UUID("00000000-0000-4000-8000-0000000000c2")) { ParentUUID = Sub, Name = "Link", AssetType = AssetType.Link, InventoryType = InventoryType.Notecard, AssetUUID = target };
        var l = LmvTransport.ToItem(link);
        Assert.Equal(ItemKind.Link, l.Kind);
        Assert.Equal(target.Guid, l.LinkedId);
    }

    [Fact]
    public void An_unset_creation_time_is_unknown_rather_than_year_one()
    {
        var item = new InventoryNotecard(new UUID("00000000-0000-4000-8000-0000000000c3")) { ParentUUID = Sub, Name = "n", AssetType = AssetType.Notecard };
        Assert.Null(LmvTransport.ToItem(item).CreatedAt);
    }

    [Fact]
    public void Changes_and_removals_name_the_right_kind_of_object()
    {
        var f = new InventoryFolder(Sub) { ParentUUID = Root, Name = "F" };
        var i = new InventoryNotecard(new UUID("00000000-0000-4000-8000-0000000000c4")) { ParentUUID = Sub, Name = "I", AssetType = AssetType.Notecard };
        var cf = LmvTransport.ChangedFor(f);
        Assert.Single(cf.Folders); Assert.Empty(cf.Items);
        var ci = LmvTransport.ChangedFor(i);
        Assert.Single(ci.Items); Assert.Empty(ci.Folders);
        var rf = LmvTransport.RemovedFor(f);
        Assert.Equal(new[] { Sub.Guid }, rf.RemovedFolders); Assert.Empty(rf.RemovedItems);
        var ri = LmvTransport.RemovedFor(i);
        Assert.Equal(new[] { i.UUID.Guid }, ri.RemovedItems); Assert.Empty(ri.RemovedFolders);
    }

    [Fact]
    public void The_skeleton_is_built_from_the_library_store_for_both_trees()
    {
        using var client = new GridClient();
        var store = new LmvStore(client, Agent);
        store.RootFolder = new InventoryFolder(Root) { Name = "", ParentUUID = UUID.Zero };
        store.LibraryFolder = new InventoryFolder(LibRoot) { Name = "", ParentUUID = UUID.Zero };
        store.UpdateNodeFor(new InventoryFolder(Sub) { ParentUUID = Root, Name = "Textures", PreferredType = FolderType.Texture, Version = 3, OwnerID = Agent });
        store.UpdateNodeFor(new InventoryFolder(Deep) { ParentUUID = Sub, Name = "Deeper", Version = 1, OwnerID = Agent });
        store.UpdateNodeFor(new InventoryFolder(LibSub) { ParentUUID = LibRoot, Name = "Objects", PreferredType = FolderType.Object, Version = 1, OwnerID = LibOwner });
        var data = new LoginResponseData { AgentID = Agent, InventoryRoot = Root, LibraryRoot = LibRoot, LibraryOwner = LibOwner };

        var s = LmvTransport.BuildSkeleton(data, store);
        Assert.Equal((Agent.Guid, Root.Guid, LibOwner.Guid, LibRoot.Guid), (s.OwnerId, s.RootId, s.LibraryOwnerId, s.LibraryRootId));
        Assert.Equal(new[] { Root.Guid, Sub.Guid, Deep.Guid }, s.Folders.Select(f => f.Id).ToArray());
        Assert.Equal(FolderKind.Root, s.Folders[0].Kind);
        Assert.Equal(new[] { LibRoot.Guid, LibSub.Guid }, s.LibraryFolders.Select(f => f.Id).ToArray());
        Assert.Equal(FolderKind.Texture, s.Folders[1].Kind);

        var model = new InventoryModel();
        model.Apply(s);
        Assert.True(model.IsInLibrary(LibSub.Guid));
        Assert.False(model.IsInLibrary(Deep.Guid));
        Assert.Equal(Sub.Guid, model.FolderOfKind(FolderKind.Texture));
    }

    [Fact]
    public void Without_a_circuit_a_folder_request_answers_failed_at_once_so_nothing_waits_forever()
    {
        using var t = new LmvTransport(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "test"));
        var seen = new List<TransportEvent>();
        t.Event += seen.Add;
        ((IInventoryRequester)t).RequestFolderContents(Sub.Guid, Agent.Guid);
        var fetched = Assert.IsType<InventoryFolderFetched>(Assert.Single(seen));
        Assert.False(fetched.Ok);
        Assert.Equal(Sub.Guid, fetched.FolderId);
    }

    [Fact]
    public void The_adapter_knows_the_folder_commands_and_without_a_circuit_refuses_them_for_that_reason()
    {
        using var t = new LmvTransport(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "test"));
        foreach (TransportCommand c in new TransportCommand[]
        {
            new CreateInventoryFolderCommand(Root.Guid, "x"), new RenameInventoryFolderCommand(Sub.Guid, Root.Guid, -1, "x"), new MoveInventoryFolderCommand(Sub.Guid, Root.Guid),
        })
            Assert.Equal(CommandDispatch.NotSent("not_connected"), t.Execute(c));
    }
}
