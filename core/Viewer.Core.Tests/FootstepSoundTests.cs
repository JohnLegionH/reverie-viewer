// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Assets;
using Viewer.Core.Avatars;
using Viewer.Core.Protocol;
using Viewer.Core.World;
using Viewer.Shell.Sound;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Footsteps and the sound ids around them: which official step and collision sounds an OpenSim grid carries in its default asset set,
/// a real OpenSim step sound read as Ogg Vorbis, the official foot-plant timing on the real walk cycle (land and an object), the ground
/// and material under a foot, the prim material reaching the world state, and our fallback step in the generator and the tuner.
/// Plain .NET, nothing shared, so the class runs in parallel.
/// </summary>
public class FootstepSoundTests
{
    // ---------------- the ids ----------------

    [Fact]
    public void OpenSim_ships_the_official_land_step_and_none_of_the_official_material_steps_or_collision_sounds()
    {
        // OpenSim's default asset set: 49 material pairs, 7 terrain sounds and the land step (named snd_SandStep there)
        Assert.Equal(57, OpenSimSounds.Provided.Count);
        Assert.Equal(56, OpenSimSounds.Collisions.Count);
        Assert.Contains(Guid.Parse("be7295c0-a158-11e1-b3dd-0800200c9a66"), OpenSimSounds.Collisions);   // its stone on stone
        Assert.Contains(StepSounds.OnLand, OpenSimSounds.Provided);
        Assert.DoesNotContain(StepSounds.OnLand, OpenSimSounds.Collisions);
        Assert.All(StepSounds.ByMaterial, id => Assert.DoesNotContain(id, OpenSimSounds.Provided));
        // the official collision table (llmaterialtable.cpp): 28 distinct sounds, the 7 material steps among them
        Assert.Equal(28, CollisionSounds.Official.Count);
        Assert.All(StepSounds.ByMaterial, id => Assert.Contains(id, CollisionSounds.Official));
        Assert.Empty(CollisionSounds.Official.Intersect(OpenSimSounds.Provided));
        Assert.True(CollisionSounds.IsCollision(Guid.Parse("9538f37c-456e-4047-81be-6435045608d4")));
        Assert.True(CollisionSounds.IsCollision(Guid.Parse("be7295c0-a158-11e1-b3dd-0800200c9a66")));
        Assert.False(CollisionSounds.IsCollision(StepSounds.OnLand));
    }

    /// <summary>A test on OpenSim's own land step sound (fixtures-local/sounds/snd_SandStep.ogg, copied from OpenSim's asset set; local
    /// input, never in the repo): skipped with its reason when absent.</summary>
    public sealed class OpenSimSoundFactAttribute : FactAttribute
    {
        public static string FilePath => Path.Combine(TestPaths.FixturesLocal, "sounds", "snd_SandStep.ogg");

        public OpenSimSoundFactAttribute()
        {
            if (!File.Exists(FilePath))
                Skip = "local-only sound: fixtures-local/sounds/snd_SandStep.ogg is absent (OpenSim's asset set, not in the repo)";
        }
    }

    [OpenSimSoundFact]
    public void OpenSims_land_step_reads_as_ogg_vorbis_and_the_grid_sounds_take_it()
    {
        var bytes = File.ReadAllBytes(OpenSimSoundFactAttribute.FilePath);
        var info = OggVorbis.Read(bytes);
        Assert.NotNull(info);
        Assert.InRange(info!.Value.Channels, 1, 2);
        Assert.InRange(info.Value.SampleRate, 8000, 96000);
        var g = new GridSounds((id, ct) => Task.FromResult(AssetFetchResult.Ok(bytes)));
        g.Request(StepSounds.OnLand);
        g.Pump();
        Assert.Equal(GridSoundState.Ready, g.StateOf(StepSounds.OnLand));
        Assert.Null(g.WhyOf(StepSounds.OnLand));
        Assert.Equal(bytes, g.BytesOf(StepSounds.OnLand));
    }

    [Fact]
    public void Bytes_that_are_not_an_ogg_vorbis_stream_have_no_vorbis_header()
    {
        Assert.Null(OggVorbis.Read([]));
        Assert.Null(OggVorbis.Read(UiSoundFiles.Read("ui-click.wav")));
        Assert.Null(OggVorbis.Read("OggS\0\u0002"u8.ToArray()));
    }

    // ---------------- the timing, on the real walk cycle ----------------

    private static AvatarRig RealRig()
    {
        string dir = CharacterFactAttribute.Folder!;
        return new AvatarRig(CharacterSkeleton.Parse(File.ReadAllBytes(Path.Combine(dir, "avatar_skeleton.xml")), File.ReadAllBytes(Path.Combine(dir, "avatar_lad.xml"))));
    }

