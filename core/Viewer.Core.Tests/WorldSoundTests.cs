// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Assets;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Sound;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Sounds in the world: the official gain, fall-off, cut-off and channel rules; the parcel rule; the block list; one-shots, an object's
/// loop, queue, sync, stop and gain change; preloads; footsteps; others' typing; the levels; OpenSim's restart text. A fake engine side
/// records what it is asked to play. Plain .NET, nothing shared, so the class runs in parallel.
/// </summary>
public class WorldSoundTests
{
    private const ulong RegionA = 256000UL << 32 | 256000UL;
    private const ulong RegionEast = 256256UL << 32 | 256000UL;
    private static readonly SimRef Here = new(RegionA, "Here");
    private static readonly Guid Self = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Ann = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abcd");
    private static readonly Guid Bell = Guid.Parse("b0000000-0000-4000-8000-000000000001");
    private static readonly Guid Hum = Guid.Parse("b0000000-0000-4000-8000-000000000002");
    private static readonly Guid Chime = Guid.Parse("b0000000-0000-4000-8000-000000000003");
    private static readonly Guid Box = Guid.Parse("c0000000-0000-4000-8000-000000000001");
    private static readonly Guid Lamp = Guid.Parse("c0000000-0000-4000-8000-000000000002");
    private static readonly byte[] Ogg = [(byte)'O', (byte)'g', (byte)'g', (byte)'S', 0, 2, 0, 0, 0, 0, 0, 0, 0, 0];

    private sealed class MemoryStore : ISettingsStore
    {
        public string? Text;
        public string? Read() => Text;
        public bool Write(string text) { Text = text; return true; }
    }

    private sealed class Output : IWorldSoundOutput
    {
        public readonly Dictionary<int, (Guid Sound, bool Loop)> Playing = new();
        public readonly List<(int Voice, Guid Sound, bool Loop)> Starts = new();
        public readonly List<int> Stops = new();
        public readonly Dictionary<int, (Vector3 Pos, float Gain)> Placed = new();
        public readonly Dictionary<int, double> Position = new();
        public bool Start(int voice, Guid soundId, byte[] bytes, bool loop)
        {
            Starts.Add((voice, soundId, loop));
            Playing[voice] = (soundId, loop);
            Position[voice] = 0;
            return true;
        }
        public void Stop(int voice) { Stops.Add(voice); Playing.Remove(voice); }
        public void Place(int voice, Vector3 position, float gain) => Placed[voice] = (position, gain);
        public bool IsPlaying(int voice) => Playing.ContainsKey(voice);
        public double PlaybackPosition(int voice) => Position.GetValueOrDefault(voice);
        public int VoiceOf(Guid sound) => Playing.First(p => p.Value.Sound == sound).Key;
    }

    private sealed class Rig
    {
        public readonly SettingsService Settings = new(new MemoryStore());
        public readonly Output Out = new();
        public readonly ParcelSoundMap Parcels = new();
        public readonly List<Guid> Fetched = new();
        public readonly Dictionary<Guid, SoundPlace> Objects = new();
        public readonly Dictionary<Guid, SoundPlace> Avatars = new();
        public readonly HashSet<Guid> MutedObjects = new();
        public readonly HashSet<Guid> MutedOwners = new();
        public double Now = 1000;
        public readonly WorldSounds Sounds;

        public Rig(Func<Guid, AssetFetchResult>? answer = null)
        {
            var grid = new GridSounds((id, ct) =>
            {
                Fetched.Add(id);
                return Task.FromResult(answer?.Invoke(id) ?? AssetFetchResult.Ok(Ogg));
            });
            Sounds = new WorldSounds(Settings, Out, grid, Parcels, () => Now)
            {
                Self = Self,
                AgentRegion = RegionA,
                LocateObject = id => Objects.TryGetValue(id, out var p) ? p : null,
                LocateAvatar = id => Avatars.TryGetValue(id, out var p) ? p : null,
                ObjectMuted = MutedObjects.Contains,
                OwnerSoundsMuted = MutedOwners.Contains,
            };
        }

