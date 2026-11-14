// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.Social;
using Viewer.Shell;
using Viewer.Shell.Social;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// "Show my online status to friends": off takes the see-online right (and the map right that goes with it) from every friend who has
/// it, remembering what each had; on gives back what was remembered; it never sends a right it does not need to, so it can run again
/// whenever the friend list changes. The rights go through the real action pipeline over a fake transport. Every member builds its
/// own objects, so the class runs in parallel.
/// </summary>
public class OnlineStatusSharingTests
{
    private static readonly Guid Self = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Ann = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abcd");
    private static readonly Guid Bob = Guid.Parse("5e5e5e5e-0000-4000-8000-000000000002");
    private static readonly Guid Cy = Guid.Parse("c1c1c1c1-0000-4000-8000-000000000003");
    private const FriendRightsFlags Online = FriendRightsFlags.SeeOnline, Map = FriendRightsFlags.SeeOnMap, Modify = FriendRightsFlags.ModifyObjects;
    private const string Account = "test-account";

    private sealed class MemoryStore(string? text = null) : ISettingsStore
    {
        public string? Text = text;
        public string? Read() => Text;
        public bool Write(string text) { Text = text; return true; }
    }

    private sealed class Rig
    {
        public readonly FakeTransport Transport = new();
        public readonly FriendsModel Friends;
        public readonly MemoryStore Store;
        public readonly SettingsService Settings;
        public readonly OnlineStatusSharing Sharing;

        public Rig(string? file = null, params FriendEntry[] friends)
        {
            var pipeline = new ActionPipeline(() => Transport, () => true, new CoreLog(new ListSink(), "test"));
            Friends = new FriendsModel(Self, new NameCache(), pipeline.Submit, () => true);
            Friends.Receive(new FriendsListed(friends));
            Store = new MemoryStore(file);
            Settings = new SettingsService(Store);
            Sharing = new OnlineStatusSharing(Friends, Settings, Account);
        }

        public IReadOnlyList<FriendRightsCommand> Sent => Transport.Commands.OfType<FriendRightsCommand>().ToList();

        /// <summary>The grid echoing a rights change back, as it does after every grant.</summary>
        public void Echo(Guid friend, FriendRightsFlags granted) => Friends.Receive(new FriendRightsChanged(friend, granted, Online));

        public FriendRightsFlags Granted(Guid friend) => Friends.Friends.Single(f => f.Id == friend).Granted;
    }

    private static FriendEntry Entry(Guid id, string name, FriendRightsFlags granted) => new(id, name, false, granted, Online);

    private static Rig ThreeFriends(string? file = null) =>
        new(file, Entry(Ann, "Ann", Online | Map), Entry(Bob, "Bob", Online | Map | Modify), Entry(Cy, "Cy", FriendRightsFlags.None));

    [Fact]
    public void Turning_it_off_takes_the_online_and_map_rights_from_each_friend_who_has_them_and_keeps_the_rest()
    {
        var r = ThreeFriends();

        r.Sharing.Apply(share: false);

        var sent = r.Sent;
        Assert.Equal(2, sent.Count);
        Assert.Equal(FriendRightsFlags.None, sent.Single(c => c.Friend == Ann).Rights);
        Assert.Equal(Modify, sent.Single(c => c.Friend == Bob).Rights);
        Assert.DoesNotContain(sent, c => c.Friend == Cy);   // Cy never had the right
    }

    [Fact]
    public void Asking_again_before_the_grid_answers_sends_nothing_twice_and_after_the_answers_nothing_at_all()
    {
        var r = ThreeFriends();
        r.Sharing.Apply(share: false);
        Assert.Equal(2, r.Sent.Count);

        r.Sharing.Apply(share: false);
        Assert.Equal(2, r.Sent.Count);

        r.Echo(Ann, FriendRightsFlags.None);
        r.Echo(Bob, Modify);
        r.Sharing.Apply(share: false);
        Assert.Equal(2, r.Sent.Count);
    }

    [Fact]
    public void Turning_it_on_again_gives_back_exactly_what_each_friend_had()
    {
        var r = ThreeFriends();
        r.Sharing.Apply(share: false);
        r.Echo(Ann, FriendRightsFlags.None);
        r.Echo(Bob, Modify);
        int before = r.Sent.Count;

        r.Sharing.Apply(share: true);

        var restored = r.Sent.Skip(before).ToList();
        Assert.Equal(2, restored.Count);
        Assert.Equal(Online | Map, restored.Single(c => c.Friend == Ann).Rights);
        Assert.Equal(Online | Map | Modify, restored.Single(c => c.Friend == Bob).Rights);
        Assert.DoesNotContain(restored, c => c.Friend == Cy);   // Cy had nothing to give back
    }