    /// <summary>Walks the real walk cycle for <paramref name="seconds"/> at 60 frames a second, forward along x at
    /// <paramref name="speed"/>, the official way: the avatar's soles at <paramref name="soles"/> (its stated place less half its height),
    /// each foot joint at its height over the avatar root in the pose. Returns the steps and the cycles played.</summary>
    private static (List<StepHit> Steps, double Cycles) Walk(float soles, Func<float, float, float?> land, IReadOnlyList<StepBox> boxes, double seconds = 4, float speed = 3.2f)
    {
        var rig = RealRig();
        var clip = AnimationClip.Parse(File.ReadAllBytes(CharacterFactAttribute.AnimationPath("walk")!));
        var tracks = AvatarJoints.TracksFor(rig, clip);
        var joints = new AvatarJoints(rig);
        var playback = new WalkPlayback();
        playback.Start(0, clip, FootSpeedTable.Of(rig, clip));
        int l = rig.Find("mFootLeft"), r = rig.Find("mFootRight");
        var sampler = new FootstepSampler();
        var steps = new List<StepHit>();
        const double frame = 1.0 / 60;
        for (double t = 0; t < seconds; t += frame)
        {
            playback.Step(t, new Vector2(speed, 0), 0f);
            joints.Begin(tracks, clip.TimeAt(t + playback.Skew));
            var root = joints.RootWorld().Translation;
            var at = new Vector3(100 + speed * (float)t, 128, soles);
            Vector3 Foot(int node) => at + (joints.WorldPosition(node) - root);
            if (sampler.Update(Foot(l), Foot(r), at, stepAnimationPlaying: true, sitting: false, land, boxes) is { } hit)
                steps.Add(hit);
        }
        double cycles = (seconds + playback.Skew) / (clip.LoopOut - clip.LoopIn);
        return (steps, cycles);
    }

    [CharacterFact("walk")]
    public void A_four_second_walk_on_land_makes_a_step_at_each_foot_plant_of_the_cycle()
    {
        var (steps, cycles) = Walk(25f, (x, y) => 25f, []);
        // the official rule gives one step per foot per cycle: two a cycle
        double expected = 2 * cycles;
        Assert.InRange(steps.Count, (int)Math.Floor(expected) - 1, (int)Math.Ceiling(expected) + 1);
        Assert.True(steps.Count >= 6, $"{steps.Count} steps in 4 s");
        Assert.All(steps, s => Assert.True(s.OnLand));
        // the feet take turns
        for (int i = 1; i < steps.Count; i++)
            Assert.NotEqual(steps[i - 1].Foot, steps[i].Foot);
    }

    [CharacterFact("walk")]
    public void A_walk_on_an_object_steps_on_its_material_and_one_well_above_the_ground_makes_no_step()
    {
        var glass = new StepBox(new Vector3(110, 128, 24.5f), Quaternion.Identity, new Vector3(40, 10, 1), 2);
        var (steps, cycles) = Walk(25f, (x, y) => 20f, [glass]);
        Assert.InRange(steps.Count, (int)Math.Floor(2 * cycles) - 1, (int)Math.Ceiling(2 * cycles) + 1);
        Assert.All(steps, s => { Assert.False(s.OnLand); Assert.Equal(2, s.Material); });
        // an avatar half a metre above the ground (a jump, a fall) never puts a foot down
        var (inAir, _) = Walk(25.5f, (x, y) => 25f, []);
        Assert.Empty(inAir);
    }

    [CharacterFact("walk")]
    public void An_avatar_the_grid_rests_a_little_above_the_ground_still_steps_at_each_foot_plant()
    {
        // an OpenSim grid rests the avatar's stated place about 11 cm higher than half its height above the ground; the feet are measured
        // against the ground less that resting gap, so the walk steps as it does on a grid that rests it at the ground
        var (steps, cycles) = Walk(25.12f, (x, y) => 25f, []);
        Assert.InRange(steps.Count, (int)Math.Floor(2 * cycles) - 1, (int)Math.Ceiling(2 * cycles) + 1);
        var glass = new StepBox(new Vector3(110, 128, 24.5f), Quaternion.Identity, new Vector3(40, 10, 1), 2);
        var (onGlass, _) = Walk(25.12f, (x, y) => 20f, [glass]);
        Assert.InRange(onGlass.Count, (int)Math.Floor(2 * cycles) - 1, (int)Math.Ceiling(2 * cycles) + 1);
        Assert.All(onGlass, s => Assert.Equal(2, s.Material));
    }

    // ---------------- short, normal and tall avatars ----------------

