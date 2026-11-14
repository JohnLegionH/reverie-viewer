// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml.Linq;
using System.Xml.XPath;
using Viewer.Core.Avatars;
using Viewer.Core.Meshing;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The shaped skeleton (<see cref="AvatarShape"/>): visual parameters that scale and move joints, the male/female choice, driver
/// parameters, the body height and the hover, and the order between the shape and the joint overrides worn rigged meshes carry. The
/// synthetic cases are worked by hand; the cases on the real avatar definition file (local input, skipped without NV_CHARACTER_DIR)
/// are checked against a second, separately written computation in this file, from the file's own data. Pure, parallel.
/// </summary>
public class ShapeSkeletonTests
{
    private const string SkeletonXml = """
        <linden_skeleton version="2.0">
          <bone name="mPelvis" pos="0 0 1" rot="0 0 0" scale="1 1 1" support="base" aliases="hip">
            <bone name="mTorso" pos="0 0 0.1" rot="0 0 0" scale="1 1 1" support="base">
              <collision_volume name="BELLY" pos="0 0 0" rot="0 0 0" scale="0.1 0.2 0.3" support="base"/>
              <bone name="mChest" pos="0 0 0.2" rot="0 0 0" scale="1 1 1" support="base">
                <bone name="mNeck" pos="0 0 0.25" rot="0 0 0" scale="1 1 1" support="base">
                  <bone name="mHead" pos="0 0 0.1" rot="0 0 0" scale="1 1 1" support="base">
                    <bone name="mSkull" pos="0 0 0.1" rot="0 0 0" scale="1 1 1" support="base"/>
                  </bone>
                </bone>
              </bone>
            </bone>
            <bone name="mHipLeft" pos="0 0.1 -0.05" rot="0 0 0" scale="1 1 1" support="base">
              <bone name="mKneeLeft" pos="0 0 -0.5" rot="0 0 0" scale="1 1 1" support="base">
                <bone name="mAnkleLeft" pos="0 0 -0.5" rot="0 0 0" scale="1 1 1" support="base">
                  <bone name="mFootLeft" pos="0 0 -0.1" rot="0 0 0" scale="1 1 1" support="base"/>
                </bone>
              </bone>
            </bone>
          </bone>
        </linden_skeleton>
        """;

    private const string DefinitionXml = """
        <linden_avatar>
          <attachment_point id="1" group="6" name="Chest" joint="mChest" position="0 0 0.1" rotation="0 0 0"/>
          <param id="32" group="1" name="Male_Skeleton" value_min="0" value_max="1">
            <param_skeleton><bone name="mTorso" scale="0 0 0.2" offset="0 0 0.01"/></param_skeleton>
          </param>
          <param id="33" group="0" name="Height" value_min="-2" value_max="2">
            <param_skeleton>
              <bone name="mKneeLeft" scale="0 0 0.2" offset="0 0 -0.05"/>
              <bone name="mTorso" scale="0 0 0.1"/>
            </param_skeleton>
          </param>
          <param id="34" group="0" name="MaleOnly" sex="male" value_min="0" value_max="1" value_default="0.25">
            <param_skeleton><bone name="mNeck" scale="0 0 0.3"/></param_skeleton>
          </param>
          <param id="35" group="0" name="Shoulder" value_min="-1" value_max="1" value_default="-0.5">
            <param_skeleton><bone name="mChest" scale="0.4 0 0"/></param_skeleton>
          </param>
          <param id="80" group="0" name="male" value_min="0" value_max="1">
            <param_driver><driven id="32"/></param_driver>
          </param>
          <param id="500" group="0" name="Driver" value_min="-1" value_max="1">
            <param_driver><driven id="501"/></param_driver>
          </param>
          <param id="501" group="1" name="Driven" value_min="0" value_max="2">
            <param_skeleton><bone name="mChest" scale="0.5 0 0"/></param_skeleton>
          </param>
          <param id="11001" group="0" name="Hover" value_min="-2" value_max="2" value_default="0"><param_skeleton/></param>
        </linden_avatar>
        """;