    [Fact]
    public void Turning_it_on_when_nothing_was_taken_away_sends_nothing()
    {
        var control = ThreeFriends();
        control.Sharing.Apply(share: false);
        Assert.Equal(2, control.Sent.Count);   // the control: the same friends do get a right taken away

        var r = ThreeFriends();
        r.Sharing.Apply(share: true);
        Assert.Empty(r.Sent);
    }

    [Fact]
    public void What_each_friend_had_is_kept_in_the_settings_so_a_later_run_can_give_it_back()
    {
        var r = ThreeFriends();
        r.Sharing.Apply(share: false);
        r.Echo(Ann, FriendRightsFlags.None);
        r.Echo(Bob, Modify);
        Assert.NotNull(r.Settings.GetText("share_online_prior_" + Account));

        // the next run: the same friends, now without the right, a new sharing object over the same file
        var next = new Rig(r.Store.Text, Entry(Ann, "Ann", FriendRightsFlags.None), Entry(Bob, "Bob", Modify), Entry(Cy, "Cy", FriendRightsFlags.None));
        next.Sharing.Apply(share: true);

        Assert.Equal(Online | Map, next.Sent.Single(c => c.Friend == Ann).Rights);
        Assert.Equal(Online | Map | Modify, next.Sent.Single(c => c.Friend == Bob).Rights);
    }

    [Fact]
    public void Once_given_back_the_memory_is_gone_and_a_second_turning_on_sends_nothing()
    {
        var r = ThreeFriends();
        r.Sharing.Apply(share: false);
        r.Echo(Ann, FriendRightsFlags.None);
        r.Echo(Bob, Modify);
        r.Sharing.Apply(share: true);
        r.Echo(Ann, Online | Map);
        r.Echo(Bob, Online | Map | Modify);
        int count = r.Sent.Count;
        Assert.Equal(4, count);   // two taken away, two given back

        r.Sharing.Apply(share: true);

        Assert.Equal(count, r.Sent.Count);
        Assert.True(string.IsNullOrEmpty(r.Settings.GetText("share_online_prior_" + Account)) || r.Settings.GetText("share_online_prior_" + Account) == "{}");
    }

    [Fact]
    public void A_friend_who_arrives_while_it_is_off_loses_the_right_too_and_gets_it_back_when_it_is_turned_on()
    {
        var r = ThreeFriends();
        r.Sharing.Apply(share: false);
        r.Echo(Ann, FriendRightsFlags.None);
        r.Echo(Bob, Modify);
        var dee = Guid.Parse("dede0000-0000-4000-8000-000000000004");
        r.Friends.Receive(new FriendsListed([Entry(Ann, "Ann", FriendRightsFlags.None), Entry(Bob, "Bob", Modify), Entry(dee, "Dee", Online)]));
        int before = r.Sent.Count;

        r.Sharing.Apply(share: false);

        var taken = r.Sent.Skip(before).ToList();
        Assert.Single(taken);
        Assert.Equal(dee, taken[0].Friend);
        Assert.Equal(FriendRightsFlags.None, taken[0].Rights);

        r.Echo(dee, FriendRightsFlags.None);
        before = r.Sent.Count;
        r.Sharing.Apply(share: true);
        Assert.Equal(Online, r.Sent.Skip(before).Single(c => c.Friend == dee).Rights);
    }

    [Fact]
    public void A_friend_who_is_gone_by_the_time_it_is_turned_on_is_skipped()
    {
        var r = ThreeFriends();
        r.Sharing.Apply(share: false);
        r.Echo(Ann, FriendRightsFlags.None);
        r.Echo(Bob, Modify);
        r.Friends.Receive(new FriendsListed([Entry(Bob, "Bob", Modify)]));   // Ann is no longer a friend
        int before = r.Sent.Count;

        r.Sharing.Apply(share: true);

        var restored = r.Sent.Skip(before).ToList();
        Assert.Single(restored);
        Assert.Equal(Bob, restored[0].Friend);
    }

    [Fact]
    public void The_memory_is_kept_per_account()
    {
        var r = ThreeFriends();
        r.Sharing.Apply(share: false);
        Assert.NotNull(r.Settings.GetText("share_online_prior_" + Account));
        Assert.Null(r.Settings.GetText("share_online_prior_someone-else"));
    }
}