        public void Frame(Vector3? listener = null, bool underwater = false) => Sounds.Update(listener ?? new Vector3(128, 128, 25), underwater);

        public void Trigger(Guid sound, Vector3 at, float gain = 1f, Guid? owner = null, Guid? obj = null, Guid? parent = null, ulong region = RegionA) =>
            Sounds.Receive(new SoundTriggered(Here, sound, owner ?? Ann, obj ?? Box, parent ?? Guid.Empty, region, at, gain));

        public void Attached(Guid obj, Guid sound, byte flags, float gain = 1f, Guid? owner = null) =>
            Sounds.Receive(new AttachedSoundReceived(Here, obj, sound, owner ?? Ann, gain, flags));
    }

    // ---------------- the official numbers ----------------

    [Fact]
    public void Distance_falls_off_as_the_inverse_clamped_at_one_metre_with_rolloff_five_under_water()
    {
        Assert.Equal(1f, WorldSoundMath.DistanceGain(0.3f, false));
        Assert.Equal(1f, WorldSoundMath.DistanceGain(1f, false));
        Assert.Equal(0.1f, WorldSoundMath.DistanceGain(10f, false), 5);
        Assert.Equal(1f / (1 + 5 * 9f), WorldSoundMath.DistanceGain(10f, true), 5);
        Assert.Equal(1f / 100f, WorldSoundMath.DistanceGain(100f, false), 5);
    }

    [Fact]
    public void A_channel_is_the_sources_clamped_gain_times_its_kinds_level_and_priority_is_gain_over_distance_squared()
    {
        Assert.Equal(0.25f, WorldSoundMath.ChannelGain(0.5f, 0.5f, false), 5);
        Assert.Equal(0.5f, WorldSoundMath.ChannelGain(3f, 0.5f, false), 5);
        Assert.Equal(0f, WorldSoundMath.ChannelGain(0.5f, 0.5f, true));
        Assert.Equal(0.5f / 16f, WorldSoundMath.Priority(0.5f, 4f), 5);
        Assert.Equal(0.5f, WorldSoundMath.Priority(0.5f, 0.5f), 5);
        Assert.True(WorldSoundMath.InsideCutoff(1000f, 0f));
        Assert.True(WorldSoundMath.InsideCutoff(1000f, 0.1f));
        Assert.True(WorldSoundMath.InsideCutoff(4f, 5f));
        Assert.False(WorldSoundMath.InsideCutoff(6f, 5f));
    }

    [Fact]
    public void Levels_default_to_the_official_ones_and_mutes_and_minimized_silence_them()
    {
        var s = new SettingsService(new MemoryStore());
        Assert.Equal(0.5f, AudioLevels.KindLevel(s, WorldSoundKind.Object));
        Assert.Equal(0.5f, AudioLevels.KindLevel(s, WorldSoundKind.Ambient));
        Assert.Equal(1f, AudioLevels.MasterLevel(s, minimized: false));
        Assert.Equal(0f, AudioLevels.MasterLevel(s, minimized: true));
        s.Set(AudioLevels.MuteSounds, true);
        Assert.Equal(0f, AudioLevels.KindLevel(s, WorldSoundKind.Object));
        s.Set(AudioLevels.Ambient, 0.8);
        Assert.Equal(0.8f, AudioLevels.KindLevel(s, WorldSoundKind.Ambient), 5);
        s.Set(UiSoundSettings.MuteWhenMinimized, false);
        Assert.Equal(1f, AudioLevels.MasterLevel(s, minimized: true));
        s.Set(AudioLevels.MuteMaster, true);
        Assert.Equal(0f, AudioLevels.MasterLevel(s, minimized: false));
    }

    // ---------------- the parcel rule ----------------

    private static byte[] BitmapWithCell(int x, int y, int perEdge = 64)
    {
        var b = new byte[perEdge * perEdge / 8];
        int i = y * perEdge + x;
        b[i / 8] |= (byte)(1 << (i % 8));
        return b;
    }

