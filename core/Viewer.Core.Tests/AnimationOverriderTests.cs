// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Overrider;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The animation overrider reads its sets from the inventory as the Firestorm viewer stores them (aoengine.cpp update,
/// reloadStateAnimations, readAnimationLinks) and sends the animation requests Firestorm's engine sends when the avatar
/// changes state (aoengine.cpp override, enable, cycle, selectSet, checkBelowWater; llvoavatar.cpp startMotion and
/// stopMotion). A fixture inventory and a recording agent; time is a clock the test moves. Pure, runs in parallel.
/// </summary>
public class AnimationOverriderTests
{
    private static readonly Guid Stand = Guid.Parse("2408fe9e-df1d-1d7d-f4ff-1384fa7b350f");
    private static readonly Guid Stand1 = Guid.Parse("15468e00-3400-bb66-cecc-646d7c14458e");
    private static readonly Guid Stand2 = Guid.Parse("370f3a20-6ca6-9971-848c-9a01bc42ae3c");
    private static readonly Guid Stand3 = Guid.Parse("42b46214-4b44-79ae-deb8-0df61424ff4b");
    private static readonly Guid Stand4 = Guid.Parse("f22fed8b-a5ed-2c93-64d5-bdd8b93c889f");
    private static readonly Guid Walk = Guid.Parse("6ed24bd8-91aa-4b12-ccc7-c97c857ab4e0");
    private static readonly Guid Sit = Guid.Parse("1a5fe8ac-a804-8a5d-7cbd-56bd83184568");
    private static readonly Guid SitFemale = Guid.Parse("b1709c8d-ecd3-54a1-4f28-d55ac0840782");
    private static readonly Guid SitGeneric = Guid.Parse("245f3c54-f1c0-bf2e-811f-46d8eeb386e7");
    private static readonly Guid SitGround = Guid.Parse("1c7600d6-661f-b87b-efe2-d7421eb93c86");
    private static readonly Guid SitGroundConstrained = Guid.Parse("1a2bd58e-87ff-0df8-0b4c-53e047b0bb6e");
    private static readonly Guid Land = Guid.Parse("7a17b059-12b2-41b1-570a-186368b6aa6f");
    private static readonly Guid Hover = Guid.Parse("4ae8016b-31b9-03bb-c401-b1ea941db41d");

    private sealed class FakeInventory : IAoInventory
    {
        private readonly Dictionary<Guid, List<AoFolder>> _folders = [];
        private readonly Dictionary<Guid, List<AoItem>> _items = [];
        public readonly HashSet<Guid> Incomplete = [];
        public readonly List<Guid> Fetched = [];
        public readonly Dictionary<Guid, Guid> Assets = [];

        public Guid RootId { get; } = Guid.NewGuid();
        public bool IsComplete(Guid folder) => !Incomplete.Contains(folder);
        public void Fetch(Guid folder) => Fetched.Add(folder);
        public IReadOnlyList<AoFolder> Folders(Guid folder) => _folders.TryGetValue(folder, out var f) ? f : [];
        public IReadOnlyList<AoItem> Items(Guid folder) => _items.TryGetValue(folder, out var i) ? i : [];
        public Guid AssetOf(Guid linkItemId) => Assets.TryGetValue(linkItemId, out var a) ? a : Guid.Empty;

        public Guid Folder(Guid parent, string name)
        {
            var id = Guid.NewGuid();
            if (!_folders.TryGetValue(parent, out var list)) _folders[parent] = list = [];
            list.Add(new AoFolder(id, name));
            return id;
        }

        /// <summary>An animation link; returns the animation's asset id.</summary>
        public Guid Link(Guid folder, string name, string description = "", bool targetKnown = true) => Link(folder, name, out _, description, targetKnown);

        public Guid Link(Guid folder, string name, out Guid linkId, string description = "", bool targetKnown = true)
        {
            linkId = Guid.NewGuid();
            var asset = Guid.NewGuid();
            if (targetKnown) Assets[linkId] = asset;
            Add(folder, new AoItem(linkId, name, description, true, true, Guid.NewGuid(), targetKnown ? asset : Guid.Empty));
            return asset;
        }

        public void Add(Guid folder, AoItem item)
        {
            if (!_items.TryGetValue(folder, out var list)) _items[folder] = list = [];
            list.Add(item);
        }

