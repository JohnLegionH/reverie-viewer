// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Inventory;
using Viewer.Shell;
using Viewer.Shell.Inventory;
using Xunit;
using static Viewer.Core.Tests.InventoryModelTests;

namespace Viewer.Core.Tests;

/// <summary>
/// The Recent tab's cut-off (items made since the account last logged out, kept in the settings) and the background fetch of
/// the whole tree after login. Every member builds its own objects and uses a test clock, so the class runs in parallel.
/// </summary>
[Collection("TimingTests")]
public class InventoryRecentFetchTests
{
    private sealed class MemoryStore(string? text = null) : ISettingsStore
    {
        public string? Text = text;
        public string? Read() => Text;
        public bool Write(string text) { Text = text; return true; }
    }

    private static readonly Guid AgentA = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid AgentB = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly DateTimeOffset T0 = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    // ---------------- the logout time ----------------

    [Fact]
    public void No_logout_time_is_kept_until_one_is_recorded()
    {
        var s = new SettingsService(new MemoryStore());
        Assert.Null(LogoutTime.Previous(s, AgentA));
    }

    [Fact]
    public void A_recorded_logout_time_comes_back_to_the_second_after_a_restart()
    {
        var store = new MemoryStore();
        LogoutTime.Record(new SettingsService(store), AgentA, T0.AddMilliseconds(700));
        var back = LogoutTime.Previous(new SettingsService(store), AgentA);
        Assert.Equal(T0, back);   // kept in whole seconds
    }

    [Fact]
    public void Each_account_keeps_its_own_logout_time_and_a_later_one_replaces_an_earlier_one()
    {
        var store = new MemoryStore();
        var s = new SettingsService(store);
        LogoutTime.Record(s, AgentA, T0);
        LogoutTime.Record(s, AgentB, T0.AddDays(3));
        LogoutTime.Record(s, AgentA, T0.AddDays(1));
        Assert.Equal(T0.AddDays(1), LogoutTime.Previous(s, AgentA));
        Assert.Equal(T0.AddDays(3), LogoutTime.Previous(s, AgentB));
    }

    [Fact]
    public void A_nonsense_stored_logout_time_reads_as_none()
    {
        var key = Settings.LastLogoutOf(AgentA).Key;
        Assert.Null(LogoutTime.Previous(new SettingsService(new MemoryStore("{ \"" + key + "\": \"yesterday\" }")), AgentA));
        Assert.Null(LogoutTime.Previous(new SettingsService(new MemoryStore("{ \"" + key + "\": -5 }")), AgentA));
    }

    [Fact]
    public void The_logout_time_keeps_the_other_settings_in_the_file()
    {
        var store = new MemoryStore("""{ "chat_font_size": 20 }""");
        var s = new SettingsService(store);
        LogoutTime.Record(s, AgentA, T0);
        Assert.Equal(20, new SettingsService(store).Get(Settings.ChatFontSize));
    }

    [Fact]
    public void The_background_fetch_is_on_by_default_and_the_setting_can_turn_it_off()
    {
        Assert.True(new SettingsService(new MemoryStore()).Get(Settings.InventoryBackgroundFetch));
        Assert.False(new SettingsService(new MemoryStore("""{ "inventory_background_fetch": false }""")).Get(Settings.InventoryBackgroundFetch));
    }

    // ---------------- Recent since the last logout ----------------

    private static (InventoryViewModel View, InventoryModel Model) NewView()
    {
        var model = new InventoryModel((_, _) => { }, 8);
        model.Apply(Skeleton());
        return (new InventoryViewModel(model, "My Inventory", "Library"), model);
    }

    private static InventoryFolderFetched Fetched(Guid folder, IEnumerable<InventoryItemData> items) =>
        new(folder, true, 9, 0, Array.Empty<InventoryFolderData>(), items.ToList());

    [Fact]
    public void Recent_with_a_logout_time_lists_only_items_made_after_it_newest_first_and_not_capped()
    {
        var (v, m) = NewView();
        const int many = 80;
        m.Apply(Fetched(Root, Enumerable.Range(1, many).Select(n => I(n, Root, "new " + n, at: T0.AddMinutes(n)))
            .Concat(new[] { I(200, Root, "before", at: T0.AddHours(-1)), I(201, Root, "same instant", at: T0) })));
        v.RecentSince = T0;
        v.Tab = InventoryTab.Recent;
        var rows = v.Rows();
        Assert.Equal(many, rows.Count);
        Assert.Equal("new " + many, rows[0].Name);
        Assert.DoesNotContain(rows, r => r.Name is "before" or "same instant");
    }