    [Fact]
    public void The_parcel_rule_hears_the_agents_parcel_and_keeps_sound_local_parcels_apart()
    {
        var m = new ParcelSoundMap();
        // nothing known: heard, as the official viewer does with no parcel data
        Assert.True(m.CanHear(RegionA, new Vector3(10, 10, 20)));
        m.SetAgentParcel(RegionA, soundLocal: true, BitmapWithCell(2, 3));
        Assert.True(m.CanHear(RegionA, new Vector3(9, 13, 20)));     // square (2, 3): the agent's parcel
        Assert.False(m.CanHear(RegionA, new Vector3(40, 40, 20)));   // outside, and the agent's parcel keeps its sounds in
        Assert.False(m.CanHear(RegionEast, new Vector3(9, 13, 20))); // another region is never the agent's parcel
        m.SetAgentParcel(RegionA, soundLocal: false, BitmapWithCell(2, 3));
        Assert.True(m.CanHear(RegionA, new Vector3(40, 40, 20)));
        // the overlay: square (10, 10) is in chunk 0 (rows 0-15), byte 10 * 64 + 10
        var chunk = new byte[1024];
        chunk[10 * 64 + 10] = ParcelSoundMap.SoundLocalBit;
        m.SetOverlay(RegionA, 0, chunk);
        Assert.False(m.CanHear(RegionA, new Vector3(41, 42, 20)));
        Assert.True(m.CanHear(RegionA, new Vector3(45, 42, 20)));
        Assert.True(m.CanHear(RegionA, new Vector3(9, 13, 20)));
    }

    // ---------------- one-shots ----------------

    [Fact]
    public void A_trigger_fetches_its_sound_once_and_plays_it_at_its_point_at_the_official_gain()
    {
        var r = new Rig();
        r.Trigger(Bell, new Vector3(138, 128, 25), gain: 0.8f);
        r.Trigger(Bell, new Vector3(128, 138, 25), gain: 0.8f);
        r.Frame();
        Assert.Equal([Bell], r.Fetched);
        Assert.Equal(2, r.Out.Starts.Count);
        var v = r.Out.Starts[0].Voice;
        Assert.Equal(new Vector3(138, 128, 25), r.Out.Placed[v].Pos);
        // 0.8 x Sounds level 0.5 x distance 10 m (0.1)
        Assert.Equal(0.04f, r.Out.Placed[v].Gain, 4);
        Assert.False(r.Out.Starts[0].Loop);
        Assert.Contains(r.Sounds.Sources, s => s.Origin == WorldSoundOrigin.Trigger && s.State == WorldSoundState.Playing && s.SoundId == Bell);
        Assert.Equal(2, r.Sounds.Started);
    }

    [Fact]
    public void A_trigger_from_a_neighbour_region_is_placed_in_the_agent_regions_frame()
    {
        var r = new Rig();
        r.Trigger(Bell, new Vector3(4, 128, 25), region: RegionEast);
        r.Frame();
        Assert.Single(r.Out.Starts);
        Assert.Equal(new Vector3(260, 128, 25), r.Out.Placed[r.Out.Starts[0].Voice].Pos);
    }

    [Fact]
    public void A_finished_one_shot_is_done_and_its_source_goes()
    {
        var r = new Rig();
        r.Trigger(Bell, new Vector3(130, 128, 25));
        r.Frame();
        r.Out.Playing.Clear();
        r.Frame();
        Assert.Empty(r.Sounds.Sources);
        Assert.Single(r.Out.Starts);
    }