        /// <summary>The folder Firestorm keeps its sets in.</summary>
        public Guid AoFolder() => Folder(Folder(RootId, "#Firestorm"), "#AO");
    }

    private sealed class FakeAgent : IAoAgent
    {
        public readonly List<(Guid Id, bool Start)> Sent = [];
        public readonly HashSet<Guid> Signalled = [];
        public void SendAnimation(Guid animation, bool start) => Sent.Add((animation, start));
        public bool SittingOnObject { get; set; }
        public bool ForeignAnimationOnSeat { get; set; }
        public bool IsSignalled(Guid animation) => Signalled.Contains(animation);
    }

    private sealed class World
    {
        public readonly FakeInventory Inventory = new();
        public readonly FakeAgent Agent = new();
        public double Now;
        public double Random;
        public AoEngine Engine = null!;

        /// <summary>Makes the engine and lets it poll the inventory until the sets are read (two polls, five seconds apart).</summary>
        public World Load(bool useAo, string? saved = null)
        {
            Engine = new AoEngine(Inventory, Agent, () => Now, () => Random) { SavedSetName = saved };
            Engine.SetUseAo(useAo);
            Engine.LoginComplete();
            Engine.Tick();
            Now += 5;
            Engine.Tick();
            return this;
        }

        public (Guid, bool)[] Take()
        {
            var sent = Agent.Sent.ToArray();
            Agent.Sent.Clear();
            return sent;
        }
    }

    private static (Guid, bool) Start(Guid id) => (id, true);
    private static (Guid, bool) Stop(Guid id) => (id, false);

    /// <summary>A set with two cycling stands (20 s), a walk, a sit and a landing; returns the animations' asset ids.</summary>
    private static (Guid S1, Guid S2, Guid W1, Guid Sit1, Guid Land1) MainSet(World w, string setFolderName)
    {
        var set = w.Inventory.Folder(w.Inventory.AoFolder(), setFolderName);
        var standing = w.Inventory.Folder(set, "Standing:CT20.00:CY");
        var s1 = w.Inventory.Link(standing, "stand one", "0");
        var s2 = w.Inventory.Link(standing, "stand two", "1");
        var w1 = w.Inventory.Link(w.Inventory.Folder(set, "Walking"), "walk");
        var sit1 = w.Inventory.Link(w.Inventory.Folder(set, "Sitting"), "sit");
        var land1 = w.Inventory.Link(w.Inventory.Folder(set, "Landing"), "land");
        return (s1, s2, w1, sit1, land1);
    }

    // ---------------- reading the sets ----------------

    [Fact]
    public void Sets_are_read_from_the_AO_folder_with_the_options_in_their_folder_names()
    {
        var w = new World();
        var ao = w.Inventory.AoFolder();
        w.Inventory.Folder(ao, "Beach:SO:SM:DM");
        w.Inventory.Folder(ao, "City:**");
        w.Load(useAo: false);

        Assert.True(w.Engine.Loaded);
        Assert.Equal(["Beach", "City"], w.Engine.Sets.Select(s => s.Name));
        var beach = w.Engine.Sets[0];
        Assert.True(beach.SitOverride);
        Assert.True(beach.Smart);
        Assert.True(beach.MouselookStandDisable);
        var city = w.Engine.Sets[1];
        Assert.False(city.SitOverride);
        Assert.False(city.Smart);
        Assert.False(city.MouselookStandDisable);
        Assert.Same(city, w.Engine.DefaultSet);
        Assert.Same(city, w.Engine.CurrentSet);
    }

    [Fact]
    public void State_folders_are_matched_by_name_or_alternate_name_and_carry_the_cycle_options()
    {
        var w = new World();
        var set = w.Inventory.Folder(w.Inventory.AoFolder(), "A");
        w.Inventory.Link(w.Inventory.Folder(set, "Standing:CT30.50:CY:RN"), "stand");
        w.Inventory.Link(w.Inventory.Folder(set, "Walk.N"), "walk");
        w.Inventory.Link(w.Inventory.Folder(set, "Dancing"), "dance");
        w.Load(useAo: false);

        var a = Assert.Single(w.Engine.Sets);
        var standing = a.GetState(AoStateId.Standing);
        Assert.True(standing.Cycle);
        Assert.True(standing.Random);
        Assert.Equal(30.5f, standing.CycleTime);
        Assert.Single(standing.Steps);
        var walking = a.GetState(AoStateId.Walking);
        Assert.Single(walking.Steps);
        Assert.False(walking.Cycle);
        Assert.Equal(0f, walking.CycleTime);
        Assert.Same(standing, a.GetStateByName("Stand.2"));
        Assert.Same(walking, a.GetStateByName("Walking"));
        Assert.Null(a.GetStateByName("Dancing"));
        Assert.Equal(2, Enum.GetValues<AoStateId>().Count(id => a.GetState(id).Steps.Count > 0));
    }