    /// <summary>A shape from the real character definition: height and leg length at <paramref name="size"/> (60 short, 128 middle, 255 tall;
    /// the very smallest shape barely lifts a foot in the walk, so it makes no steps by the official rule either).</summary>
    private static AvatarShape Shaped(AvatarRig rig, byte size, bool male)
    {
        var def = ShapeDefinition.Parse(File.ReadAllBytes(Path.Combine(CharacterFactAttribute.Folder!, "avatar_lad.xml")));
        return AvatarShape.From(rig, def, new VisualParamSet([male ? (byte)255 : (byte)0, size, size], [80, 33, 692]));
    }

    /// <summary>Walks the real walk cycle with the avatar's own shaped skeleton, the way the host places the feet: the soles
    /// <paramref name="rest"/> above flat land at 25 m, each foot joint where <see cref="FootPlacement.NodeHeight"/> puts it, the lift the shape
    /// gives the body (<see cref="PoseComputer.LiftOf"/>), and the avatar's own body height handed to the sampler.</summary>
    private static (List<StepHit> Steps, double Cycles, float Height) WalkShaped(AvatarRig rig, AvatarShape shape, float rest, double seconds = 4, float speed = 3.2f)
    {
        var clip = AnimationClip.Parse(File.ReadAllBytes(CharacterFactAttribute.AnimationPath("walk")!));
        var tracks = AvatarJoints.TracksFor(rig, clip);
        var joints = new AvatarJoints(rig) { Shape = shape };
        var neutral = AvatarShape.Neutral(rig);
        float lift = PoseComputer.LiftOf(neutral, neutral.Measure(null)!.Value.PelvisToFoot, shape, null, 0f);
        float height = shape.Measure(null)!.Value.Height;
        var playback = new WalkPlayback();
        playback.Start(0, clip, FootSpeedTable.Of(rig, clip));
        int l = rig.Find("mFootLeft"), r = rig.Find("mFootRight");
        var sampler = new FootstepSampler();
        var steps = new List<StepHit>();
        const double frame = 1.0 / 60;
        float soles = 25f + rest;
        for (double t = 0; t < seconds; t += frame)
        {
            playback.Step(t, new Vector2(speed, 0), 0f);
            joints.Begin(tracks, clip.TimeAt(t + playback.Skew));
            var at = new Vector3(100 + speed * (float)t, 128, soles);
            Vector3 Foot(int node) => new(at.X, at.Y, FootPlacement.NodeHeight(soles, joints, node, neutral.Measure(null)!.Value.PelvisToFoot + lift));
            if (sampler.Update(Foot(l), Foot(r), at, stepAnimationPlaying: true, sitting: false, (x, y) => 25f, [], height) is { } hit)
                steps.Add(hit);
        }
        return (steps, (seconds + playback.Skew) / (clip.LoopOut - clip.LoopIn), height);
    }

    [CharacterFact("walk")]
    public void Short_normal_and_tall_avatars_resting_where_an_opensim_region_rests_them_all_step_at_each_foot_plant()
    {
        var rig = RealRig();
        var heights = new List<float>();
        foreach (var (size, male) in new[] { ((byte)60, false), ((byte)128, false), ((byte)255, true) })
        {
            var (steps, cycles, height) = WalkShaped(rig, Shaped(rig, size, male), 0.12f);
            heights.Add(height);
            Assert.True(steps.Count >= (int)Math.Floor(2 * cycles) - 1 && steps.Count <= (int)Math.Ceiling(2 * cycles) + 1,
                $"{steps.Count} steps in {2 * cycles:F1} foot plants for a {height:F2} m avatar");
            for (int i = 1; i < steps.Count; i++)
                Assert.NotEqual(steps[i - 1].Foot, steps[i].Foot);
        }
        Assert.True(heights[0] < 1.75f && heights[2] > 2.15f, $"the shapes span short to tall: {string.Join(", ", heights)}");
    }

    [CharacterFact("walk")]
    public void The_resting_gap_allowed_scales_with_the_avatars_own_height()
    {
        // the gap counted as resting is a share of the avatar's own height: a tall avatar resting a little over 0.3 m up still steps,
        // a short one resting a little under 0.3 m up but over its share is taken as off the ground (a jump, a fall) and makes none
        var rig = RealRig();
        var tall = Shaped(rig, 255, true);
        float tallHeight = tall.Measure(null)!.Value.Height;
        var (tallSteps, cycles, _) = WalkShaped(rig, tall, FootstepSampler.RestingGapShare * tallHeight - 0.01f);
        Assert.True(FootstepSampler.RestingGapShare * tallHeight - 0.01f > 0.3f);
        Assert.InRange(tallSteps.Count, (int)Math.Floor(2 * cycles) - 1, (int)Math.Ceiling(2 * cycles) + 1);

        var little = Shaped(rig, 60, false);
        float littleHeight = little.Measure(null)!.Value.Height;
        float over = FootstepSampler.RestingGapShare * littleHeight + 0.03f;
        Assert.True(over < 0.3f);
        var (littleSteps, _, _) = WalkShaped(rig, little, over);
        Assert.Empty(littleSteps);
    }