    [Fact]
    public void Triggers_from_muted_owners_objects_and_parents_and_unhearable_parcels_are_dropped()
    {
        var r = new Rig();
        r.MutedOwners.Add(Ann);
        r.Trigger(Bell, new Vector3(130, 128, 25));
        r.MutedOwners.Clear();
        r.MutedObjects.Add(Box);
        r.Trigger(Bell, new Vector3(130, 128, 25));
        r.MutedObjects.Clear();
        r.MutedObjects.Add(Lamp);
        r.Trigger(Bell, new Vector3(130, 128, 25), parent: Lamp);
        r.MutedObjects.Clear();
        r.Parcels.SetAgentParcel(RegionA, soundLocal: true, BitmapWithCell(0, 0));
        r.Trigger(Bell, new Vector3(130, 128, 25));
        r.Frame();
        Assert.Empty(r.Out.Starts);
        Assert.Equal(["owner muted", "object muted", "parent muted", "parcel"], r.Sounds.Recent.Select(d => d.Outcome).ToArray());
        // a gesture sound of someone else plays only while gesture sounds are on
        r.Parcels.SetAgentParcel(RegionA, soundLocal: false, null);
        r.Settings.Set(AudioLevels.GestureSounds, false);
        r.Trigger(Bell, new Vector3(130, 128, 25), owner: Ann, obj: Ann);
        r.Trigger(Chime, new Vector3(130, 128, 25), owner: Self, obj: Self);
        r.Frame();
        Assert.Equal([Chime], r.Out.Starts.Select(s => s.Sound).ToArray());
        Assert.Equal("gesture sounds off", r.Sounds.Recent[^2].Outcome);
    }

    // ---------------- an object's own sound ----------------

    [Fact]
    public void A_loop_follows_its_object_and_the_same_loop_again_is_ignored_and_a_null_sound_removes_it()
    {
        var r = new Rig();
        r.Objects[Box] = new SoundPlace(RegionA, new Vector3(130, 128, 25));
        r.Attached(Box, Hum, SoundFlagBits.Loop, gain: 1f);
        r.Frame();
        Assert.Single(r.Out.Starts);
        Assert.True(r.Out.Starts[0].Loop);
        int v = r.Out.Starts[0].Voice;
        r.Objects[Box] = new SoundPlace(RegionA, new Vector3(132, 128, 25));
        r.Frame();
        Assert.Equal(new Vector3(132, 128, 25), r.Out.Placed[v].Pos);
        Assert.Equal(0.5f * 0.25f, r.Out.Placed[v].Gain, 4);
        r.Attached(Box, Hum, SoundFlagBits.Loop, gain: 1f);
        r.Frame();
        Assert.Single(r.Out.Starts);
        r.Attached(Box, Guid.Empty, 0);
        r.Frame();
        Assert.Contains(v, r.Out.Stops);
        Assert.Empty(r.Sounds.Sources);
    }

    [Fact]
    public void A_gain_change_sets_the_objects_level_and_the_stop_flag_stops_a_one_shot()
    {
        var r = new Rig();
        r.Objects[Box] = new SoundPlace(RegionA, new Vector3(129, 128, 25));
        r.Attached(Box, Bell, 0, gain: 1f);
        r.Frame();
        Assert.Single(r.Out.Starts);
        int v = r.Out.Starts[0].Voice;
        Assert.Equal(0.5f, r.Out.Placed[v].Gain, 4);
        r.Sounds.Receive(new AttachedSoundGainChanged(Here, Box, 0.2f));
        r.Frame();
        Assert.Equal(0.1f, r.Out.Placed[v].Gain, 4);
        r.Attached(Box, Guid.Empty, SoundFlagBits.Stop);
        r.Frame();
        Assert.Contains(v, r.Out.Stops);
    }

    [Fact]
    public void A_new_sound_without_queue_stops_the_current_one_and_with_queue_waits_for_it_to_end()
    {
        var r = new Rig();
        r.Objects[Box] = new SoundPlace(RegionA, new Vector3(129, 128, 25));
        r.Attached(Box, Bell, 0);
        r.Frame();
        r.Attached(Box, Chime, 0);
        r.Frame();
        Assert.Equal([Bell, Chime], r.Out.Starts.Select(s => s.Sound).ToArray());
        Assert.Contains(r.Out.Starts[0].Voice, r.Out.Stops);

        r.Attached(Box, Hum, SoundFlagBits.Queue);
        r.Frame();
        Assert.Equal(2, r.Out.Starts.Count);                  // Chime still plays; Hum waits
        r.Out.Playing.Clear();                                // Chime ends
        r.Frame();
        Assert.Equal(Hum, r.Out.Starts[^1].Sound);
    }

