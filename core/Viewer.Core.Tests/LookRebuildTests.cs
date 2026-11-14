// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.Inventory;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Xunit;
using static Viewer.Core.Tests.ServerAppearanceTests;

namespace Viewer.Core.Tests;

/// <summary>
/// When the region's server-side baking is asked to rebuild the look. The official viewer asks once at login, whenever the region
/// bakes on the server (llstartup.cpp set_flags_and_update_appearance calls LLAppearanceMgr::updateAppearanceFromCOF, which calls
/// requestServerAppearanceUpdate when the region has a central bake version), so a look the region holds from an older outfit is
/// rebuilt from the current outfit folder. It also asks after every outfit change, objects put on or taken off included
/// (LLUpdateAppearanceOnDestroy, LLRequestServerAppearanceUpdateOnDestroy). No grid; every member builds its own objects.
/// </summary>
public class LookRebuildTests
{
    private static readonly Guid Cof = Guid.Parse("00000000-0000-4000-8000-0000000000c1");
    private static readonly Guid RootId = Guid.Parse("00000000-0000-4000-8000-0000000000c0");
    private static readonly SimRef Other = new(257024UL << 32 | 256000UL, "Other");

    private static void Skeleton(FakeTransport t) =>
        t.Raise(new InventorySkeletonReceived(Me, RootId, Guid.NewGuid(), Guid.NewGuid(),
            new[] { new InventoryFolderData(RootId, Guid.Empty, "", FolderKind.Root, 1), new InventoryFolderData(Cof, RootId, "c", FolderKind.CurrentOutfit, 7) },
            Array.Empty<InventoryFolderData>()));

    private static List<RebuildLookCommand> Rebuilds(FakeTransport t)
    {
        lock (t.Commands) return t.Commands.OfType<RebuildLookCommand>().ToList();
    }

    [Fact]
    public void At_login_in_a_region_that_bakes_on_the_server_the_look_is_rebuilt_once_from_the_current_outfit_folder()
    {
        var (s, t, _) = LoggedIn();
        using var _s = s;
        Skeleton(t);
        Arrive(t, Alpha, serverBaked: true);
        Assert.Equal(new[] { new RebuildLookCommand(Cof) }, Rebuilds(t));
    }

    [Fact]
    public void When_the_outfit_folder_is_known_only_after_arrival_the_rebuild_is_asked_for_then()
    {
        var (s, t, _) = LoggedIn();
        using var _s = s;
        Arrive(t, Alpha, serverBaked: true);
        Assert.Empty(Rebuilds(t));   // nothing to rebuild from yet
        Skeleton(t);
        Assert.Equal(new[] { new RebuildLookCommand(Cof) }, Rebuilds(t));
    }

    [Fact]
    public void A_login_region_that_bakes_on_the_client_gets_no_rebuild_request_and_a_later_teleport_does_not_add_one()
    {
        var (s, t, _) = LoggedIn();
        using var _s = s;
        Skeleton(t);
        Arrive(t, Alpha, serverBaked: false);
        t.Raise(new SimConnected(Other, 256, 256, ServerBakedAppearance: true));
        Assert.Empty(Rebuilds(t));
    }

    [Fact]
    public void A_teleport_after_login_does_not_ask_again()
    {
        var (s, t, _) = LoggedIn();
        using var _s = s;
        Skeleton(t);
        Arrive(t, Alpha, serverBaked: true);
        t.Raise(new SimConnected(Other, 256, 256, ServerBakedAppearance: true));
        Skeleton(t);
        Assert.Single(Rebuilds(t));
    }

    [Fact]
    public void The_login_rebuild_result_is_kept_and_logged_and_is_not_an_outfit_command_result()
    {
        var (s, t, log) = LoggedIn();
        using var _s = s;
        t.Raise(new LookRebuildFinished(true, "", 835));
        Assert.Equal(new LookRebuildFinished(true, "", 835), s.LastLookRebuild);
        Assert.Null(s.LastOutfitResult);
        Assert.Contains(log.Entries.Select(e => e.Format()), l => l.Contains("appearance.login-rebuild") && l.Contains("cof=835") && l.Contains("ok=True"));
    }

    [Fact]
    public void Every_outfit_change_that_does_anything_asks_for_a_rebuild_objects_alone_included_and_an_empty_one_does_not()
    {
        var item = Guid.NewGuid();
        Assert.True(LmvTransport.RebuildAfter(new OutfitChange(Cof, [], [], [], [item])));                               // detach an object worn without a link
        Assert.True(LmvTransport.RebuildAfter(new OutfitChange(Cof, [], [], [new AttachRequest(item, 5, true)], [])));   // put an object on
        Assert.True(LmvTransport.RebuildAfter(new OutfitChange(Cof, [], [Guid.NewGuid()], [], [])));                     // take a layer off
        Assert.False(LmvTransport.RebuildAfter(new OutfitChange(Cof, [], [], [], [])));
    }

    [Fact]
    public void The_adapter_runs_the_rebuild_command()
    {
        Assert.True(LmvTransport.IsKnownCommand(new RebuildLookCommand(Cof)));
    }
}