    [Fact]
    public void Recent_with_a_logout_time_still_leaves_out_the_library_and_still_narrows_by_search()
    {
        var (v, m) = NewView();
        m.Apply(Fetched(Root, new[] { I(1, Root, "red hat", at: T0.AddDays(1)), I(2, Root, "blue hat", at: T0.AddDays(2)) }));
        m.Apply(new InventoryChanged(Array.Empty<InventoryFolderData>(), new[] { I(3, LibObjects, "library hat", at: T0.AddDays(3)) }, Array.Empty<Guid>(), Array.Empty<Guid>()));
        v.RecentSince = T0;
        v.Tab = InventoryTab.Recent;
        Assert.Equal(new[] { "blue hat", "red hat" }, v.Rows().Select(r => r.Name).ToArray());
        v.SearchText = "red";
        Assert.Equal(new[] { "red hat" }, v.Rows().Select(r => r.Name).ToArray());
    }

    [Fact]
    public void An_item_with_no_creation_time_is_not_new()
    {
        var (v, m) = NewView();
        m.Apply(Fetched(Root, new[] { I(1, Root, "undated") }));
        v.RecentSince = T0;
        v.Tab = InventoryTab.Recent;
        Assert.Empty(v.Rows());
    }

    // ---------------- the background fetch ----------------

    private sealed class Rig
    {
        public readonly List<(Guid Folder, Guid Owner)> Asked = new();
        public readonly InventoryModel Model;
        public readonly InventoryBackgroundFetch Fetch;
        public TimeSpan Now;

        public Rig(BackgroundFetchOptions options, int modelMax = 64)
        {
            Model = new InventoryModel((f, o) => { lock (Asked) Asked.Add((f, o)); }, modelMax);
            Model.Apply(FullSkeleton());
            Fetch = new InventoryBackgroundFetch(Model, () => Now, options);
        }

        public void Answer(Guid folder) => Model.Apply(new InventoryFolderFetched(folder, true, 3, 0, Array.Empty<InventoryFolderData>(), Array.Empty<InventoryItemData>()));
        public void Fail(Guid folder) => Model.Apply(new InventoryFolderFetched(folder, false, -1, -1, Array.Empty<InventoryFolderData>(), Array.Empty<InventoryItemData>()));

        // The frame loop calls Tick every frame; preparing the queue takes a few of them, so tick at this instant until it is ready.
        public int Advance(TimeSpan by)
        {
            Now += by;
            int asked = 0;
            for (int guard = 0; guard < 10000; guard++)
            {
                bool preparing = Fetch.Preparing;
                asked += Fetch.Tick();
                if (!preparing && !Fetch.Preparing)
                    break;   // this tick neither started nor continued preparing, so it was the one that could ask
            }
            return asked;
        }
    }

    private static readonly Guid Cof = Guid.Parse("00000000-0000-4000-8000-0000000000c1");
    private static readonly Guid MyOutfits = Guid.Parse("00000000-0000-4000-8000-0000000000c2");
    private static readonly Guid Bodyparts = Guid.Parse("00000000-0000-4000-8000-0000000000c3");
    private static readonly Guid Plain1 = Guid.Parse("00000000-0000-4000-8000-0000000000c4");
    private static readonly Guid Plain2 = Guid.Parse("00000000-0000-4000-8000-0000000000c5");

    private static InventorySkeletonReceived FullSkeleton() => new(Owner, Root, LibOwner, LibRoot,
        new[]
        {
            F(Root, Guid.Empty, "", FolderKind.Root), F(Plain1, Root, "plain one", FolderKind.None), F(Textures, Root, "Textures", FolderKind.Texture),
            F(Plain2, Root, "plain two", FolderKind.None), F(Bodyparts, Root, "Body Parts", FolderKind.Bodypart),
            F(MyOutfits, Root, "My Outfits", FolderKind.MyOutfits), F(Cof, Root, "Current Outfit", FolderKind.CurrentOutfit),
            F(Trash, Root, "Trash", FolderKind.Trash),
        },
        new[] { F(LibRoot, Guid.Empty, "", FolderKind.Root), F(LibObjects, LibRoot, "Objects", FolderKind.Object) });