    [Fact]
    public void A_sync_slave_waits_for_its_master_to_go_round_and_then_starts_with_it()
    {
        var r = new Rig();
        r.Objects[Box] = new SoundPlace(RegionA, new Vector3(129, 128, 25));
        r.Objects[Lamp] = new SoundPlace(RegionA, new Vector3(127, 128, 25));
        r.Attached(Box, Hum, SoundFlagBits.Loop | SoundFlagBits.SyncMaster);
        r.Frame();
        Assert.Single(r.Out.Starts);
        int master = r.Out.VoiceOf(Hum);
        r.Out.Position[master] = 1.5;
        r.Frame();
        r.Attached(Lamp, Chime, SoundFlagBits.Loop | SoundFlagBits.SyncSlave);
        r.Frame();
        Assert.Single(r.Out.Starts);
        Assert.Contains(r.Sounds.Sources, s => s.ObjectId == Lamp && s.State == WorldSoundState.WaitingSync);
        r.Out.Position[master] = 0.1;                         // the master went round
        r.Frame();
        Assert.Equal(Chime, r.Out.Starts[^1].Sound);
    }

    [Fact]
    public void A_sound_for_an_object_not_yet_known_waits_up_to_15_seconds_for_it()
    {
        var r = new Rig();
        r.Attached(Box, Hum, SoundFlagBits.Loop);
        r.Frame();
        Assert.Empty(r.Out.Starts);
        r.Now += 10;
        r.Objects[Box] = new SoundPlace(RegionA, new Vector3(129, 128, 25));
        r.Frame();
        Assert.Single(r.Out.Starts);

        r.Attached(Lamp, Bell, SoundFlagBits.Loop);
        r.Now += 16;
        r.Objects[Lamp] = new SoundPlace(RegionA, new Vector3(129, 128, 25));
        r.Frame();
        Assert.DoesNotContain(r.Out.Starts, s => s.Sound == Bell);
    }

    [Fact]
    public void An_object_sound_beyond_its_cutoff_radius_or_from_a_blocked_owner_is_silent()
    {
        var r = new Rig();
        r.Objects[Box] = new SoundPlace(RegionA, new Vector3(148, 128, 25));
        r.Sounds.Receive(new AttachedSoundReceived(Here, Box, Hum, Ann, 1f, SoundFlagBits.Loop, CutoffRadius: 10f));
        r.Frame();
        Assert.Empty(r.Out.Starts);
        Assert.Contains(r.Sounds.Sources, s => s.ObjectId == Box && s.State == WorldSoundState.Silent && s.Why == "cutoff");
        r.Frame(listener: new Vector3(145, 128, 25));
        Assert.Single(r.Out.Starts);

        r.MutedOwners.Add(Ann);
        r.Attached(Lamp, Bell, SoundFlagBits.Loop);
        r.Objects[Lamp] = new SoundPlace(RegionA, new Vector3(146, 128, 25));
        r.Frame(listener: new Vector3(145, 128, 25));
        Assert.DoesNotContain(r.Out.Starts, s => s.Sound == Bell);
    }

    [Fact]
    public void A_preload_fetches_without_playing()
    {
        var r = new Rig();
        r.Objects[Box] = new SoundPlace(RegionA, new Vector3(129, 128, 25));
        r.Sounds.Receive(new SoundPreloaded(Here, Box, Chime, Ann));
        r.Frame();
        Assert.Equal([Chime], r.Fetched);
        Assert.Empty(r.Out.Starts);
    }

    [Fact]
    public void Only_30_channels_play_and_the_quietest_by_priority_wait()
    {
        var r = new Rig();
        for (int i = 0; i < 32; i++)
            r.Trigger(Bell, new Vector3(128 + 1 + i, 128, 25), obj: Guid.NewGuid());
        r.Frame();
        Assert.Equal(30, r.Out.Starts.Count);
        Assert.DoesNotContain(r.Out.Placed.Values, p => p.Pos.X > 128 + 30);
    }

    // ---------------- footsteps and typing ----------------