    [Fact]
    public void Links_are_ordered_by_the_number_in_their_description_and_one_without_a_number_is_appended()
    {
        var w = new World();
        var standing = w.Inventory.Folder(w.Inventory.Folder(w.Inventory.AoFolder(), "A"), "Standing");
        w.Inventory.Link(standing, "b", "1");
        w.Inventory.Link(standing, "a", "0");
        w.Inventory.Link(standing, "u", "");
        w.Load(useAo: false);

        Assert.Equal(["a", "b", "u"], Assert.Single(w.Engine.Sets).GetState(AoStateId.Standing).Steps.Select(s => s.Name));
    }

    [Fact]
    public void A_link_whose_animation_is_not_known_yet_is_kept_and_plays_once_it_is_known()
    {
        var w = new World();
        var standing = w.Inventory.Folder(w.Inventory.Folder(w.Inventory.AoFolder(), "A"), "Standing");
        var asset = Guid.NewGuid();
        w.Inventory.Link(standing, "broken", out var link, targetKnown: false);
        w.Load(useAo: true);

        var step = Assert.Single(Assert.Single(w.Engine.Sets).GetState(AoStateId.Standing).Steps);
        Assert.Equal(Guid.Empty, step.Members[0].AssetId);
        w.Take();
        Assert.False(w.Engine.MotionStarted(Stand));
        Assert.DoesNotContain(w.Take(), s => s.Item2);   // nothing is started for a link that leads nowhere

        w.Inventory.Assets[link] = asset;                // the animation the link points to has arrived
        Assert.True(w.Engine.MotionStarted(Stand));
        Assert.Contains(Start(asset), w.Take());
    }

    [Fact]
    public void Items_that_are_not_animation_links_are_not_part_of_a_state()
    {
        var w = new World();
        var standing = w.Inventory.Folder(w.Inventory.Folder(w.Inventory.AoFolder(), "A"), "Standing");
        w.Inventory.Add(standing, new AoItem(Guid.NewGuid(), "a real animation, not a link", "", false, true, Guid.Empty, Guid.NewGuid()));
        w.Inventory.Add(standing, new AoItem(Guid.NewGuid(), "a link to a notecard", "", true, false, Guid.NewGuid(), Guid.Empty));
        w.Inventory.Link(standing, "good");
        w.Load(useAo: false);

        Assert.Equal(["good"], Assert.Single(w.Engine.Sets).GetState(AoStateId.Standing).Steps.Select(s => s.Name));
    }

    [Fact]
    public void A_group_folder_plays_its_animations_together_and_a_track_folder_plays_beside_them_on_its_own_timer()
    {
        var w = new World();
        var standing = w.Inventory.Folder(w.Inventory.Folder(w.Inventory.AoFolder(), "A:**"), "Standing");
        var pair = w.Inventory.Folder(standing, "Pair:OR0");
        var g1 = w.Inventory.Link(pair, "body", "0");
        var g2 = w.Inventory.Link(pair, "hands", "1");
        var face = w.Inventory.Folder(standing, "Face:TR:CY:CT5.00");
        var t1 = w.Inventory.Link(face, "smile", "0");
        var t2 = w.Inventory.Link(face, "frown", "1");
        w.Load(useAo: true);

        var state = Assert.Single(w.Engine.Sets).GetState(AoStateId.Standing);
        var step = Assert.Single(state.Steps);
        Assert.True(step.IsGroup);
        Assert.Equal("Pair", step.Name);
        Assert.Equal(2, step.Members.Count);
        var track = Assert.Single(state.Tracks);
        Assert.Equal("Face", track.Name);
        Assert.True(track.Cycle);
        Assert.Equal(5f, track.CycleTime);

        var sent = w.Take();
        Assert.Contains(Start(g1), sent);
        Assert.Contains(Start(g2), sent);
        Assert.Contains(Start(t2), sent);       // a cycling track moves on before it plays, so the second comes first
        Assert.DoesNotContain(Start(t1), sent);

        w.Now += 4.9;
        w.Engine.Tick();
        Assert.Empty(w.Take());
        w.Now += 0.2;
        w.Engine.Tick();
        Assert.Equal([Start(t1), Stop(t2)], w.Take());
    }