    // ---------------- the ground and the material under a foot ----------------

    [Fact]
    public void The_ground_under_a_foot_is_the_highest_object_top_within_half_a_metre_else_the_land()
    {
        var wood = new StepBox(new Vector3(10, 10, 24.5f), Quaternion.Identity, new Vector3(4, 4, 1), 3);
        var hit = StepGround.Under(new Vector3(10, 10, 25.03f), 20f, [wood]);
        Assert.NotNull(hit);
        Assert.Equal(25f, hit!.Value.Ground, 3);
        Assert.False(hit.Value.OnLand);
        Assert.Equal(3, hit.Value.Material);
        // a box turned 45 degrees about up still carries a foot near its middle
        var turned = wood with { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4), Material = 1 };
        Assert.Equal(1, StepGround.Under(new Vector3(10.5f, 10.5f, 25.1f), 20f, [turned])!.Value.Material);
        // a top more than half a metre below the foot, or a box beside it: the land
        var low = StepGround.Under(new Vector3(10, 10, 25.7f), 20f, [wood])!.Value;
        Assert.True(low.OnLand);
        Assert.Equal(20f, low.Ground);
        Assert.True(StepGround.Under(new Vector3(13, 10, 25.03f), 20f, [wood])!.Value.OnLand);
        // two boxes: the higher top within reach wins
        var metal = new StepBox(new Vector3(10, 10, 24.9f), Quaternion.Identity, new Vector3(1, 1, 0.4f), 1);
        Assert.Equal(1, StepGround.Under(new Vector3(10, 10, 25.12f), 20f, [wood, metal])!.Value.Material);
        // no land known and nothing under: nothing
        Assert.Null(StepGround.Under(new Vector3(13, 10, 25), null, [wood]));
    }

    private static ObjectState Prim(uint local, uint parent, Vector3 pos, Quaternion rot, Vector3 scale, int material, int? point = null) =>
        new(new ObjectId(1, local), Guid.NewGuid(), parent, point, point is null ? null : Guid.NewGuid(), pos, rot, scale, default, default, default, Material: material);

    [Fact]
    public void Prim_boxes_come_from_roots_and_children_in_the_region_frame_near_the_avatar_and_never_from_attachments()
    {
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var root = Prim(10, 0, new Vector3(50, 50, 20), turn, new Vector3(2, 2, 1), 3);
        var child = Prim(11, 10, new Vector3(1, 0, 0.5f), Quaternion.Identity, new Vector3(1, 1, 1), 2);
        var worn = Prim(12, 0, new Vector3(50, 50, 22), Quaternion.Identity, new Vector3(1, 1, 1), 4, point: 2);
        var far = Prim(13, 0, new Vector3(150, 50, 20), Quaternion.Identity, new Vector3(1, 1, 1), 5);
        var boxes = StepGround.Boxes([root, child, worn, far], new Vector3(50, 50, 21), 20f);
        Assert.Equal(2, boxes.Count);
        var c = Assert.Single(boxes, b => b.Material == 2);
        Assert.True(Vector3.Distance(new Vector3(50, 51, 20.5f), c.Center) < 1e-4f, c.Center.ToString());   // turned with its root
        Assert.Contains(boxes, b => b.Material == 3 && b.Center == new Vector3(50, 50, 20));
    }

    [Fact]
    public void A_prims_material_reaches_the_world_state()
    {
        var p = new LibreMetaverse.Primitive
        {
            LocalID = 7, ID = new LibreMetaverse.UUID(Guid.NewGuid()),
            PrimData = new LibreMetaverse.Primitive.ConstructionData { Material = LibreMetaverse.Material.Glass, PCode = LibreMetaverse.PCode.Prim },
        };
        Assert.Equal(2, LmvTransport.ToPrimData(p).Material);
    }

    // ---------------- our fallback step ----------------

    [Fact]
    public void Our_step_is_one_soft_short_recipe_in_the_generator_and_the_tuner_and_the_shipped_file_is_its_output()
    {
        var recipe = UiSoundSynth.Recipes.SingleOrDefault(r => r.File == "world-footstep.wav");
        Assert.NotNull(recipe);
        Assert.InRange(recipe!.Ms, 40, 200);
        Assert.Contains("world-footstep.wav", SoundTunerList.Files);
        Assert.Equal(UiSoundSynth.Wav(UiSoundSynth.Render(recipe)), UiSoundFiles.Read("world-footstep.wav"));
    }
}