    [Fact]
    public void A_foot_that_comes_down_during_a_walk_makes_one_step()
    {
        var d = new FootstepDetector();
        Assert.Equal(Foot.None, d.Update(0.3f, 0.3f, true, false));
        Assert.Equal(Foot.Left, d.Update(0.02f, 0.3f, true, false));
        Assert.Equal(Foot.None, d.Update(0.01f, 0.3f, true, false));   // still down: no second step
        Assert.Equal(Foot.Right, d.Update(0.3f, 0.04f, true, false));
        Assert.Equal(Foot.None, d.Update(0.3f, 0.3f, false, false));
        Assert.Equal(Foot.None, d.Update(0.01f, 0.3f, false, false));  // no walk: no step
        var sitting = new FootstepDetector();
        sitting.Update(0.3f, 0.3f, true, true);
        Assert.Equal(Foot.None, sitting.Update(0.01f, 0.3f, true, true));
    }

    [Fact]
    public void Step_sounds_are_the_official_land_and_material_ids_and_walk_run_and_land_make_steps()
    {
        Assert.Equal(Guid.Parse("e8af4a28-aa83-4310-a7c4-c047e15ea0df"), StepSounds.For(onLand: true, 3));
        Assert.Equal(Guid.Parse("7d1826f4-24c4-4aac-8c2e-eff45df37783"), StepSounds.For(onLand: false, 3));
        Assert.Equal(Guid.Parse("25a863e8-dc42-4e8a-a357-e76422ace9b5"), StepSounds.For(onLand: false, 99));
        Assert.True(StepSounds.MakesSteps([StepSounds.Run]));
        Assert.True(StepSounds.MakesSteps([Guid.NewGuid(), StepSounds.Land]));
        Assert.False(StepSounds.MakesSteps([StepSounds.Type]));
    }

    [Fact]
    public void A_step_plays_at_the_foot_at_gain_0_1_under_the_ambient_level_unless_muted_or_unhearable()
    {
        var r = new Rig();
        r.Settings.Set(AudioLevels.Ambient, 0.8);
        r.Sounds.Step(Ann, new SoundPlace(RegionA, new Vector3(128, 129, 24)), onLand: true, 0);
        r.Frame(listener: new Vector3(128, 128, 24));
        Assert.Single(r.Out.Starts);
        Assert.Equal(StepSounds.OnLand, r.Out.Starts[0].Sound);
        Assert.Equal(0.1f * 0.8f, r.Out.Placed[r.Out.Starts[0].Voice].Gain, 4);
        Assert.Contains(r.Sounds.Sources, s => s.Origin == WorldSoundOrigin.Step);
        r.MutedOwners.Add(Ann);
        r.Sounds.Step(Ann, new SoundPlace(RegionA, new Vector3(128, 129, 24)), onLand: false, 1);
        r.Frame();
        Assert.Single(r.Out.Starts);
    }

    [Fact]
    public void Another_avatar_starting_to_type_plays_the_typing_sound_there_once_and_the_own_avatar_does_not()
    {
        var r = new Rig();
        r.Sounds.TypingSound = () => (Guid.Parse("5e191c7b-8996-9ced-a177-b2ac32bfea06"), null);
        r.Avatars[Ann] = new SoundPlace(RegionA, new Vector3(120, 128, 25));
        r.Avatars[Self] = new SoundPlace(RegionA, new Vector3(128, 128, 25));
        var typing = new Viewer.Core.World.PlayingAnimation(StepSounds.Type, 1);
        r.Sounds.Receive(new AvatarAnimationsReceived(Here, Ann, [typing]));
        r.Sounds.Receive(new AvatarAnimationsReceived(Here, Ann, [typing]));   // still typing: no second sound
        r.Sounds.Receive(new AvatarAnimationsReceived(Here, Self, [typing]));
        r.Frame();
        Assert.Single(r.Out.Starts);
        Assert.Equal(Guid.Parse("5e191c7b-8996-9ced-a177-b2ac32bfea06"), r.Out.Starts[0].Sound);
        Assert.Equal(new Vector3(120, 128, 25), r.Out.Placed[r.Out.Starts[0].Voice].Pos);
        r.Sounds.Receive(new AvatarAnimationsReceived(Here, Ann, []));
        r.Sounds.Receive(new AvatarAnimationsReceived(Here, Ann, [typing]));
        r.Frame();
        Assert.Equal(2, r.Out.Starts.Count);
    }