    [Fact]
    public void A_folder_whose_contents_are_not_known_is_asked_for_and_read_on_a_later_poll()
    {
        var w = new World();
        var standing = w.Inventory.Folder(w.Inventory.Folder(w.Inventory.AoFolder(), "A"), "Standing");
        w.Inventory.Link(standing, "stand");
        w.Inventory.Incomplete.Add(standing);
        w.Load(useAo: false);

        Assert.Contains(standing, w.Inventory.Fetched);
        Assert.False(w.Engine.Loaded);

        w.Inventory.Incomplete.Clear();
        w.Now += 4.9;
        w.Engine.Tick();
        Assert.False(w.Engine.Loaded);          // the inventory is looked at every five seconds, not sooner
        w.Now += 0.2;
        w.Engine.Tick();
        w.Now += 5.1;
        w.Engine.Tick();
        Assert.True(w.Engine.Loaded);
        Assert.Single(Assert.Single(w.Engine.Sets).GetState(AoStateId.Standing).Steps);
    }

    [Fact]
    public void Without_a_default_set_the_set_used_last_is_chosen_and_else_the_first()
    {
        var saved = new World();
        var ao = saved.Inventory.AoFolder();
        saved.Inventory.Folder(ao, "One");
        saved.Inventory.Folder(ao, "Two");
        saved.Load(useAo: false, saved: "Two");
        Assert.Equal("Two", saved.Engine.CurrentSet?.Name);
        Assert.Null(saved.Engine.DefaultSet);

        var first = new World();
        ao = first.Inventory.AoFolder();
        first.Inventory.Folder(ao, "One");
        first.Inventory.Folder(ao, "Two");
        first.Load(useAo: false);
        Assert.Equal("One", first.Engine.CurrentSet?.Name);
    }

    // ---------------- playing them ----------------

    [Fact]
    public void Turning_the_overrider_on_while_standing_replaces_the_default_stand()
    {
        var w = new World();
        var (_, s2, _, _, _) = MainSet(w, "Main:SO:**");
        w.Load(useAo: false);
        Assert.Equal([Start(Stand)], w.Take());   // off: the default animation of the state the avatar is in is asked for

        w.Engine.SetUseAo(true);

        Assert.True(w.Engine.Enabled);
        // a cycling state moves on before it plays, so the second stand comes first
        Assert.Equal([Stop(Stand), Stop(Stand1), Stop(Stand2), Stop(Stand3), Stop(Stand4), Start(s2)], w.Take());
        Assert.False(w.Engine.MotionStopped(Stand));   // the region dropping the default stand does not stop the replacement
        Assert.Empty(w.Take());
        Assert.Equal([s2], w.Engine.CurrentSet!.GetState(AoStateId.Standing).CurrentAnimationIds);
    }

    [Fact]
    public void Walking_starts_the_sets_walk_and_stops_the_stand_and_standing_again_swaps_back()
    {
        var w = new World();
        var (s1, s2, w1, _, _) = MainSet(w, "Main:SO:**");
        w.Load(useAo: true);
        w.Take();

        Assert.True(w.Engine.MotionStarted(Walk));
        Assert.Equal([Stop(s2), Start(w1)], w.Take());
        Assert.True(w.Engine.IsOverridden(Walk));

        Assert.True(w.Engine.MotionStopped(Walk));
        Assert.Equal([Stop(w1)], w.Take());
        Assert.False(w.Engine.IsOverridden(Walk));
        Assert.True(w.Engine.MotionStarted(Stand));
        Assert.Equal([Start(s1)], w.Take());
    }

