// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.Inventory;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;
using static Viewer.Core.Tests.ServerAppearanceTests;

namespace Viewer.Core.Tests;

/// <summary>
/// The outfit side of the adapter and the session: the rebuild request's body and retry waits, what a finished command does
/// to the session, that the own look still comes only from the grid's appearance message, and that the library's own wearing
/// methods (which bake and send a look) are never called. No grid; every member builds its own objects.
/// </summary>
public class OutfitAdapterTests
{
    private static readonly Guid Cof = Guid.Parse("00000000-0000-4000-8000-0000000000e1");
    private static readonly Guid Mine = Guid.Parse("00000000-0000-4000-8000-0000000000e2");
    private static readonly Guid RootId = Guid.Parse("00000000-0000-4000-8000-000000000001");

    [Fact]
    public void The_rebuild_request_carries_the_folders_version_and_nothing_else()
    {
        var body = LmvTransport.RefreshBody(12);
        Assert.Equal(new[] { "cof_version" }, body.Keys.ToArray());
        Assert.Equal(12, body["cof_version"].AsInteger());
    }

    [Fact]
    public void A_refused_rebuild_is_tried_again_after_one_three_seven_fifteen_and_thirty_one_seconds_five_times_at_most()
    {
        Assert.Equal(new double[] { 1, 3, 7, 15, 31 }, Enumerable.Range(1, LmvTransport.RefreshMaxRetries).Select(n => LmvTransport.RefreshRetryDelay(n).TotalSeconds).ToArray());
    }