    [Fact]
    public void Grid_sounds_take_only_ogg_unless_a_check_says_what_it_plays()
    {
        var wav = UiSoundFiles.Read("ui-click.wav")!;
        var plain = new GridSounds((id, ct) => Task.FromResult(AssetFetchResult.Ok(wav)));
        plain.Request(Bell);
        plain.Pump();
        Assert.Equal(GridSoundState.NotOnGrid, plain.StateOf(Bell));
        var check = new GridSounds((id, ct) => Task.FromResult(AssetFetchResult.Ok(wav)), b => WavFile.Parse(b) is not null);
        check.Request(Bell);
        check.Pump();
        Assert.Equal(GridSoundState.Ready, check.StateOf(Bell));
        Assert.Equal(wav, check.BytesOf(Bell));
    }

    [Fact]
    public void Grid_sounds_say_why_a_sound_is_not_played()
    {
        var wav = UiSoundFiles.Read("ui-click.wav")!;
        var answers = new Dictionary<Guid, AssetFetchResult>
        {
            [Bell] = AssetFetchResult.Of(AssetFetchStatus.NotFound),
            [Hum] = AssetFetchResult.Ok(wav),
            [Chime] = AssetFetchResult.Of(AssetFetchStatus.Failed),
            [Box] = AssetFetchResult.Ok(Ogg),
        };
        var g = new GridSounds((id, ct) => Task.FromResult(answers[id]));
        foreach (var id in answers.Keys)
            g.Request(id);
        g.Pump();
        Assert.Equal(GridSoundState.NotOnGrid, g.StateOf(Bell));
        Assert.Equal("not found", g.WhyOf(Bell));
        Assert.Equal(GridSoundState.NotOnGrid, g.StateOf(Hum));
        Assert.Equal("not Ogg (starts 52494646)", g.WhyOf(Hum));
        Assert.Equal(GridSoundState.Failed, g.StateOf(Chime));
        Assert.Equal("fetch failed", g.WhyOf(Chime));
        Assert.Equal(GridSoundState.Ready, g.StateOf(Box));
        Assert.Null(g.WhyOf(Box));
        g.MarkUndecodable(Box);
        Assert.Equal("undecodable", g.WhyOf(Box));
    }

    [Fact]
    public void A_step_plays_our_step_only_when_the_grid_lacks_the_official_one()
    {
        var stone = StepSounds.For(onLand: false, 0);
        var ours = UiSoundFiles.Read("world-footstep.wav");
        Assert.NotNull(ours);
        var r = new Rig(id => id == stone ? AssetFetchResult.Of(AssetFetchStatus.NotFound) : AssetFetchResult.Ok(Ogg));
        r.Sounds.StepFallback = () => ours;
        Assert.True(r.Settings.Get(AudioLevels.FootstepFallback));   // on by default
        // the grid has the land step: its own sound plays, never ours
        r.Sounds.Step(Ann, new SoundPlace(RegionA, new Vector3(128, 129, 24)), onLand: true, 0);
        r.Frame();
        r.Frame();
        Assert.Single(r.Out.Starts);
        Assert.Equal(StepSounds.OnLand, r.Out.Starts[0].Sound);
        // the grid answers "not found" for the stone step: our step plays in its place, under its own id
        r.Sounds.Step(Ann, new SoundPlace(RegionA, new Vector3(128, 129, 24)), onLand: false, 0);
        r.Frame();
        r.Frame();
        Assert.Equal(2, r.Out.Starts.Count);
        Assert.NotEqual(stone, r.Out.Starts[1].Sound);
        Assert.Contains(r.Sounds.Recent, d => d.Origin == WorldSoundOrigin.Step && d.SoundId == stone && d.Outcome == "fallback");
        // switched off: a step the grid lacks plays nothing
        r.Settings.Set(AudioLevels.FootstepFallback, false);
        r.Sounds.Step(Ann, new SoundPlace(RegionA, new Vector3(128, 129, 24)), onLand: false, 0);
        r.Frame();
        r.Frame();
        Assert.Equal(2, r.Out.Starts.Count);
        Assert.Contains(r.Sounds.Recent, d => d.Origin == WorldSoundOrigin.Step && d.Outcome == "not on grid");
    }