    [Fact]
    public void Stands_cycle_when_the_states_cycle_time_runs_out()
    {
        var w = new World();
        var (s1, s2, _, _, _) = MainSet(w, "Main:SO:**");
        w.Load(useAo: true);   // the stand starts at 5 s on the test clock
        Assert.Contains(Start(s2), w.Take());

        w.Now = 24.9;
        w.Engine.Tick();
        Assert.Empty(w.Take());
        w.Now = 25.1;
        w.Engine.Tick();
        Assert.Equal([Start(s1), Stop(s2)], w.Take());
        w.Now = 45.2;
        w.Engine.Tick();
        Assert.Equal([Start(s2), Stop(s1)], w.Take());
    }

    [Fact]
    public void Next_and_previous_step_through_the_stands_by_hand_even_when_the_state_does_not_cycle()
    {
        var w = new World();
        var standing = w.Inventory.Folder(w.Inventory.Folder(w.Inventory.AoFolder(), "A"), "Standing");
        var s1 = w.Inventory.Link(standing, "one", "0");
        var s2 = w.Inventory.Link(standing, "two", "1");
        var s3 = w.Inventory.Link(standing, "three", "2");
        w.Load(useAo: true);
        Assert.Contains(Start(s1), w.Take());

        w.Engine.Cycle(AoCycleMode.Next, true);
        Assert.Equal([Start(s2), Stop(s1)], w.Take());
        w.Engine.Cycle(AoCycleMode.Previous, true);
        Assert.Equal([Start(s1), Stop(s2)], w.Take());
        w.Engine.Cycle(AoCycleMode.Previous, true);
        Assert.Equal([Start(s3), Stop(s1)], w.Take());
        w.Now += 1000;
        w.Engine.Tick();
        Assert.Empty(w.Take());   // no timer: the state does not cycle by itself
    }

    [Fact]
    public void A_sit_is_replaced_only_when_the_set_overrides_sits()
    {
        var plain = new World();
        var (_, plainS2, _, plainSit, _) = MainSet(plain, "Main:**");
        plain.Load(useAo: true);
        Assert.Contains(Start(plainS2), plain.Take());
        Assert.False(plain.Engine.MotionStarted(Sit));
        Assert.Equal([Stop(plainS2)], plain.Take());   // the stand is cleared away, the sit stays the region's
        Assert.DoesNotContain(Start(plainSit), plain.Agent.Sent);

        var w = new World();
        var (_, s2, _, sit1, _) = MainSet(w, "Main:SO:**");
        w.Load(useAo: true);
        w.Take();
        Assert.True(w.Engine.MotionStarted(Sit));
        // the generic sit covers the moment between two sit animations without starting the default sit again
        Assert.Equal([Stop(s2), Start(SitGeneric), Start(sit1)], w.Take());

        Assert.True(w.Engine.MotionStopped(Sit));
        Assert.Equal([Stop(SitFemale), Stop(SitGeneric), Stop(SitGround), Stop(SitGroundConstrained), Stop(sit1)], w.Take());
    }

    [Fact]
    public void Turning_the_overrider_off_stops_what_it_started_and_asks_for_the_default_again()
    {
        var w = new World();
        var (_, _, w1, _, _) = MainSet(w, "Main:SO:**");
        w.Load(useAo: true);
        Assert.True(w.Engine.MotionStarted(Walk));
        w.Take();

        w.Engine.SetUseAo(false);

        Assert.Equal([Stop(w1), Start(Walk)], w.Take());
        Assert.False(w.Engine.Enabled);
        Assert.False(w.Engine.IsOverridden(Walk));
        Assert.False(w.Engine.MotionStopped(Walk));
        Assert.False(w.Engine.MotionStarted(Stand));
        Assert.Empty(w.Take());
        Assert.All(Enum.GetValues<AoStateId>(), id => Assert.Empty(w.Engine.CurrentSet!.GetState(id).CurrentAnimationIds));
    }

    [Fact]
    public void A_landing_animation_starts_beside_the_default_and_stops_with_it()
    {
        var w = new World();
        var (_, s2, _, _, land1) = MainSet(w, "Main:SO:**");
        w.Load(useAo: true);
        w.Take();

        Assert.False(w.Engine.MotionStarted(Land));   // the default landing still plays: the viewer needs it to know the agent's state
        Assert.Equal([Stop(s2), Start(land1)], w.Take());
        Assert.False(w.Engine.IsOverridden(Land));

        Assert.False(w.Engine.MotionStopped(Land));
        Assert.Equal([Stop(land1)], w.Take());
    }