    private static readonly BackgroundFetchOptions Quick = new(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200), 2);

    [Fact]
    public void Nothing_is_asked_before_begin_or_before_the_start_delay_has_passed()
    {
        var r = new Rig(Quick);
        Assert.Equal(0, r.Advance(TimeSpan.FromMinutes(5)));   // never begun
        r.Fetch.Begin();
        Assert.Equal(0, r.Advance(TimeSpan.FromSeconds(9.9)));
        Assert.Empty(r.Asked);
        Assert.Equal(1, r.Advance(TimeSpan.FromSeconds(0.2)));
        Assert.Single(r.Asked);
    }

    [Fact]
    public void Folders_are_asked_for_one_at_a_time_no_closer_together_than_the_interval()
    {
        var r = new Rig(Quick with { MaxOutstanding = 8 });
        r.Fetch.Begin();
        r.Advance(TimeSpan.FromSeconds(11));
        Assert.Single(r.Asked);
        Assert.Equal(0, r.Advance(TimeSpan.FromMilliseconds(150)));
        Assert.Single(r.Asked);
        Assert.Equal(1, r.Advance(TimeSpan.FromMilliseconds(60)));
        Assert.Equal(2, r.Asked.Count);
    }

    [Fact]
    public void Never_more_requests_out_than_the_limit_and_the_next_waits_for_an_answer()
    {
        var r = new Rig(Quick);   // 2 outstanding
        r.Fetch.Begin();
        for (int i = 0; i < 10; i++)
            r.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(2, r.Asked.Count);
        Assert.NotEmpty(r.Asked);
        r.Answer(r.Asked[0].Folder);
        Assert.Equal(1, r.Advance(TimeSpan.FromSeconds(1)));
        Assert.Equal(3, r.Asked.Count);
    }

    [Fact]
    public void The_users_own_requests_count_against_the_limit_so_the_background_yields()
    {
        var r = new Rig(Quick);
        r.Model.EnsureLoaded(Plain1);
        r.Model.EnsureLoaded(Plain2);   // two out, both the user's
        r.Fetch.Begin();
        Assert.Equal(0, r.Advance(TimeSpan.FromSeconds(30)));
        Assert.Equal(2, r.Asked.Count);
        r.Answer(Plain1);
        Assert.Equal(1, r.Advance(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void The_order_is_the_current_outfit_then_my_outfits_then_other_system_folders_then_the_rest_then_the_library()
    {
        var r = new Rig(Quick with { MaxOutstanding = 20 });
        r.Fetch.Begin();
        r.Advance(TimeSpan.FromSeconds(11));
        for (int i = 0; i < 20; i++)
            r.Advance(TimeSpan.FromSeconds(1));
        var order = r.Asked.Select(a => a.Folder).ToList();
        Assert.True(order.Count >= 3, "too few folders asked for");
        Assert.Equal(Root, order[0]);   // the root is a folder too, and is asked first, as the official viewer does
        Assert.Equal(new[] { Cof, MyOutfits }, order.Skip(1).Take(2).ToArray());
        int firstPlain = order.FindIndex(f => f == Plain1 || f == Plain2);
        Assert.True(order.IndexOf(Textures) < firstPlain && order.IndexOf(Bodyparts) < firstPlain && order.IndexOf(Trash) < firstPlain);
        Assert.True(order.IndexOf(LibRoot) > order.IndexOf(Plain2) && order.IndexOf(LibRoot) > order.IndexOf(Plain1));
        Assert.True(order.IndexOf(LibObjects) > order.IndexOf(LibRoot));
        Assert.Equal(order.Count, order.Distinct().Count());
    }

    [Fact]
    public void Folders_already_loaded_or_on_their_way_are_skipped()
    {
        var r = new Rig(Quick with { MaxOutstanding = 20 });
        r.Model.EnsureLoaded(Textures);
        r.Answer(Textures);
        r.Fetch.Begin();
        r.Advance(TimeSpan.FromSeconds(11));
        for (int i = 0; i < 20; i++)
            r.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(r.Asked, a => a.Folder == Textures);   // once, by the user, not again
    }

    [Fact]
    public void A_failed_folder_is_not_asked_for_again_by_the_background_and_it_finishes()
    {
        var r = new Rig(Quick with { MaxOutstanding = 20 });
        r.Fetch.Begin();
        r.Advance(TimeSpan.FromSeconds(11));
        for (int i = 0; i < 20; i++)
            r.Advance(TimeSpan.FromSeconds(1));
        Assert.NotEmpty(r.Asked);
        var first = r.Asked[0].Folder;
        r.Fail(first);
        foreach (var a in r.Asked.Select(a => a.Folder).Where(f => f != first).ToList())
            r.Answer(a);
        for (int i = 0; i < 20; i++)
            r.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(r.Asked, a => a.Folder == first);
        Assert.True(r.Fetch.Done);
        Assert.Equal(0, r.Advance(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Folders_that_arrive_later_are_picked_up_and_done_is_not_final_until_everything_was_asked()
    {
        var r = new Rig(Quick with { MaxOutstanding = 20 });
        r.Fetch.Begin();
        Assert.False(r.Fetch.Done);
        r.Advance(TimeSpan.FromSeconds(11));
        for (int i = 0; i < 20; i++)
            r.Advance(TimeSpan.FromSeconds(1));
        foreach (var a in r.Asked.Select(a => a.Folder).ToList())
            r.Answer(a);
        Assert.True(r.Advance(TimeSpan.FromSeconds(1)) == 0);
        var late = Guid.Parse("00000000-0000-4000-8000-0000000000d1");
        r.Model.Apply(new InventoryChanged(new[] { F(late, Root, "late", FolderKind.None) }, Array.Empty<InventoryItemData>(), Array.Empty<Guid>(), Array.Empty<Guid>()));
        Assert.Equal(1, r.Advance(TimeSpan.FromSeconds(1)));
        Assert.Equal(late, r.Asked[^1].Folder);
    }

    [Fact]
    public void Begin_before_the_skeleton_arrives_asks_nothing_until_it_does()
    {
        var model = new InventoryModel((_, _) => Assert.Fail("asked without a skeleton"), 8);
        TimeSpan now = TimeSpan.Zero;
        var fetch = new InventoryBackgroundFetch(model, () => now, Quick);
        fetch.Begin();
        now += TimeSpan.FromMinutes(1);
        Assert.Equal(0, fetch.Tick());
        Assert.False(fetch.Done);
    }

    [Fact]
    public void A_tick_with_nothing_due_does_no_work_that_grows_with_the_tree()
    {
        var model = new InventoryModel((_, _) => { }, 8);
        var folders = new List<InventoryFolderData> { F(Root, Guid.Empty, "", FolderKind.Root) };
        for (int i = 0; i < 20000; i++)
            folders.Add(F(Guid.NewGuid(), Root, "f" + i, FolderKind.None));
        model.Apply(new InventorySkeletonReceived(Owner, Root, LibOwner, LibRoot, folders, new[] { F(LibRoot, Guid.Empty, "", FolderKind.Root) }));
        TimeSpan now = TimeSpan.Zero;
        var fetch = new InventoryBackgroundFetch(model, () => now, Quick);
        fetch.Begin();
        now += TimeSpan.FromSeconds(11);
        fetch.Tick();
        // between intervals a tick returns at once
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
            fetch.Tick();
        Assert.True(sw.ElapsedMilliseconds < 200, $"1000 idle ticks took {sw.ElapsedMilliseconds} ms");
    }
}

/// <summary>When a window that follows the inventory redraws. Pure; every member builds its own gate.</summary>
public class RedrawGateTests
{
    [Fact]
    public void What_the_user_did_redraws_at_once_even_right_after_a_draw()
    {
        var g = new RedrawGate(250);
        g.Drew(1000);
        Assert.True(g.Due(1001, userRequested: true, modelChanged: false, visible: true));
    }

    [Fact]
    public void Changes_from_the_network_redraw_at_most_once_per_gap()
    {
        var g = new RedrawGate(250);
        Assert.True(g.Due(1000, false, true, true));   // the first one is not held back
        g.Drew(1000);
        Assert.False(g.Due(1100, false, true, true));
        Assert.False(g.Due(1249, false, true, true));
        Assert.True(g.Due(1250, false, true, true));
    }

    [Fact]
    public void A_hidden_window_never_redraws_and_nothing_pending_does_not_either()
    {
        var g = new RedrawGate(250);
        Assert.False(g.Due(5000, true, true, visible: false));
        Assert.False(g.Due(5000, false, false, visible: true));
    }
}