    [Fact]
    public void The_collision_and_gesture_switches_are_on_by_default_and_each_silences_its_sounds()
    {
        var r = new Rig();
        Assert.True(r.Settings.Get(AudioLevels.CollisionSounds));
        Assert.True(r.Settings.Get(AudioLevels.GestureSounds));
        var officialCollision = Guid.Parse("9538f37c-456e-4047-81be-6435045608d4");   // stone on stone
        var openSimCollision = Guid.Parse("be7295c0-a158-11e1-b3dd-0800200c9a66");    // OpenSim's stone on stone
        r.Trigger(officialCollision, new Vector3(129, 128, 25));
        r.Trigger(Bell, new Vector3(129, 128, 25), owner: Ann, obj: Ann);   // a gesture of another resident: its object is its owner
        r.Frame();
        Assert.Equal(2, r.Out.Starts.Count);
        r.Settings.Set(AudioLevels.CollisionSounds, false);
        r.Settings.Set(AudioLevels.GestureSounds, false);
        r.Sounds.ClearRecent();
        r.Trigger(officialCollision, new Vector3(129, 128, 25));
        r.Trigger(openSimCollision, new Vector3(129, 128, 25));
        r.Trigger(Bell, new Vector3(129, 128, 25), owner: Ann, obj: Ann);
        r.Trigger(Bell, new Vector3(129, 128, 25), owner: Self, obj: Self);   // the own gesture still plays
        r.Trigger(Hum, new Vector3(129, 128, 25));                             // an ordinary sound still plays
        r.Frame();
        Assert.Equal(4, r.Out.Starts.Count);
        Assert.Equal(2, r.Sounds.Recent.Count(d => d.Outcome == "collision sounds off"));
        Assert.Single(r.Sounds.Recent, d => d.Outcome == "gesture sounds off");
    }

    [Fact]
    public void Reset_stops_every_voice()
    {
        var r = new Rig();
        r.Objects[Box] = new SoundPlace(RegionA, new Vector3(129, 128, 25));
        r.Attached(Box, Hum, SoundFlagBits.Loop);
        r.Frame();
        r.Sounds.Reset();
        Assert.Empty(r.Out.Playing);
        Assert.Empty(r.Sounds.Sources);
    }

    // ---------------- OpenSim's restart text ----------------

    [Theory]
    [InlineData("Region will restart in 2 minutes", true)]
    [InlineData("Region will restart in 1 minute and 30 seconds", true)]
    [InlineData("Region will restart in 15 seconds", true)]
    [InlineData("Region will restart in 1 second", true)]
    [InlineData("Region: Example Region is about to restart.\n\nIf you stay here you will be logged out.\n\n\nTime remaining: 5 minutes.\n", true)]
    [InlineData("Maintenance tonight\n\nTime remaining: 45 seconds.\n", true)]
    [InlineData("The region will restart soon, maybe", false)]
    [InlineData("Time remaining: lots.", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OpenSims_restart_texts_are_recognised_and_others_are_not(string? text, bool expected) =>
        Assert.Equal(expected, OpenSimRestartNotice.Matches(text));

    [Fact]
    public void OpenSims_plain_text_restart_warning_raises_the_restart_warning_and_other_alerts_do_not()
    {
        var notices = new Viewer.Shell.Travel.NoticeModel();
        int warned = 0;
        notices.RestartWarned += () => warned++;
        notices.Receive(new ServerAlert("Region will restart in 2 minutes", "", false));
        Assert.Equal(1, warned);
        notices.Receive(new ServerAlert("Welcome to the region", "", false));
        Assert.Equal(1, warned);
        notices.Receive(new ServerAlert("", "RegionRestartSeconds", false));
        Assert.Equal(2, warned);
    }
}