    internal static AvatarRig Rig() => new(CharacterSkeleton.Parse(Encoding.UTF8.GetBytes(SkeletonXml), Encoding.UTF8.GetBytes(DefinitionXml)));
    internal static ShapeDefinition Def() => ShapeDefinition.Parse(Encoding.UTF8.GetBytes(DefinitionXml));

    internal static VisualParamSet Wire(params (int Id, byte Value)[] values) =>
        new(values.Select(v => v.Value), [.. values.Select(v => v.Id)]);

    private static void Near(float expected, float actual, float tol = 1e-4f) =>
        Assert.True(MathF.Abs(expected - actual) <= tol, $"expected {expected}, got {actual}");

    private static void Near(Vector3 expected, Vector3 actual, float tol = 1e-4f) =>
        Assert.True((expected - actual).Length() <= tol, $"expected {expected}, got {actual}");

    // ---- the weights ----

    [Fact]
    public void A_wire_value_is_a_weight_in_the_parameters_range_and_a_value_within_one_step_of_zero_is_zero()
    {
        Near(-2.3f, AvatarShape.WeightOf(0, -2.3f, 2f));
        Near(2f, AvatarShape.WeightOf(255, -2.3f, 2f));
        Assert.Equal(0f, AvatarShape.WeightOf(128, -1f, 1f));          // 0.0039 is under one step (2/255)
        Near(128f / 255f, AvatarShape.WeightOf(128, 0f, 1f));
    }

    [Fact]
    public void A_driver_passes_a_weight_down_on_the_trapezoid_and_a_driver_missing_from_the_wire_passes_its_default()
    {
        var def = Def();
        var w = AvatarShape.ResolveWeights(def, Wire((500, 255)));      // driver at +1: driven 0 + (1 - -1)/(1 - -1) * 2 = 2
        Near(2f, w[501]);
        Near(0f, AvatarShape.ResolveWeights(def, Wire((500, 0)))[501]); // driver at -1: the driven minimum
        Near(1f, AvatarShape.ResolveWeights(def, Wire())[501]);         // no wire value: the driver's default 0 drives 1
    }

    [Fact]
    public void A_driver_value_outside_its_trapezoid_follows_the_official_edges()
    {
        var driver = new ShapeParam(1, "d", 0f, 10f, 0f, ShapeSex.Both, [], []);
        var driven = new ShapeParam(2, "x", 10f, 20f, 10f, ShapeSex.Both, [], []);
        var e = new DrivenEntry(2, 2f, 4f, 6f, 8f);
        Near(10f, AvatarShape.DrivenWeight(driver, e, driven, 1f));     // below min1: the driven minimum
        Near(15f, AvatarShape.DrivenWeight(driver, e, driven, 3f));     // on the way up
        Near(20f, AvatarShape.DrivenWeight(driver, e, driven, 5f));     // held between max1 and max2
        Near(15f, AvatarShape.DrivenWeight(driver, e, driven, 7f));     // on the way down
        Near(10f, AvatarShape.DrivenWeight(driver, e, driven, 9f));     // past min2, max2 below the driver's maximum: the minimum
        Near(20f, AvatarShape.DrivenWeight(driver, new DrivenEntry(2, 2f, 4f, 10f, 10f), driven, 10f));
    }

    // ---- scale, offset, sex ----

    [Fact]
    public void With_no_parameters_but_the_defaults_the_skeleton_takes_each_parameters_default_weight()
    {
        var rig = Rig();
        var shape = AvatarShape.From(rig, Def(), Wire());
        Assert.False(shape.Male);
        // chest x: 1 + the shoulder parameter's default (-0.5 * 0.4) + the driven parameter at the driver's default 0 -> 1 (1 * 0.5)
        Near(1f - 0.2f + 0.5f, shape.Scale[rig.Find("mChest")].X);
        Near(1.075f, shape.Scale[rig.Find("mNeck")].Z);               // a female takes the male-only parameter's default 0.25 * 0.3
        Near(1f, shape.Scale[rig.Find("mTorso")].Z);                  // height and male skeleton both at zero
    }

