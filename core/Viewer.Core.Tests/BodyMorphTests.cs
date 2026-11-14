// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml.Linq;
using Viewer.Core.Avatars;
using Viewer.Core.Meshing;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The shape's morph targets on the default body and its volume morphs on the collision volumes (<see cref="BodyMorph"/>,
/// <see cref="MorphTarget"/>): a morph moves its vertices by weight times offset, softens and renormalises the normals, shifts
/// texture coordinates, takes the parameter's own weight or its default by sex, follows drivers; the volume morphs of the
/// same sliders scale and move collision volumes, which fitted mesh follows. The synthetic cases are worked by hand; the cases
/// on the real files (local input, skipped without NV_CHARACTER_DIR) are checked against a second computation in this file,
/// written from the files' own bytes and elements. Pure, parallel.
/// </summary>
public class BodyMorphTests
{
    private const string SkeletonXml = """
        <linden_skeleton version="2.0">
          <bone name="mPelvis" pos="0 0 1" rot="0 0 0" scale="1 1 1" support="base">
            <bone name="mTorso" pos="0 0 0.1" rot="0 0 0" scale="1 1 1" support="base">
              <collision_volume name="BELLY" pos="0 0 0" rot="0 0 0" scale="0.1 0.2 0.3" support="base"/>
              <bone name="mChest" pos="0 0 0.2" rot="0 0 0" scale="1 1 1" support="base"/>
            </bone>
          </bone>
        </linden_skeleton>
        """;

    // upperBodyMesh: Fat (a volume morph on BELLY), Muscle once for each sex (one morph, two ids; the male one also widens the belly),
    // an Absent morph the mesh file lacks, and Wide_Driven (found under its name without the suffix). The skirt mesh is not drawn
    // but its volume morph still applies. Body Fat (637) drives Fat (104) and Skirt_Fat (120).
    private const string DefinitionXml = """
        <linden_avatar>
          <attachment_point id="1" group="6" name="Chest" joint="mChest" position="0 0 0.1" rotation="0 0 0"/>
          <mesh type="upperBodyMesh" lod="0" file_name="u.llm" min_pixel_width="320">
            <param id="104" group="1" name="Fat" value_min="0" value_max="1">
              <param_morph><volume_morph name="BELLY" scale="0.1 0 0" pos="0 0 -0.05"/><volume_morph name="NOT_A_VOLUME" scale="1 1 1" pos="0 0 0"/></param_morph>
            </param>
            <param id="106" group="1" name="Muscle" sex="male" value_min="0" value_max="1.4">
              <param_morph><volume_morph name="BELLY" scale="0 0.1 0" pos="0 0 0"/></param_morph>
            </param>
            <param id="107" group="1" name="Muscle" sex="female" value_min="0" value_max="1.4"><param_morph/></param>
            <param id="109" group="1" name="Absent" value_min="0" value_max="1"><param_morph/></param>
            <param id="110" group="1" name="Wide_Driven" value_min="0" value_max="1"><param_morph/></param>
            EXTRA
          </mesh>
          <mesh type="upperBodyMesh" lod="1" file_name="u1.llm" min_pixel_width="160" reference="u.llm"></mesh>
          <mesh type="skirtMesh" lod="0" file_name="s.llm" min_pixel_width="320">
            <param id="120" group="1" name="Skirt_Fat" value_min="0" value_max="1">
              <param_morph><volume_morph name="BELLY" scale="0.05 0 0" pos="0 0 0"/></param_morph>
            </param>
          </mesh>
          <param id="637" group="0" name="Body Fat" value_min="0" value_max="1"><param_driver><driven id="104"/><driven id="120"/></param_driver></param>
          <param id="80" group="0" name="male" value_min="0" value_max="1"/>
        </linden_avatar>
        """;

    // for the defaults: a female-and-male morph whose default is 0.25, and a male-only one whose default is 0.5
    private const string Extra =
        """<param id="108" group="1" name="Lift" value_min="0" value_max="1" value_default="0.25"><param_morph/></param>""" +
        """<param id="111" group="1" name="Muscle" sex="male" value_min="0" value_max="1.4" value_default="0.5"><param_morph/></param>""";

