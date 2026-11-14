// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The folder actions through the action pipeline: a new folder, a rename, a move and a move to the Trash. Each is checked
/// before it is sent and becomes exactly one command. Nothing purges. Offline, through the fake transport; nothing here
/// touches process-wide state, so the class runs in parallel with the others.
/// </summary>
public class InventoryFolderIntentTests
{
    private static readonly Guid Parent = Guid.Parse("a1b2c3d4-0000-4000-8000-0000000000b1");
    private static readonly Guid Folder = Guid.Parse("a1b2c3d4-0000-4000-8000-0000000000b2");
    private static readonly Guid Trash = Guid.Parse("a1b2c3d4-0000-4000-8000-0000000000b3");

    private static (ActionPipeline Pipeline, FakeTransport Transport, ListSink Sink) Rig()
    {
        var t = new FakeTransport();
        var sink = new ListSink();
        return (new ActionPipeline(() => t, () => true, new CoreLog(sink, "test")), t, sink);
    }

    [Fact]
    public void Each_folder_intent_becomes_its_command_with_the_name_cleaned()
    {
        var (p, t, _) = Rig();
        Assert.Equal(ActionStatus.Sent, p.Submit(new InventoryFolderCreateIntent(Parent, "  New Folder ")).Status);
        Assert.Equal(ActionStatus.Sent, p.Submit(new InventoryFolderRenameIntent(Folder, Parent, -1, " Plans | draft ")).Status);
        Assert.Equal(ActionStatus.Sent, p.Submit(new InventoryFolderMoveIntent(Folder, Parent)).Status);
        Assert.Equal(ActionStatus.Sent, p.Submit(new InventoryFolderTrashIntent(Folder, Trash, true)).Status);
        Assert.Equal(new TransportCommand[]
        {
            new CreateInventoryFolderCommand(Parent, "New Folder"),
            new RenameInventoryFolderCommand(Folder, Parent, -1, "Plans   draft"),   // the grid's separator character becomes a space
            new MoveInventoryFolderCommand(Folder, Parent),
            new MoveInventoryFolderCommand(Folder, Trash),
        }, t.Commands.ToArray());
    }

    public static TheoryData<ActionIntent, string> Bad => new()
    {
        { new InventoryFolderCreateIntent(Guid.Empty, "x"), "inventory.no_folder" },
        { new InventoryFolderCreateIntent(Parent, "   "), "inventory.bad_name" },
        { new InventoryFolderCreateIntent(Parent, new string('n', 64)), "inventory.bad_name" },
        { new InventoryFolderCreateIntent(Parent, "a\u0007b"), "inventory.bad_name" },
        { new InventoryFolderRenameIntent(Guid.Empty, Parent, -1, "x"), "inventory.no_folder" },
        { new InventoryFolderRenameIntent(Folder, Parent, -1, ""), "inventory.bad_name" },
        { new InventoryFolderMoveIntent(Guid.Empty, Parent), "inventory.no_folder" },
        { new InventoryFolderMoveIntent(Folder, Guid.Empty), "inventory.no_folder" },
        { new InventoryFolderMoveIntent(Folder, Folder), "inventory.same_id" },
        { new InventoryFolderTrashIntent(Guid.Empty, Trash, true), "inventory.no_folder" },
        { new InventoryFolderTrashIntent(Folder, Guid.Empty, true), "inventory.no_trash" },
        { new InventoryFolderTrashIntent(Folder, Folder, true), "inventory.same_id" },
        { new InventoryFolderTrashIntent(Folder, Trash, false), "inventory.not_confirmed" },
    };

    [Theory, MemberData(nameof(Bad))]
    public void A_bad_folder_intent_is_refused_with_its_code_and_nothing_is_sent(ActionIntent intent, string code)
    {
        var (p, t, _) = Rig();
        var r = p.Submit(intent);
        Assert.Equal(ActionStatus.Invalid, r.Status);
        Assert.Contains(code, r.Issues.Select(i => i.Code));
        Assert.Equal(0, t.ExecuteCalls);
    }

    [Fact]
    public void A_name_of_the_longest_allowed_length_is_sent()
    {
        var (p, _, _) = Rig();
        Assert.Equal(ActionStatus.Sent, p.Submit(new InventoryFolderCreateIntent(Parent, new string('n', 63))).Status);
    }

    [Fact]
    public void Folder_names_never_reach_the_log()
    {
        var (p, _, sink) = Rig();
        p.Submit(new InventoryFolderCreateIntent(Parent, "Zebra Plans 4471"));
        p.Submit(new InventoryFolderRenameIntent(Folder, Parent, -1, "Zebra Plans 4471"));
        Assert.DoesNotContain("Zebra", sink.Text);
    }

    [Fact]
    public void The_folder_kinds_are_known_to_the_pipeline_and_none_of_them_empties_the_trash()
    {
        foreach (var intent in new ActionIntent[]
        {
            new InventoryFolderCreateIntent(Parent, "x"), new InventoryFolderRenameIntent(Folder, Parent, -1, "x"),
            new InventoryFolderMoveIntent(Folder, Parent), new InventoryFolderTrashIntent(Folder, Trash, true),
        })
            Assert.DoesNotContain("purge", intent.Kind.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, new[] { ActionKind.InventoryFolderCreate, ActionKind.InventoryFolderRename, ActionKind.InventoryFolderMove, ActionKind.InventoryFolderTrash }.Distinct().Count());
    }
}