    [Fact]
    public void A_female_gets_the_default_weight_of_a_male_only_parameter_and_a_male_gets_the_transmitted_one()
    {
        var rig = Rig();
        int neck = rig.Find("mNeck");
        // female: effective weight = the parameter's default 0.25, whatever was transmitted -> 1 + 0.25 * 0.3
        Near(1.075f, AvatarShape.From(rig, Def(), Wire((34, 255))).Scale[neck].Z);
        // male: the transmitted 1.0 -> 1 + 1 * 0.3
        Near(1.3f, AvatarShape.From(rig, Def(), Wire((80, 255), (34, 255))).Scale[neck].Z);
    }

    [Fact]
    public void The_male_parameter_above_one_half_makes_a_male_and_drives_the_male_skeleton_with_its_bones_and_collision_volumes()
    {
        var rig = Rig();
        int torso = rig.Find("mTorso"), belly = rig.Find("BELLY");
        var female = AvatarShape.From(rig, Def(), Wire());
        Assert.False(female.Male);
        Near(1f, female.Scale[torso].Z);
        Near(0.1f, female.Position[torso].Z);
        var male = AvatarShape.From(rig, Def(), Wire((80, 255)));
        Assert.True(male.Male);
        Near(1.2f, male.Scale[torso].Z);                              // + 1 * 0.2
        Near(0.11f, male.Position[torso].Z);                          // + 1 * 0.01
        Near(new Vector3(0.1f, 0.2f, 0.3f + 0.3f * 0.2f), male.Scale[belly]);   // a collision volume under the bone grows with it: own size times the bone's change
        Assert.False(AvatarShape.From(rig, Def(), Wire((80, 127))).Male);   // 127 is 0.498
        Assert.True(AvatarShape.From(rig, Def(), Wire((80, 128))).Male);    // 128 is 0.502
    }

    [Fact]
    public void Extreme_weights_move_and_scale_every_joint_they_name_and_the_driver_chain_reaches_the_skeleton()
    {
        var rig = Rig();
        var s = AvatarShape.From(rig, Def(), Wire((33, 255), (500, 255), (11001, 255)));
        Near(1.4f, s.Scale[rig.Find("mKneeLeft")].Z);                 // 1 + 2 * 0.2
        Near(-0.5f - 0.1f, s.Position[rig.Find("mKneeLeft")].Z);      // -0.5 + 2 * -0.05
        Near(1.2f, s.Scale[rig.Find("mTorso")].Z);                    // 1 + 2 * 0.1
        Near(1f - 0.2f + 2f * 0.5f, s.Scale[rig.Find("mChest")].X);   // the shoulder default, and the driver at +1 driving 501 to 2
        Near(2f, s.Hover);
        var low = AvatarShape.From(rig, Def(), Wire((33, 0), (11001, 0)));
        Near(1f - 0.4f, low.Scale[rig.Find("mKneeLeft")].Z);          // 1 + -2 * 0.2
        Near(-2f, low.Hover);
    }

    // ---- height and hover ----

    [Fact]
    public void The_body_height_is_the_official_sum_of_joint_positions_and_scales_and_follows_the_shape()
    {
        var rig = Rig();
        float root2 = MathF.Sqrt(2f);
        // the skeleton as the file has it: pelvis to foot = -0.05 - (-0.5) - (-0.5) - (-0.1) = 1.05 with every scale one
        var neutral = AvatarShape.Neutral(rig).Measure(null)!.Value;
        Near(1.05f, neutral.PelvisToFoot);
        Near(neutral.PelvisToFoot + root2 * 0.1f + 0.1f + 0.25f + 0.2f + 0.1f, neutral.Height);
        // a tall shape: knee scale 1.4, torso scale 1.2 (Height at +2), knee offset -0.6
        var s = AvatarShape.From(rig, Def(), Wire((33, 255), (35, 128)));
        float pelvisToFoot = -0.05f * 1f - (-0.6f) * 1f - (-0.5f) * 1.4f - (-0.1f) * 1f;     // hip.z*pelvisScale - knee.z*hipScale - ankle.z*kneeScale - foot.z*ankleScale
        var m = s.Measure(null)!.Value;
        Near(pelvisToFoot, m.PelvisToFoot);
        // the female takes the male-only parameter's default, so the neck's scale is 1.075 (head.z * neckScale)
        Near(pelvisToFoot + root2 * 0.1f * 1f + 0.1f * 1.075f + 0.25f * 1f + 0.2f * 1.2f + 0.1f * 1f, m.Height);
        Assert.True(m.Height > neutral.Height);
    }