    private static readonly Vector3[] Base = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];

    private static LlmMesh Mesh() => LlmMesh.Parse(AvatarTests.Llm(Base, [0, 1, 2], null, [], detail: false,
    [
        ("Fat", [0, 1], [new(0, 0, 1), new(0, 0, 2)], [new(1, 0, 0), Vector3.Zero], [new(0.1f, 0), Vector2.Zero]),
        ("Muscle", [2], [new(0, 0.5f, 0)], [Vector3.Zero], [Vector2.Zero]),
        ("Lift", [0], [new(0, 0, 4)], [Vector3.Zero], [Vector2.Zero]),
        ("Wide", [1], [new(0, 0, 8)], [Vector3.Zero], [Vector2.Zero]),
    ]));

    private static (AvatarBody Body, ShapeDefinition Def, AvatarRig Rig) Setup(bool extra = false)
    {
        var lad = Encoding.UTF8.GetBytes(DefinitionXml.Replace("EXTRA", extra ? Extra : ""));
        var skeleton = CharacterSkeleton.Parse(Encoding.UTF8.GetBytes(SkeletonXml), lad);
        var body = AvatarBody.Build(skeleton, [("upperBodyMesh", "upper", "mTorso", Mesh())]);
        return (body, ShapeDefinition.Parse(lad), new AvatarRig(skeleton));
    }

    private static VisualParamSet Wire(params (int Id, byte Value)[] values) => new(values.Select(v => v.Value), [.. values.Select(v => v.Id)]);

    private static void Near(float expected, float actual, float tol = 1e-4f) =>
        Assert.True(MathF.Abs(expected - actual) <= tol, $"expected {expected}, got {actual}");

    private static void Near(Vector3 expected, Vector3 actual, float tol = 1e-4f) =>
        Assert.True((expected - actual).Length() <= tol, $"expected {expected}, got {actual}");

    private static void Near(Vector2 expected, Vector2 actual, float tol = 1e-4f) =>
        Assert.True((expected - actual).Length() <= tol, $"expected {expected}, got {actual}");

    private static Vector3 AtTorso(AvatarBody body, Vector3 local) => Vector3.Transform(local, body.Skeleton.DefaultWorld[body.Skeleton.IndexOf("mTorso")]);

    /// <summary>The morphed upper body of an avatar (female unless <paramref name="male"/>) with the given wire values; null when nothing changes.</summary>
    private static MorphedPart? Run(bool male, bool extra, params (int, byte)[] values)
    {
        var (body, def, rig) = Setup(extra);
        var wire = new List<(int, byte)> { (80, male ? (byte)255 : (byte)0) };
        wire.AddRange(values);
        return BodyMorph.Compute(body, def, AvatarShape.From(rig, def, Wire([.. wire])))?.Single();
    }

    // ---- reading the morphs ----

    [Fact]
    public void A_body_mesh_file_gives_its_morphs_by_name_with_their_vertices_offsets_and_uvs()
    {
        var m = Mesh();
        Assert.Equal(["Fat", "Lift", "Muscle", "Wide"], m.Morphs.Keys.Order(StringComparer.Ordinal).ToArray());
        var fat = m.Morphs["Fat"];
        Assert.Equal([0, 1], fat.Indices);
        Assert.Equal(new Vector3(0, 0, 2), fat.Coords[1]);
        Assert.Equal(new Vector3(1, 0, 0), fat.Normals[0]);
        Assert.Equal(new Vector2(0.1f, 0), fat.Uvs[0]);
    }

    [Fact]
    public void The_loader_makes_the_physics_driven_copies_of_five_morphs_as_the_official_loader_does()
    {
        var pos = new[] { new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(0, -1, 0) };
        var coords = new Vector3[] { new(1, 2, 3), new(4, 5, 6), new(7, 8, 9) };
        var normals = new Vector3[] { new(0, 0, 1), new(0, 1, 0), new(1, 0, 0) };
        var uvs = new Vector2[] { new(0.1f, 0.2f), new(0.3f, 0.4f), new(0.5f, 0.6f) };
        int[] idx = [0, 1, 2];
        var m = LlmMesh.Parse(AvatarTests.Llm(pos, [0, 1, 2], null, [], detail: false,
        [
            ("Breast_Female_Cleavage", idx, coords, normals, uvs), ("Breast_Gravity", idx, coords, normals, uvs), ("Big_Belly_Torso", idx, coords, normals, uvs),
            ("Big_Belly_Legs", idx, coords, normals, uvs), ("skirt_belly", idx, coords, normals, uvs), ("Small_Butt", idx, coords, normals, uvs),
        ]));
        // straight copies
        Assert.Equal(coords, m.Morphs["Breast_Physics_InOut_Driven"].Coords);
        Assert.Equal(normals, m.Morphs["Breast_Physics_UpDown_Driven"].Normals);
        Assert.Equal(uvs, m.Morphs["Breast_Physics_UpDown_Driven"].Uvs);
        // a fixed direction, normals cleared
        foreach (var name in new[] { "Belly_Physics_Torso_UpDown_Driven", "Belly_Physics_Legs_UpDown_Driven", "Belly_Physics_Skirt_UpDown_Driven", "Butt_Physics_UpDown_Driven" })
        {
            Assert.All(m.Morphs[name].Coords, c => Assert.Equal(new Vector3(0, 0, 0.05f), c));
            Assert.All(m.Morphs[name].Normals, c => Assert.Equal(Vector3.Zero, c));
        }
        Assert.All(m.Morphs["Butt_Physics_LeftRight_Driven"].Coords, c => Assert.Equal(new Vector3(0, 0.03f, 0), c));
        // the cleavage copy: three quarters of the offsets
        var cleavage = m.Morphs["Breast_Physics_LeftRight_Driven"];
        Near(new Vector3(0.75f, 1.5f, 2.25f), cleavage.Coords[0]);
        Near(new Vector3(0.75f * 7, 0.75f * 8, 0.75f * 9), cleavage.Coords[2]);
    }

    [Fact]
    public void The_cleavage_copy_flips_the_y_scale_for_an_offset_with_a_negative_y()
    {
        var coords = new Vector3[] { new(1, -2, 3), new(1, 2, 3) };
        var m = LlmMesh.Parse(AvatarTests.Llm([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [0, 1, 2], null, [], detail: false,
            [("Breast_Female_Cleavage", [0, 1], coords, [new(1, 1, 1), new(1, 1, 1)], [Vector2.Zero, Vector2.Zero])]));
        var c = m.Morphs["Breast_Physics_LeftRight_Driven"];
        Near(new Vector3(0.75f, 1.5f, 2.25f), c.Coords[0]);     // y -2 times -0.75
        Near(new Vector3(0.75f, 1.5f, 2.25f), c.Coords[1]);
        Near(new Vector3(0.75f, -0.75f, 0.75f), c.Normals[0]);  // the normal offset is mirrored the same way
        Near(new Vector3(0.75f, 0.75f, 0.75f), c.Normals[1]);
    }

    [Fact]
    public void A_morph_with_a_vertex_index_past_ten_thousand_makes_the_file_unreadable_as_the_official_loader_rejects_it()
    {
        Assert.Throws<InvalidDataException>(() => LlmMesh.Parse(AvatarTests.Llm(Base, [0, 1, 2], null, [], detail: false,
            [("Bad", [10001], [Vector3.One], [Vector3.Zero], [Vector2.Zero])])));
    }

    [Fact]
    public void The_definition_lists_the_morph_parameters_under_each_level_zero_mesh_with_their_volume_morphs()
    {
        var def = Setup().Def;
        Assert.Equal(6, def.Morphs.Count);          // not the lower-detail mesh's (it has none) and not the driver or the male parameter
        var fat = def.Morphs.Single(m => m.Id == 104);
        Assert.Equal("upperBodyMesh", fat.MeshType);
        Assert.Equal("Fat", fat.Name);
        Assert.Equal(2, fat.Volumes.Count);
        Assert.Equal(new VolumeMorph("BELLY", new Vector3(0.1f, 0, 0), new Vector3(0, 0, -0.05f)), fat.Volumes[0]);
        Assert.Equal(ShapeSex.Male, def.Morphs.Single(m => m.Id == 106).Sex);
        Assert.Equal("skirtMesh", def.Morphs.Single(m => m.Id == 120).MeshType);
    }

    // ---- what a morph does to the mesh ----

    [Fact]
    public void A_weight_of_zero_changes_nothing_so_the_shared_body_is_kept()
    {
        Assert.Null(Run(male: false, extra: false, (637, 0)));
        Assert.Null(Run(male: true, extra: false));
    }

    [Fact]
    public void A_morph_moves_its_vertices_by_weight_times_offset_and_leaves_the_others()
    {
        var (body, _, _) = Setup();
        var p = Run(false, false, (637, 255))!;     // Body Fat at the top drives Fat to weight 1
        Near(AtTorso(body, new Vector3(0, 0, 1)), p.Positions[0]);
        Near(AtTorso(body, new Vector3(1, 0, 2)), p.Positions[1]);
        Near(AtTorso(body, new Vector3(0, 1, 0)), p.Positions[2]);   // not a vertex of Fat
        float w = 128f / 255f;
        var half = Run(false, false, (637, 128))!;
        Near(AtTorso(body, new Vector3(0, 0, w)), half.Positions[0]);
        Near(AtTorso(body, new Vector3(1, 0, 2 * w)), half.Positions[1]);
    }

    [Fact]
    public void Normals_are_moved_by_the_softened_offset_and_renormalised_and_texture_coordinates_by_the_uv_offset()
    {
        float w = 128f / 255f;
        var p = Run(false, false, (637, 128))!;
        var n0 = Vector3.Normalize(new Vector3(0.65f * w, 0, 1));      // base normal (0,0,1) plus offset (1,0,0) x weight x 0.65
        Near(n0, p.Normals[0], 1e-4f);
        Near(Vector3.UnitZ, p.Normals[1]);                              // a zero normal offset leaves it
        Near(Vector3.UnitZ, p.Normals[2]);
        Near(new Vector2(0.1f * w, 0), p.Uvs[0]);                       // base uv of vertex 0 is (0, 0)
        Near(new Vector2(0.5f, 0.25f), p.Uvs[1]);
        Assert.All(p.Normals, n => Near(1f, n.Length(), 1e-4f));
    }

    [Fact]
    public void A_driver_passes_its_weight_to_the_morph_it_drives()
    {
        var viaDriver = Run(false, false, (637, 200))!;
        var direct = Run(false, false, (104, 200))!;
        for (int i = 0; i < 3; i++) Near(direct.Positions[i], viaDriver.Positions[i]);
    }

    [Fact]
    public void A_parameter_for_one_sex_gives_the_other_sex_its_default_weight()
    {
        var (body, _, _) = Setup();
        // the male Muscle (106, up to 1.4) at the top: a male is moved by 0.5 x 1.4, a female by the parameter's default 0 (nothing changes)
        var male = Run(true, false, (106, 255))!;
        Near(AtTorso(body, new Vector3(0, 1.7f, 0)), male.Positions[2]);
        Assert.Null(Run(false, false, (106, 255)));
        // and the female Muscle (107) is the other way round
        Near(AtTorso(body, new Vector3(0, 1.7f, 0)), Run(false, false, (107, 255))!.Positions[2]);
        Assert.Null(Run(true, false, (107, 255)));
    }

    [Fact]
    public void A_weight_the_wire_does_not_carry_is_the_parameters_default_and_a_default_applies_to_the_other_sex_too()
    {
        var (body, _, _) = Setup(extra: true);
        // Lift defaults to 0.25 (both sexes): up 4 x 0.25 at vertex 0. The male-only Muscle 111 defaults to 0.5 x 0.5 up in y at vertex 2
        // (the other sex gets the default; a male not transmitting it takes the same default as its weight)
        foreach (bool male in new[] { false, true })
        {
            var p = Run(male, extra: true)!;
            Near(AtTorso(body, new Vector3(0, 0, 1)), p.Positions[0]);
            Near(AtTorso(body, new Vector3(0, 1.25f, 0)), p.Positions[2]);
        }
        // a male transmitting 111 at the top (1.4) and 106 at 1.4: the morph is moved by both
        var both = Run(true, true, (106, 255), (111, 255))!;
        Near(AtTorso(body, new Vector3(0, 1 + 0.5f * (1.4f + 1.4f), 0)), both.Positions[2]);
    }

    [Fact]
    public void A_parameter_whose_morph_the_mesh_lacks_is_ignored_and_a_driven_name_falls_back_to_the_name_without_the_suffix()
    {
        var (body, _, _) = Setup();
        Assert.Null(Run(false, false, (109, 255)));            // Absent
        var p = Run(false, false, (110, 255))!;                // Wide_Driven -> Wide: vertex 1 up by 8
        Near(AtTorso(body, new Vector3(1, 0, 8)), p.Positions[1]);
    }

    // ---- volume morphs ----

    [Fact]
    public void Volume_morphs_scale_and_move_collision_volumes_by_weight_including_those_of_a_mesh_that_is_not_drawn()
    {
        var (_, def, rig) = Setup();
        int belly = rig.Find("BELLY");
        var neutral = AvatarShape.From(rig, def, Wire((80, 0)));
        Near(new Vector3(0.1f, 0.2f, 0.3f), neutral.Scale[belly]);
        var fat = AvatarShape.From(rig, def, Wire((80, 0), (637, 255)));
        // Fat (0.1, 0, 0) plus the skirt's Skirt_Fat (0.05, 0, 0), both driven to 1; the position moves down by 0.05; NOT_A_VOLUME is ignored
        Near(new Vector3(0.25f, 0.2f, 0.3f), fat.Scale[belly]);
        Near(new Vector3(0, 0, -0.05f), fat.Position[belly]);
        float w = 128f / 255f;
        Near(new Vector3(0.1f + 0.15f * w, 0.2f, 0.3f), AvatarShape.From(rig, def, Wire((80, 0), (637, 128))).Scale[belly]);
    }

    [Fact]
    public void A_volume_morph_follows_the_parameters_sex_rule()
    {
        var (_, def, rig) = Setup();
        int belly = rig.Find("BELLY");
        Near(new Vector3(0.1f, 0.2f + 0.14f, 0.3f), AvatarShape.From(rig, def, Wire((80, 255), (106, 255))).Scale[belly]);   // a male: 0.1 x 1.4 in y
        Near(new Vector3(0.1f, 0.2f, 0.3f), AvatarShape.From(rig, def, Wire((80, 0), (106, 255))).Scale[belly]);               // a female: the male parameter's default 0
    }

    [Fact]
    public void A_fitted_garment_bound_to_a_collision_volume_widens_with_body_fat_and_with_the_muscle_slider()
    {
        var (_, def, rig) = Setup();
        int belly = rig.Find("BELLY");
        // joints are in the pelvis frame: the belly sits 0.1 above the pelvis, its box 0.1 x 0.2 x 0.3 as the skeleton file has it
        Matrix4x4.Invert(Matrix4x4.CreateScale(0.1f, 0.2f, 0.3f) * Matrix4x4.CreateTranslation(0, 0, 0.1f), out var inverseBind);
        var skin = new MeshSkinBlock(["BELLY"], [inverseBind], Matrix4x4.Identity, [], 0f, false, false);
        Vector3 Skinned(AvatarShape shape, Vector3 vertex)
        {
            var joints = new AvatarJoints(rig) { Shape = shape };
            joints.Begin(null, 0);
            var worlds = new Matrix4x4[rig.NodeCount + 1];
            joints.Fill(worlds);
            var palette = new Matrix4x4[1];
            AttachmentPlacement.Palette(skin, [belly], worlds, palette);
            return AttachmentPlacement.SkinPosition(vertex, Matrix4x4.Identity, palette, new byte[4], [1f, 0, 0, 0]);
        }
        // a garment vertex 0.05 to the side and another 0.1 to the front of the belly's centre (at z 0.1 in the pelvis frame)
        var side = new Vector3(0.05f, 0, 0.1f);
        var front = new Vector3(0, 0.1f, 0.1f);
        Near(side, Skinned(AvatarShape.Neutral(rig), side));                                       // the garment fits the default avatar as made
        Near(side, Skinned(AvatarShape.From(rig, def, Wire((80, 0))), side));
        Near(new Vector3(0.05f * 2.5f, 0, 0.1f - 0.05f), Skinned(AvatarShape.From(rig, def, Wire((80, 0), (637, 255))), side));   // box 0.1 -> 0.25 wide, centre down 0.05
        Near(new Vector3(0, 0.1f * 1.7f, 0.1f), Skinned(AvatarShape.From(rig, def, Wire((80, 255), (106, 255))), front));      // box 0.2 -> 0.34 deep
    }

    // ---- the real character files, against a second computation ----

    private static readonly string[] DrawnTypes = ["headMesh", "upperBodyMesh", "lowerBodyMesh", "eyelashMesh", "eyeBallLeftMesh", "eyeBallRightMesh"];

    private static float F(string? s, float d = 0f) => s is null ? d : float.Parse(s, CultureInfo.InvariantCulture);
    private static Vector3 V(string? s) => s is null ? Vector3.Zero : s.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var x, var y, var z] ? new(F(x), F(y), F(z)) : Vector3.Zero;

    /// <summary>The weights the official viewer ends with: defaults, drivers walked from every driver, then the wire values in id order, each driving its driven.</summary>
    private static (Dictionary<int, float> Weight, bool Male) RealWeights(XDocument lad, Dictionary<int, byte> wire)
    {
        var all = lad.Descendants("param").Where(p => p.Attribute("id") is not null).GroupBy(p => (int)p.Attribute("id")!).ToDictionary(g => g.Key, g => g.First());
        float Min(int id) => F((string?)all[id].Attribute("value_min"), 0f);
        float Max(int id) => F((string?)all[id].Attribute("value_max"), 1f);
        float Def(int id) => Math.Clamp(F((string?)all[id].Attribute("value_default")), Min(id), Max(id));
        var weight = all.Keys.ToDictionary(k => k, Def);
        void Drive(int driver, int depth)
        {
            if (depth > 8) return;
            var d = all[driver].Element("param_driver");
            if (d is null) return;
            foreach (var e in d.Elements("driven"))
            {
                int id = (int)e.Attribute("id")!;
                float w = weight[driver], lo = Min(driver), hi = Max(driver);
                float min1 = F((string?)e.Attribute("min1"), lo), max1 = F((string?)e.Attribute("max1"), hi), max2 = F((string?)e.Attribute("max2"), hi), min2 = F((string?)e.Attribute("min2"), hi);
                float dlo = Min(id), dhi = Max(id);
                float v;
                if (w <= min1) v = (min1 == max1 && min1 <= lo) ? dhi : dlo;
                else if (w <= max1) v = dlo + (w - min1) / (max1 - min1) * (dhi - dlo);
                else if (w <= max2) v = dhi;
                else if (w <= min2) v = dhi + (w - max2) / (min2 - max2) * (dlo - dhi);
                else v = max2 >= hi ? dhi : dlo;
                weight[id] = Math.Clamp(v, dlo, dhi);
                Drive(id, depth + 1);
            }
        }
        foreach (var id in all.Keys.OrderBy(k => k)) Drive(id, 0);
        foreach (var (id, b) in wire.OrderBy(kv => kv.Key))
        {
            float lo = Min(id), hi = Max(id), v = b / 255f * (hi - lo) + lo;
            weight[id] = Math.Clamp(MathF.Abs(v) < (hi - lo) / 255f ? 0f : v, lo, hi);
            Drive(id, 0);
        }
        return (weight, weight[80] > 0.5f);
    }

    private sealed class RawMorph
    {
        public int[] Index = [];
        public Vector3[] Coords = [], Normals = [];
        public Vector2[] Uvs = [];
    }

    private sealed class RawMesh
    {
        public Vector3[] Positions = [], Normals = [];
        public Vector2[] Uvs = [];
        public Dictionary<string, RawMorph> Morphs = new();
    }

    /// <summary>A second reader of a body mesh file: positions, normals, uvs and the morph records (with the loader's physics-driven copies).</summary>
    private static RawMesh ReadRaw(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        r.ReadBytes(24);
        bool weighted = r.ReadByte() != 0, detail = r.ReadByte() != 0;
        r.ReadBytes(12 + 12 + 1 + 12);
        int n = r.ReadUInt16();
        var m = new RawMesh { Positions = new Vector3[n], Normals = new Vector3[n], Uvs = new Vector2[n] };
        Vector3 Read3() => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        for (int i = 0; i < n; i++) m.Positions[i] = Read3();
        for (int i = 0; i < n; i++) m.Normals[i] = Read3();
        r.ReadBytes(12 * n);
        for (int i = 0; i < n; i++) m.Uvs[i] = new(r.ReadSingle(), r.ReadSingle());
        if (detail) r.ReadBytes(8 * n);
        if (weighted) r.ReadBytes(4 * n);
        int tris = r.ReadUInt16();
        r.ReadBytes(6 * tris);
        if (weighted) r.ReadBytes(64 * r.ReadUInt16());
        while (true)
        {
            string name = Encoding.ASCII.GetString(r.ReadBytes(64)).TrimEnd('\0');
            if (name == "End Morphs") break;
            int count = r.ReadInt32();
            var mm = new RawMorph { Index = new int[count], Coords = new Vector3[count], Normals = new Vector3[count], Uvs = new Vector2[count] };
            for (int i = 0; i < count; i++)
            {
                mm.Index[i] = r.ReadInt32();
                mm.Coords[i] = Read3();
                mm.Normals[i] = Read3();
                Read3();
                mm.Uvs[i] = new(r.ReadSingle(), r.ReadSingle());
            }
            m.Morphs[name] = mm;
            RawMorph Copy(Func<Vector3, Vector3, (Vector3, Vector3)> f)
            {
                var c = new RawMorph { Index = mm.Index, Uvs = mm.Uvs, Coords = new Vector3[count], Normals = new Vector3[count] };
                for (int i = 0; i < count; i++) (c.Coords[i], c.Normals[i]) = f(mm.Coords[i], mm.Normals[i]);
                return c;
            }
            RawMorph Fixed(Vector3 d) => Copy((_, _) => (d, Vector3.Zero));
            switch (name)
            {
                case "Breast_Female_Cleavage":
                    m.Morphs["Breast_Physics_LeftRight_Driven"] = Copy((c, nn) => c.Y < 0 ? (c * new Vector3(0.75f, -0.75f, 0.75f), nn * new Vector3(0.75f, -0.75f, 0.75f)) : (c * 0.75f, nn * 0.75f));
                    m.Morphs["Breast_Physics_InOut_Driven"] = Copy((c, nn) => (c, nn));
                    break;
                case "Breast_Gravity": m.Morphs["Breast_Physics_UpDown_Driven"] = Copy((c, nn) => (c, nn)); break;
                case "Big_Belly_Torso": m.Morphs["Belly_Physics_Torso_UpDown_Driven"] = Fixed(new(0, 0, 0.05f)); break;
                case "Big_Belly_Legs": m.Morphs["Belly_Physics_Legs_UpDown_Driven"] = Fixed(new(0, 0, 0.05f)); break;
                case "skirt_belly": m.Morphs["Belly_Physics_Skirt_UpDown_Driven"] = Fixed(new(0, 0, 0.05f)); break;
                case "Small_Butt":
                    m.Morphs["Butt_Physics_UpDown_Driven"] = Fixed(new(0, 0, 0.05f));
                    m.Morphs["Butt_Physics_LeftRight_Driven"] = Fixed(new(0, 0.03f, 0));
                    break;
            }
        }
        return m;
    }

    private static bool SexApplies(string? sex, bool male) => sex is null or "both" || (sex == "male" && male) || (sex == "female" && !male);

    /// <summary>Every morph parameter listed under a level-0 mesh of the file, as (mesh type, element).</summary>
    private static IEnumerable<(string Type, XElement Param)> MorphParams(XDocument lad) =>
        lad.Descendants("mesh").Where(m => (string?)m.Attribute("lod") == "0")
           .SelectMany(m => m.Elements("param").Where(p => p.Element("param_morph") is not null).Select(p => ((string)m.Attribute("type")!, p)));

    private static Dictionary<int, byte> Extreme(XDocument lad, byte value, bool male)
    {
        var wire = new Dictionary<int, byte>();
        foreach (var p in lad.Descendants("param").Where(p => (string?)p.Attribute("group") == "0" && (string?)p.Attribute("wearable") == "shape" && p.Attribute("id") is not null))
            wire[(int)p.Attribute("id")!] = value;
        wire[80] = (byte)(male ? 255 : 0);
        return wire;
    }

    private static void BodyAgainstTheFiles(Dictionary<int, byte> wire)
    {
        string dir = CharacterFactAttribute.Folder!;
        var body = AvatarBody.Load(dir);
        var def = body.Shape!;
        var lad = XDocument.Load(Path.Combine(dir, "avatar_lad.xml"));
        var rig = new AvatarRig(body.Skeleton);
        var shape = AvatarShape.From(rig, def, new VisualParamSet(wire.OrderBy(kv => kv.Key).Select(kv => kv.Value), [.. wire.Keys.OrderBy(k => k)]));
        var (weight, male) = RealWeights(lad, wire);
        Assert.Equal(male, shape.Male);
        var morphed = BodyMorph.Compute(body, def, shape);
        int changedParts = 0;
        for (int pi = 0; pi < body.Parts.Count; pi++)
        {
            var part = body.Parts[pi];
            string file = (string)lad.Descendants("mesh").First(m => (string?)m.Attribute("type") == part.Type && (string?)m.Attribute("lod") == "0").Attribute("file_name")!;
            var raw = ReadRaw(Path.Combine(dir, file));
            var pos = (Vector3[])raw.Positions.Clone();
            var scaledNormals = (Vector3[])raw.Normals.Clone();
            var uv = (Vector2[])raw.Uvs.Clone();
            var touched = new bool[pos.Length];
            foreach (var (type, p) in MorphParams(lad).Where(t => t.Type == part.Type))
            {
                int id = (int)p.Attribute("id")!;
                float def0 = Math.Clamp(F((string?)p.Attribute("value_default")), F((string?)p.Attribute("value_min"), 0f), F((string?)p.Attribute("value_max"), 1f));
                float eff = SexApplies((string?)p.Attribute("sex"), male) ? weight[id] : def0;
                string name = (string)p.Attribute("name")!;
                if (!raw.Morphs.TryGetValue(name, out var mm) && name.Contains("_Driven") && name.IndexOf("_Driven", StringComparison.Ordinal) > 0)
                    raw.Morphs.TryGetValue(name[..name.IndexOf("_Driven", StringComparison.Ordinal)], out mm);
                if (mm is null) continue;
                for (int k = 0; k < mm.Index.Length; k++)
                {
                    int v = mm.Index[k];
                    touched[v] = true;
                    pos[v] += mm.Coords[k] * eff;
                    scaledNormals[v] += mm.Normals[k] * (eff * 0.65f);
                    uv[v] += mm.Uvs[k] * eff;
                }
            }
            var normals = scaledNormals.Select((n, i) => touched[i] ? Vector3.Normalize(n) : n).ToArray();
            if (part.Rigid is { } m4)
            {
                pos = pos.Select(v => Vector3.Transform(v, m4)).ToArray();
                normals = normals.Select(v => Vector3.Normalize(Vector3.TransformNormal(v, m4))).ToArray();
            }
            var got = morphed?[pi];
            bool changed = pos.Zip(part.Positions).Any(t => (t.First - t.Second).Length() > 1e-6f);
            if (changed) changedParts++;
            for (int i = 0; i < pos.Length; i++)
            {
                var gp = got?.Positions[i] ?? part.Positions[i];
                Assert.True((pos[i] - gp).Length() < 2e-4f, $"{part.Type} vertex {i}: expected {pos[i]}, got {gp}");
                var gn = got?.Normals[i] ?? part.Normals[i];
                Assert.True((normals[i] - gn).Length() < 2e-3f, $"{part.Type} normal {i}: expected {normals[i]}, got {gn}");
                var gu = got?.Uvs[i] ?? part.Mesh.Uvs[i];
                Assert.True((uv[i] - gu).Length() < 2e-4f, $"{part.Type} uv {i}: expected {uv[i]}, got {gu}");
            }
        }
        if (changedParts == 0) Assert.Null(morphed);
        else Assert.NotNull(morphed);
    }

    [CharacterFact]
    public void A_default_female_and_a_default_male_match_the_files_own_data_and_differ()
    {
        BodyAgainstTheFiles(new() { [80] = 0 });
        BodyAgainstTheFiles(new() { [80] = 255 });
        string dir = CharacterFactAttribute.Folder!;
        var body = AvatarBody.Load(dir);
        var rig = new AvatarRig(body.Skeleton);
        var female = BodyMorph.Compute(body, body.Shape!, AvatarShape.From(rig, body.Shape!, Wire((80, 0))));
        var male = BodyMorph.Compute(body, body.Shape!, AvatarShape.From(rig, body.Shape!, Wire((80, 255))));
        Assert.NotNull(male);                              // the default male carries non-zero defaults (a male-only parameter's default, for one)
        Assert.NotNull(female);
        int upper = body.Parts.ToList().FindIndex(p => p.Type == "upperBodyMesh");
        Assert.Contains(Enumerable.Range(0, body.Parts[upper].Positions.Length), i => (male![upper].Positions[i] - female![upper].Positions[i]).Length() > 1e-4f);
    }

    [CharacterFact]
    public void Heavy_and_thin_extremes_for_each_sex_match_the_files_own_data()
    {
        var lad = XDocument.Load(Path.Combine(CharacterFactAttribute.Folder!, "avatar_lad.xml"));
        foreach (byte v in new byte[] { 255, 0 })
            foreach (bool male in new[] { true, false })
                BodyAgainstTheFiles(Extreme(lad, v, male));
    }

    [CharacterFact]
    public void The_heavy_body_is_wider_than_the_thin_one()
    {
        string dir = CharacterFactAttribute.Folder!;
        var body = AvatarBody.Load(dir);
        var lad = XDocument.Load(Path.Combine(dir, "avatar_lad.xml"));
        var rig = new AvatarRig(body.Skeleton);
        float Width(byte v)
        {
            var wire = new Dictionary<int, byte> { [80] = 255 };
            foreach (int id in new[] { 637, 106, 107, 105, 758 }) wire[id] = id == 758 ? (byte)128 : v;   // Body Fat, the muscles and a belly slider
            var m = BodyMorph.Compute(body, body.Shape!, AvatarShape.From(rig, body.Shape!, new VisualParamSet(wire.OrderBy(k => k.Key).Select(k => k.Value), [.. wire.Keys.OrderBy(k => k)])));
            int upper = body.Parts.ToList().FindIndex(p => p.Type == "upperBodyMesh");
            var all = m?[upper].Positions ?? body.Parts[upper].Positions;
            return all.Max(p => p.X) - all.Min(p => p.X) + all.Max(p => p.Y) - all.Min(p => p.Y);   // the chest's breadth plus its depth
        }
        Assert.True(Width(255) > Width(0) + 0.02f, $"{Width(255)} vs {Width(0)}");
    }

    /// <summary>The volume morphs worked out here: each listed volume morph times the parameter's weight (its default for the other sex).</summary>
    private static Dictionary<string, (Vector3 Scale, Vector3 Pos)> ExpectedVolumes(XDocument lad, Dictionary<int, byte> wire)
    {
        var (weight, male) = RealWeights(lad, wire);
        var r = new Dictionary<string, (Vector3, Vector3)>();
        foreach (var (_, p) in MorphParams(lad))
        {
            int id = (int)p.Attribute("id")!;
            float def0 = Math.Clamp(F((string?)p.Attribute("value_default")), F((string?)p.Attribute("value_min"), 0f), F((string?)p.Attribute("value_max"), 1f));
            float eff = SexApplies((string?)p.Attribute("sex"), male) ? weight[id] : def0;
            foreach (var vm in p.Element("param_morph")!.Elements("volume_morph"))
            {
                string n = (string)vm.Attribute("name")!;
                var (s, q) = r.TryGetValue(n, out var cur) ? cur : (Vector3.Zero, Vector3.Zero);
                r[n] = (s + V((string?)vm.Attribute("scale")) * eff, q + V((string?)vm.Attribute("pos")) * eff);
            }
        }
        return r;
    }

    private static void VolumesAgainstTheFiles(Dictionary<int, byte> wire)
    {
        string dir = CharacterFactAttribute.Folder!;
        var body = AvatarBody.Load(dir);
        var def = body.Shape!;
        var lad = XDocument.Load(Path.Combine(dir, "avatar_lad.xml"));
        var shape = AvatarShape.From(new AvatarRig(body.Skeleton), def, new VisualParamSet(wire.OrderBy(kv => kv.Key).Select(kv => kv.Value), [.. wire.Keys.OrderBy(k => k)]));
        var (weight, male) = RealWeights(lad, wire);
        var got = BodyMorph.VolumeEffects(def.Morphs, weight, male);
        var want = ExpectedVolumes(lad, wire);
        foreach (var (name, (s, q)) in want)
        {
            Assert.True(got.TryGetValue(name, out var g) || (s.Length() < 1e-7f && q.Length() < 1e-7f), $"volume {name} missing");
            if (got.TryGetValue(name, out g)) { Near(s, g.Scale, 1e-4f); Near(q, g.Pos, 1e-4f); }
        }
        Assert.All(got.Keys, k => Assert.True(want.ContainsKey(k), $"volume {k} not in the file's morphs"));
    }

    [CharacterFact]
    public void Volume_morphs_for_the_defaults_and_the_extremes_match_the_files_own_data()
    {
        var lad = XDocument.Load(Path.Combine(CharacterFactAttribute.Folder!, "avatar_lad.xml"));
        VolumesAgainstTheFiles(new() { [80] = 0 });
        VolumesAgainstTheFiles(new() { [80] = 255 });
        foreach (byte v in new byte[] { 255, 0 })
            foreach (bool male in new[] { true, false })
                VolumesAgainstTheFiles(Extreme(lad, v, male));
    }

    [CharacterFact]
    public void A_muscular_male_has_larger_arm_and_clavicle_volumes_than_a_default_male()
    {
        string dir = CharacterFactAttribute.Folder!;
        var body = AvatarBody.Load(dir);
        var rig = new AvatarRig(body.Skeleton);
        var plain = AvatarShape.From(rig, body.Shape!, Wire((80, 255)));
        var strong = AvatarShape.From(rig, body.Shape!, Wire((80, 255), (106, 255)));
        foreach (var name in new[] { "L_CLAVICLE", "L_UPPER_ARM", "R_UPPER_ARM" })
        {
            int j = rig.Find(name);
            Assert.True(strong.Scale[j].X > plain.Scale[j].X + 0.01f, $"{name}: {strong.Scale[j]} vs {plain.Scale[j]}");
        }
    }

    // ---- named weights: the animated morphs (hand poses) ----

    private static MorphedPart Only(MorphedPart[]? parts)
    {
        Assert.NotNull(parts);
        return parts!.Single();
    }

    [Fact]
    public void A_named_weight_stands_for_the_morphs_own_weight()
    {
        var (body, def, rig) = Setup();
        var viaWire = Run(male: false, extra: false, (104, 255));
        Assert.NotNull(viaWire);
        var named = Only(BodyMorph.Compute(body, def, AvatarShape.From(rig, def, Wire((80, 0))), new Dictionary<string, float> { ["Fat"] = 1f }, onlyNamed: false));
        Assert.Equal(viaWire!.Positions.Length, named.Positions.Length);
        for (int i = 0; i < named.Positions.Length; i++) Near(viaWire.Positions[i], named.Positions[i]);
        for (int i = 0; i < named.Normals.Length; i++) Near(viaWire.Normals[i], named.Normals[i]);
    }

    [Fact]
    public void A_named_weight_of_half_moves_a_vertex_half_way()
    {
        var (body, def, rig) = Setup();
        var full = Only(BodyMorph.Compute(body, def, AvatarShape.From(rig, def, Wire((80, 0))), new Dictionary<string, float> { ["Wide_Driven"] = 1f }, onlyNamed: true));
        var half = Only(BodyMorph.Compute(body, def, AvatarShape.From(rig, def, Wire((80, 0))), new Dictionary<string, float> { ["Wide_Driven"] = 0.5f }, onlyNamed: true));
        var plain = body.Parts[0].Positions[1];
        Near(plain + (full.Positions[1] - plain) * 0.5f, half.Positions[1]);
        Assert.True((full.Positions[1] - plain).Length() > 0.5f, "the morph should move the vertex");
    }

    [Fact]
    public void Only_named_leaves_every_other_morph_out()
    {
        var (body, def, rig) = Setup(extra: true);
        var shaped = AvatarShape.From(rig, def, Wire((80, 0), (104, 255), (106, 255)));
        var withOthers = Only(BodyMorph.Compute(body, def, shaped, new Dictionary<string, float> { ["Lift"] = 1f }, onlyNamed: false));
        var alone = Only(BodyMorph.Compute(body, def, shaped, new Dictionary<string, float> { ["Lift"] = 1f }, onlyNamed: true));
        var neutralAlone = Only(BodyMorph.Compute(body, def, AvatarShape.Neutral(rig), new Dictionary<string, float> { ["Lift"] = 1f }, onlyNamed: true));
        for (int i = 0; i < alone.Positions.Length; i++) Near(neutralAlone.Positions[i], alone.Positions[i]);
        Assert.NotEqual(withOthers.Positions[1], alone.Positions[1]);   // Fat moves vertex 1 as well when it is not left out
        Near(body.Parts[0].Positions[1], alone.Positions[1]);           // and Lift touches vertex 0 only
    }

    [Fact]
    public void A_named_weight_no_morph_has_changes_nothing()
    {
        var (body, def, rig) = Setup();
        Assert.Null(BodyMorph.Compute(body, def, AvatarShape.Neutral(rig), new Dictionary<string, float> { ["NoSuchMorph"] = 1f }, onlyNamed: true));
    }
}