    [Fact]
    public void Without_a_circuit_the_outfit_commands_are_refused_for_that_reason()
    {
        using var t = new LmvTransport(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "test"));
        var change = new OutfitChange(Cof, new[] { new NewOutfitLink(Guid.NewGuid(), "x", "@400") }, [], [], []);
        Assert.Equal(CommandDispatch.NotSent("not_connected"), t.Execute(new ChangeOutfitCommand(change)));
        Assert.Equal(CommandDispatch.NotSent("not_connected"), t.Execute(new SaveOutfitCommand(Cof, Mine, "x")));
    }

    private static (ViewerSession S, FakeTransport T) WithInventory()
    {
        var (s, t, _) = LoggedIn();
        t.Raise(new InventorySkeletonReceived(Me, RootId, Guid.NewGuid(), Guid.NewGuid(),
            new[] { new InventoryFolderData(RootId, Guid.Empty, "", FolderKind.Root, 1), new InventoryFolderData(Cof, RootId, "c", FolderKind.CurrentOutfit, 1),
                new InventoryFolderData(Mine, RootId, "m", FolderKind.MyOutfits, 1) }, Array.Empty<InventoryFolderData>()));
        return (s, t);
    }

    [Fact]
    public void A_finished_change_is_kept_announced_and_settles_the_current_outfit_folder_with_the_grid()
    {
        var (s, t) = WithInventory();
        var heard = new List<OutfitCommandFinished>();
        s.OutfitCommandDone += heard.Add;
        t.Raise(new OutfitCommandFinished(false, true, "", 7));
        Assert.Equal(new OutfitCommandFinished(false, true, "", 7), s.LastOutfitResult);
        Assert.Single(heard);
        Assert.Equal(Cof, Assert.Single(t.FolderRequests).Folder);
    }

    [Fact]
    public void A_finished_save_also_reads_my_outfits_again()
    {
        var (s, t) = WithInventory();
        t.Raise(new OutfitCommandFinished(true, true, "", 8));
        Assert.Contains(t.FolderRequests, f => f.Folder == Cof);
        Assert.Contains(t.FolderRequests, f => f.Folder == Mine);
    }

    [Fact]
    public void A_finished_save_also_reads_the_new_outfit_folder_and_the_root_so_what_the_grid_holds_is_what_shows()
    {
        var (s, t) = WithInventory();
        var outfit = Guid.Parse("00000000-0000-4000-8000-0000000000e9");
        t.Raise(new InventoryChanged(new[] { new InventoryFolderData(outfit, Mine, "o", FolderKind.Outfit, 1) }, Array.Empty<InventoryItemData>(), Array.Empty<Guid>(), Array.Empty<Guid>()));
        t.Raise(new InventoryFolderFetched(outfit, true, 1, 0, Array.Empty<InventoryFolderData>(), Array.Empty<InventoryItemData>()));   // a new folder looks empty
        t.FolderRequests.Clear();
        t.Raise(new OutfitCommandFinished(true, true, "", 8, outfit));
        Assert.Contains(t.FolderRequests, f => f.Folder == outfit);
        Assert.Contains(t.FolderRequests, f => f.Folder == RootId);
    }

    [Fact]
    public void A_finished_change_also_reads_the_folders_where_library_copies_landed()
    {
        var (s, t) = WithInventory();
        var clothing = Guid.Parse("00000000-0000-4000-8000-0000000000ea");
        t.Raise(new InventoryChanged(new[] { new InventoryFolderData(clothing, RootId, "c", FolderKind.Clothing, 1) }, Array.Empty<InventoryItemData>(), Array.Empty<Guid>(), Array.Empty<Guid>()));
        t.Raise(new InventoryFolderFetched(clothing, true, 1, 0, Array.Empty<InventoryFolderData>(), Array.Empty<InventoryItemData>()));
        t.FolderRequests.Clear();
        t.Raise(new OutfitCommandFinished(false, true, "", 9, Touched: new[] { clothing }));
        Assert.Contains(t.FolderRequests, f => f.Folder == Cof);
        Assert.Contains(t.FolderRequests, f => f.Folder == clothing);
    }

    [Fact]
    public void An_outfit_result_is_never_written_to_a_capture()
    {
        Assert.False(Viewer.Core.Replay.CaptureFile.IsRecorded(new OutfitCommandFinished(false, true, "", 1)));
    }

    [Fact]
    public void The_own_look_after_an_outfit_change_comes_from_the_grids_appearance_message_and_nothing_this_viewer_made()
    {
        var (s, t, _) = LoggedIn();
        Arrive(t, Alpha, serverBaked: true);
        var shirt = Guid.Parse("11111111-1111-1111-1111-111111111111");
        t.Raise(new OutfitCommandFinished(false, true, "", 9));   // the finished change itself draws nothing
        Assert.Empty(s.World.Self.ServerBakes);
        t.Raise(new AvatarAppearanceReceived(Alpha, Look(Me, 1, (8, shirt), (9, Guid.Parse("22222222-2222-2222-2222-222222222222")))));
        Assert.Contains(shirt, s.World.Self.ServerBakes.Values);
    }

    private static readonly string[] BakingMethods = ["AddToOutfit", "RemoveFromOutfit", "ReplaceOutfitAsync", "ReplaceOutfit", "WearOutfitAsync", "SendAgentIsNowWearing", "DelayedRequestSetAppearance"];

    /// <summary>Every call to one of the library's appearance-manager wearing methods in an assembly, as "Type::Method -> Name".</summary>
    private static List<string> WearingCalls(string assemblyPath)
    {
        var found = new List<string>();
        using var asm = Mono.Cecil.AssemblyDefinition.ReadAssembly(assemblyPath);
        foreach (var type in asm.MainModule.GetTypes())
            foreach (var m in type.Methods.Where(m => m.HasBody))
                foreach (var ins in m.Body.Instructions)
                    if (ins.Operand is Mono.Cecil.MethodReference mr && mr.DeclaringType.Name == "AppearanceManager" && BakingMethods.Contains(mr.Name))
                        found.Add($"{type.FullName}::{m.Name} -> {mr.Name}");
        return found;
    }

    [Fact]
    public void The_core_never_calls_the_librarys_own_wearing_methods_which_bake_and_send_a_look()
    {
        Assert.Empty(WearingCalls(TestPaths.CoreDll));
    }

    [Fact]
    public void Negative_control_the_same_scan_finds_those_calls_inside_the_library()
    {
        Assert.NotEmpty(WearingCalls(typeof(LibreMetaverse.GridClient).Assembly.Location));
    }
}