    [Fact]
    public void Choosing_another_set_stops_the_old_sets_animation_and_starts_the_new_sets()
    {
        var w = new World();
        var ao = w.Inventory.AoFolder();
        var a1 = w.Inventory.Link(w.Inventory.Folder(w.Inventory.Folder(ao, "One:**"), "Standing"), "a");
        var b1 = w.Inventory.Link(w.Inventory.Folder(w.Inventory.Folder(ao, "Two"), "Standing"), "b");
        w.Load(useAo: true);
        Assert.Contains(Start(a1), w.Take());

        var two = w.Engine.SelectSetByName("Two");

        Assert.NotNull(two);
        Assert.Same(two, w.Engine.CurrentSet);
        Assert.Equal([Stop(a1), Start(b1)], w.Take());
        Assert.Null(w.Engine.SelectSetByName("Three"));
        Assert.Same(two, w.Engine.CurrentSet);
    }

    [Fact]
    public void With_stands_switched_off_the_stand_keeps_its_default_and_the_other_states_are_still_replaced()
    {
        var w = new World();
        var (_, s2, w1, _, _) = MainSet(w, "Main:SO:**");
        w.Load(useAo: true);
        w.Engine.SetUseAoStands(false);
        w.Take();

        Assert.True(w.Engine.MotionStarted(Walk));
        Assert.Equal([Stop(s2), Start(w1)], w.Take());
        Assert.True(w.Engine.MotionStopped(Walk));
        w.Take();
        Assert.False(w.Engine.MotionStarted(Stand));
        Assert.Empty(w.Take());
    }

    [Fact]
    public void A_random_cycle_plays_the_step_the_random_number_picks()
    {
        var w = new World { Random = 0.7 };
        var standing = w.Inventory.Folder(w.Inventory.Folder(w.Inventory.AoFolder(), "A"), "Standing:CY:RN");
        w.Inventory.Link(standing, "one", "0");
        w.Inventory.Link(standing, "two", "1");
        var s3 = w.Inventory.Link(standing, "three", "2");
        w.Load(useAo: true);

        Assert.Contains(Start(s3), w.Take());
    }

    [Fact]
    public void Under_water_a_flying_state_plays_the_swimming_state_in_its_place()
    {
        var w = new World();
        var set = w.Inventory.Folder(w.Inventory.AoFolder(), "A");
        var h1 = w.Inventory.Link(w.Inventory.Folder(set, "Hovering"), "hover");
        var swim = w.Inventory.Link(w.Inventory.Folder(set, "Floating"), "float");
        w.Load(useAo: true);
        Assert.True(w.Engine.MotionStarted(Hover));
        Assert.Contains(Start(h1), w.Take());

        w.Engine.CheckBelowWater(true);
        Assert.Equal([Stop(h1), Start(swim)], w.Take());
        w.Engine.CheckBelowWater(true);
        Assert.Empty(w.Take());
        w.Engine.CheckBelowWater(false);
        Assert.Equal([Stop(swim), Start(h1)], w.Take());
    }

    [Fact]
    public void The_regions_animation_list_drives_the_overrider_with_stops_before_starts()
    {
        var w = new World();
        var (s1, s2, w1, _, _) = MainSet(w, "Main:SO:**");
        w.Load(useAo: true);
        w.Take();
        w.Engine.SignalledChanged([(Stand, 1)]);
        Assert.Equal([Stop(s2), Start(s1)], w.Take());   // a default that starts again restarts its replacement, on the next step

        w.Engine.SignalledChanged([(Walk, 2)]);
        Assert.Equal([Stop(s1), Start(w1)], w.Take());
        w.Engine.SignalledChanged([(Walk, 2), (w1, 3)]);  // the region lists the replacement: not a state, nothing to do
        Assert.Empty(w.Take());
        w.Engine.SignalledChanged([(Stand, 4), (w1, 3)]);
        Assert.Equal([Stop(w1), Start(s2)], w.Take());
        w.Engine.SignalledChanged([(Stand, 5)]);          // the same animation with a new sequence number is a new start
        Assert.Equal([Stop(s2), Start(s1)], w.Take());
    }
}