    [Fact]
    public void A_worn_meshs_joint_override_changes_the_height_and_a_scale_lock_pins_the_scale()
    {
        var rig = Rig();
        var shape = AvatarShape.From(rig, Def(), Wire((33, 255)));
        var wear = new RiggedWear(rig);
        // a mesh body overriding the left hip to z = -0.30 and the pelvis scale... only positions are overridden; lock the knee's scale
        var skin = new MeshSkinBlock(["mHipLeft", "mKneeLeft"], [Matrix4x4.Identity, Matrix4x4.Identity], Matrix4x4.Identity,
            [Matrix4x4.CreateTranslation(0, 0.1f, -0.30f), Matrix4x4.CreateTranslation(0, 0, -0.5f)], 0f, true, false);
        Assert.True(wear.Add(1, new Guid(1, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]), skin));
        int hip = rig.Find("mHipLeft"), knee = rig.Find("mKneeLeft");
        Near(-0.30f, shape.PositionOf(hip, wear.Overrides).Z);          // the override wins over the shape's position
        Near(-0.60f, shape.PositionOf(knee, wear.Overrides).Z);         // the knee's -0.5 equals its default (no override, so no lock): the shape's stays
        Near(1.0f, shape.ScaleOf(hip, wear.Overrides).Z);               // an overridden joint of a mesh with the lock flag: the skeleton's own scale
        Near(1.4f, shape.ScaleOf(knee, wear.Overrides).Z);              // not overridden, not locked: the shape's
        Near(1.0f, shape.ScaleOf(hip, null).Z);
        var with = shape.Measure(wear.Overrides)!.Value;
        float pelvisToFoot = -0.30f * 1f - (-0.6f) * 1f - (-0.5f) * 1.4f - (-0.1f) * 1f;   // hip.z*pelvis - knee.z*hip - ankle.z*knee - foot.z*ankle
        Near(pelvisToFoot, with.PelvisToFoot);
        Assert.True(with.PelvisToFoot < shape.Measure(null)!.Value.PelvisToFoot);   // the mesh body's shorter hip
    }

    // ---- the joints: shape, then overrides, composed as the official viewer does ----

    [Fact]
    public void A_scaled_joint_scales_its_childrens_offsets_but_its_children_do_not_inherit_the_scale()
    {
        var rig = Rig();
        var joints = new AvatarJoints(rig) { Shape = AvatarShape.From(rig, Def(), Wire((80, 255))) };   // male: torso scale z 1.2, torso at z 0.11
        joints.Begin(null, 0);
        int torso = rig.Find("mTorso"), chest = rig.Find("mChest");
        Near(new Vector3(0, 0, 0.11f), joints.World(torso).Translation);
        Near(1.2f, joints.World(torso).M33);                             // the torso's matrix carries its own scale
        Near(new Vector3(0, 0, 0.11f + 0.2f * 1.2f), joints.World(chest).Translation);   // the chest's offset is scaled by the TORSO's scale
        Near(1f, joints.World(chest).M33);                               // the chest's own scale in z is one: the torso's 1.2 is not inherited
        Near(1.3f, joints.World(chest).M11);                             // its own x scale: 1 - 0.2 (shoulder default) + 0.5 (driven at the driver's default)
    }

    [Fact]
    public void A_position_override_replaces_the_shapes_position_and_a_scale_lock_pins_the_scale_in_the_joint_matrices()
    {
        var rig = Rig();
        var shape = AvatarShape.From(rig, Def(), Wire((80, 255)));
        var wear = new RiggedWear(rig);
        var skin = new MeshSkinBlock(["mTorso"], [Matrix4x4.Identity], Matrix4x4.Identity, [Matrix4x4.CreateTranslation(0, 0, 0.3f)], 0f, true, false);
        wear.Add(1, new Guid(1, 0, 0, [0, 0, 0, 0, 0, 0, 0, 2]), skin);
        var joints = new AvatarJoints(rig) { Shape = shape, Overrides = wear.Overrides };
        joints.Begin(null, 0);
        int torso = rig.Find("mTorso"), chest = rig.Find("mChest");
        Near(new Vector3(0, 0, 0.3f), joints.World(torso).Translation);          // not the shape's 0.11
        Near(1f, joints.World(torso).M33);                                       // locked: not the shape's 1.2
        Near(new Vector3(0, 0, 0.3f + 0.2f * 1f), joints.World(chest).Translation);
    }

    [Fact]
    public void A_rigged_mesh_on_the_shaped_skeleton_is_deformed_by_the_shaped_joint_matrices()
    {
        // one vertex bound wholly to the torso with the inverse of the torso's default world (the torso at z 0.1 in the pelvis frame):
        // on a torso scaled by 1.2 and moved to 0.11 the vertex at 0.3 goes to (0.3 - 0.1) * 1.2 + 0.11
        var rig = Rig();
        var joints = new AvatarJoints(rig) { Shape = AvatarShape.From(rig, Def(), Wire((80, 255))) };
        joints.Begin(null, 0);
        var skin = new MeshSkinBlock(["mTorso"], [Matrix4x4.CreateTranslation(0, 0, -0.1f)], Matrix4x4.Identity, [], 0f, false, false);
        var palette = new Matrix4x4[1];
        var worlds = new Matrix4x4[rig.NodeCount + 1];
        joints.Fill(worlds);
        AttachmentPlacement.Palette(skin, [rig.Find("mTorso")], worlds, palette);
        var p = AttachmentPlacement.SkinPosition(new Vector3(0, 0, 0.3f), Matrix4x4.Identity, palette, new byte[4], [1f, 0, 0, 0]);
        Near(new Vector3(0, 0, (0.3f - 0.1f) * 1.2f + 0.11f), p);
    }

    // ---- the real avatar definition file, against a second computation from the file's data ----

    private static (AvatarRig Rig, ShapeDefinition Def, XDocument Lad, XDocument Skeleton) Real()
    {
        string dir = CharacterFactAttribute.Folder!;
        byte[] lad = File.ReadAllBytes(Path.Combine(dir, "avatar_lad.xml")), sk = File.ReadAllBytes(Path.Combine(dir, "avatar_skeleton.xml"));
        return (new AvatarRig(CharacterSkeleton.Parse(sk, lad)), ShapeDefinition.Parse(lad), XDocument.Parse(Encoding.UTF8.GetString(lad)), XDocument.Parse(Encoding.UTF8.GetString(sk)));
    }

    private static float F(string? s, float d = 0f) => s is null ? d : float.Parse(s, CultureInfo.InvariantCulture);
    private static Vector3 V(string? s) => s is null ? Vector3.Zero : s.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var x, var y, var z] ? new(F(x), F(y), F(z)) : Vector3.Zero;

    /// <summary>The expected scale and position of every bone, worked out here from the file's param elements: each skeletal
    /// parameter's weight (the transmitted one; the male parameter drives parameter 32; a parameter the wire lacks takes its default, and a
    /// driven one is taken at its driver's default through the trapezoid), its default for the other sex, summed onto the skeleton's values.</summary>
    private static (Dictionary<string, Vector3> Scale, Dictionary<string, Vector3> Pos, bool Male) Expected(XDocument lad, XDocument skeleton, Dictionary<int, byte> wire)
    {
        var all = lad.Descendants("param").Where(p => p.Attribute("id") is not null).GroupBy(p => (int)p.Attribute("id")!).ToDictionary(g => g.Key, g => g.First());
        float Min(int id) => F((string?)all[id].Attribute("value_min"), 0f);
        float Max(int id) => F((string?)all[id].Attribute("value_max"), 1f);
        float Def(int id) => Math.Clamp(F((string?)all[id].Attribute("value_default")), Min(id), Max(id));
        float Quant(int id, byte b)
        {
            float lo = Min(id), hi = Max(id), v = b / 255f * (hi - lo) + lo;
            return MathF.Abs(v) < (hi - lo) / 255f ? 0f : v;
        }
        var weight = all.Keys.ToDictionary(k => k, Def);
        void Drive(int driver)
        {
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
                Drive(id);
            }
        }
        foreach (var id in all.Keys.OrderBy(k => k)) Drive(id);
        foreach (var (id, b) in wire.OrderBy(kv => kv.Key)) { weight[id] = Quant(id, b); Drive(id); }
        bool male = weight[80] > 0.5f;
        var sk = skeleton.Descendants().Where(e => e.Name == "bone" || e.Name == "collision_volume").ToDictionary(e => (string)e.Attribute("name")!);
        var scale = sk.ToDictionary(kv => kv.Key, kv => kv.Value.Name == "bone" ? Vector3.One : V((string?)kv.Value.Attribute("scale")));
        var pos = sk.ToDictionary(kv => kv.Key, kv => V((string?)kv.Value.Attribute("pos")));
        foreach (var p in all.Values.OrderBy(p => (int)p.Attribute("id")!))
        {
            var ps = p.Element("param_skeleton");
            if (ps is null) continue;
            int id = (int)p.Attribute("id")!;
            string? sex = (string?)p.Attribute("sex");
            bool applies = sex is null or "both" || (sex == "male" && male) || (sex == "female" && !male);
            float eff = applies ? weight[id] : Def(id);
            foreach (var b in ps.Elements("bone"))
            {
                string name = (string)b.Attribute("name")!;
                var d = V((string?)b.Attribute("scale"));
                scale[name] += eff * d;
                foreach (var cv in sk[name].Elements("collision_volume")) scale[(string)cv.Attribute("name")!] += eff * (V((string?)cv.Attribute("scale")) * d);
                if (b.Attribute("offset") is { } o) pos[name] += eff * V((string)o);
            }
        }
        // the volume morphs of the morph parameters (listed under the level-0 meshes) also scale and move collision volumes
        foreach (var p in lad.Descendants("mesh").Where(m => (string?)m.Attribute("lod") == "0").SelectMany(m => m.Elements("param")))
        {
            var morph = p.Element("param_morph");
            if (morph is null) continue;
            int id = (int)p.Attribute("id")!;
            string? sex = (string?)p.Attribute("sex");
            bool applies = sex is null or "both" || (sex == "male" && male) || (sex == "female" && !male);
            float eff = applies ? weight[id] : Def(id);
            foreach (var vm in morph.Elements("volume_morph"))
            {
                string name = (string)vm.Attribute("name")!;
                if (!scale.ContainsKey(name)) continue;
                scale[name] += eff * V((string?)vm.Attribute("scale"));
                pos[name] += eff * V((string?)vm.Attribute("pos"));
            }
        }
        return (scale, pos, male);
    }

    private static void AgainstTheFile(Dictionary<int, byte> wire)
    {
        var (rig, def, lad, skeleton) = Real();
        var shape = AvatarShape.From(rig, def, new VisualParamSet(wire.OrderBy(kv => kv.Key).Select(kv => kv.Value), [.. wire.Keys.OrderBy(k => k)]));
        var (scale, pos, male) = Expected(lad, skeleton, wire);
        Assert.Equal(male, shape.Male);
        foreach (var j in rig.Skeleton.Joints)
        {
            Near(scale[j.Name], shape.Scale[j.Index], 1e-4f);
            Near(pos[j.Name], shape.Position[j.Index], 1e-4f);
        }
    }

    [CharacterFact]
    public void A_default_female_matches_the_files_own_data()
    {
        AgainstTheFile([]);
        var (rig, def, _, _) = Real();
        Assert.False(AvatarShape.From(rig, def, Wire()).Male);
    }

    [CharacterFact]
    public void A_default_male_matches_the_files_own_data_and_gets_the_male_skeleton()
    {
        AgainstTheFile(new() { [80] = 255 });
        var (rig, def, _, _) = Real();
        var female = AvatarShape.From(rig, def, Wire());
        var male = AvatarShape.From(rig, def, Wire((80, 255)));
        Assert.True(male.Male);
        Assert.NotEqual(female.Scale[rig.Find("mNeck")], male.Scale[rig.Find("mNeck")]);   // the male skeleton is longer in the neck and wider in the shoulders
    }

    [CharacterFact]
    public void Extreme_weights_both_ways_match_the_files_own_data()
    {
        // every transmitted shape parameter that moves the skeleton, all at the top, then all at the bottom, as a male and as a female
        var (_, def, _, _) = Real();
        var skeletal = def.InIdOrder.Where(p => p.Bones.Count > 0).Select(p => p.Id).ToHashSet();
        foreach (byte v in new byte[] { 255, 0 })
            foreach (bool male in new[] { true, false })
            {
                var wire = new Dictionary<int, byte>();
                foreach (var id in new[] { 33, 34, 36, 37, 38, 675, 683, 692, 693, 756, 842, 682 }.Where(skeletal.Contains)) wire[id] = v;
                wire[80] = (byte)(male ? 255 : 0);
                AgainstTheFile(wire);
            }
    }

    [CharacterFact]
    public void The_shape_editors_height_for_the_default_female_is_the_sum_the_official_viewer_works_out()
    {
        var (rig, def, _, _) = Real();
        var m = AvatarShape.From(rig, def, Wire()).Measure(null)!.Value;
        Assert.InRange(m.Height, 1.5f, 2.2f);                  // a default avatar is a little under two metres
        var tall = AvatarShape.From(rig, def, Wire((33, 255), (692, 255), (80, 255))).Measure(null)!.Value;
        Assert.True(tall.Height > m.Height + 0.2f, $"{tall.Height} vs {m.Height}");
    }

    [CharacterFact]
    public void The_default_body_and_the_rigged_joints_agree_on_a_shaped_skeleton_with_overrides()
    {
        // the default body is skinned by the same joint matrices a rigged mesh would be: for every joint in its palette, the default
        // world times the pose's skinning matrix is the joint's world in the pelvis frame
        string dir = CharacterFactAttribute.Folder!;
        var body = AvatarBody.Load(dir);
        var rig = new AvatarRig(body.Skeleton);
        var shape = AvatarShape.From(rig, body.Shape!, Wire((33, 255), (80, 255), (692, 200)));
        var wear = new RiggedWear(rig);
        int pelvisLess = rig.Find("mKneeLeft");
        var skin = new MeshSkinBlock(["mKneeLeft"], [Matrix4x4.Identity], Matrix4x4.Identity, [Matrix4x4.CreateTranslation(0.01f, 0, -0.5f)], 0f, true, false);
        Assert.True(wear.Add(1, new Guid(1, 0, 0, [0, 0, 0, 0, 0, 0, 0, 3]), skin));
        var pose = new AvatarPose(body, null);
        var matrices = new Matrix4x4[body.Palette.Length];
        pose.Evaluate(0, matrices, shape, wear.Overrides);
        var joints = new AvatarJoints(rig) { Shape = shape, Overrides = wear.Overrides };
        joints.Begin(null, 0);
        int checkedJoints = 0;
        for (int i = 0; i < body.Palette.Length; i++)
        {
            int j = body.Palette[i];
            if (j == SkinBinding.Root) continue;
            var expected = joints.World(j);
            var actual = body.Skeleton.DefaultWorld[j] * matrices[i];
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    Assert.True(MathF.Abs(expected[r, c] - actual[r, c]) < 2e-4f, $"{body.Skeleton.Joints[j].Name} [{r},{c}] {expected[r, c]} vs {actual[r, c]}");
            checkedJoints++;
        }
        Assert.True(checkedJoints > 10);
        Assert.Contains(pelvisLess, body.Palette);                       // the overridden joint is one the default body is skinned by
    }
}
