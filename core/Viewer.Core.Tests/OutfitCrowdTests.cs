// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Viewer.Core.Avatars;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The synthetic outfits the crowd scene dresses its avatars in, and the crowd's layout, without an engine: an outfit is seeded
/// (same seed, same bytes), keeps to what outfits from the grid have (joint lists, four influences a vertex that add up to one,
/// faces of three alpha kinds), every mesh has its own binds, and the layout puts every avatar on its own spot with a mix of
/// standing and walking and every outfit worn by someone.
/// </summary>
public class OutfitCrowdTests
{
    /// <summary>A skeleton of the shape the real one has: a pelvis, a spine, two arms and two legs of base joints, and extended
    /// joints (hands, face, wings) in dozens of short chains, 130 or so bones in all, with a collision volume under the pelvis.</summary>
    private static CharacterSkeleton Skeleton()
    {
        var sb = new StringBuilder("<linden_skeleton><bone name=\"mPelvis\" pos=\"0 0 1.0\" rot=\"0 0 0\" scale=\"1 1 1\" support=\"base\">");
        sb.Append("<collision_volume name=\"PELVIS\" pos=\"0 0 0\" rot=\"0 0 0\" scale=\"0.1 0.1 0.1\" support=\"base\"/>");
        int n = 0;
        void Chain(string prefix, int length, string step, string support)
        {
            for (int i = 0; i < length; i++) sb.Append($"<bone name=\"{prefix}{i}\" pos=\"{(i == 0 ? "0 0 0.05" : step)}\" rot=\"0 {(n++ % 5) * 3} 0\" scale=\"1 1 1\" support=\"{support}\">");
            for (int i = 0; i < length; i++) sb.Append("</bone>");
        }
        Chain("mSpine", 6, "0 0 0.12", "base");
        sb.Append("<bone name=\"mArmL\" pos=\"0 0.15 0.5\" rot=\"0 0 0\" scale=\"1 1 1\" support=\"base\">");
        Chain("armL", 5, "0 0.14 0", "base");
        for (int f = 0; f < 12; f++) Chain($"fingerL{f}_", 3, "0.02 0.01 0", "extended");
        sb.Append("</bone>");
        sb.Append("<bone name=\"mArmR\" pos=\"0 -0.15 0.5\" rot=\"0 0 0\" scale=\"1 1 1\" support=\"base\">");
        Chain("armR", 5, "0 -0.14 0", "base");
        for (int f = 0; f < 12; f++) Chain($"fingerR{f}_", 3, "0.02 -0.01 0", "extended");
        sb.Append("</bone>");
        foreach (var side in new[] { "L", "R" })
        {
            sb.Append($"<bone name=\"mLeg{side}\" pos=\"0 {(side == "L" ? 0.08 : -0.08)} -0.05\" rot=\"0 0 0\" scale=\"1 1 1\" support=\"base\">");
            Chain($"leg{side}", 4, "0 0 -0.22", "base");
            sb.Append("</bone>");
        }
        Chain("head", 4, "0.01 0 0.09", "base");
        for (int w = 0; w < 6; w++) Chain($"wing{w}_", 5, "0 0.1 0.03", "extended");
        sb.Append("</bone></linden_skeleton>");
        return CharacterSkeleton.Parse(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static string Digest(Outfit o)
    {
        var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var m in o.Meshes)
        {
            h.AppendData(m.JointList.SelectMany(BitConverter.GetBytes).ToArray());
            foreach (var b in m.InverseBind) h.AppendData(BitConverter.GetBytes(b.M41 + b.M11 * 3 + b.M23));
            foreach (var f in m.Faces)
            {
                h.AppendData(f.Joints);
                h.AppendData(f.Indices.SelectMany(BitConverter.GetBytes).ToArray());
                h.AppendData(f.Positions.SelectMany(p => new[] { p.X, p.Y, p.Z }).SelectMany(BitConverter.GetBytes).ToArray());
                h.AppendData(f.Weights.SelectMany(BitConverter.GetBytes).ToArray());
                h.AppendData([(byte)f.Alpha]);
            }
        }
        return Convert.ToHexString(h.GetHashAndReset());
    }

    [Fact]
    public void The_skeleton_the_tests_use_is_a_whole_bento_sized_one()
    {
        var sk = Skeleton();
        Assert.InRange(sk.BoneCount, 120, 140);
        Assert.True(sk.Joints.Count(j => !j.Base) > 80);
    }

    [Fact]
    public void Same_seed_gives_the_same_outfit_and_another_seed_another()
    {
        var sk = Skeleton();
        var a = Digest(OutfitGenerator.Generate(sk, new(20, 7)));
        Assert.Equal(a, Digest(OutfitGenerator.Generate(sk, new(20, 7))));
        Assert.NotEqual(a, Digest(OutfitGenerator.Generate(sk, new(20, 8))));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(30)]
    public void An_outfit_has_the_mesh_count_asked_for_and_each_mesh_a_joint_list_of_its_own(int meshes)
    {
        var sk = Skeleton();
        var outfit = OutfitGenerator.Generate(sk, new(meshes, 11));
        Assert.Equal(meshes, outfit.Meshes.Length);
        foreach (var m in outfit.Meshes)
        {
            Assert.InRange(m.JointList.Length, 6, OutfitMesh.MaxJoints);
            Assert.Equal(m.JointList.Length, m.JointList.Distinct().Count());
            Assert.All(m.JointList, j => Assert.Equal(JointKind.Bone, sk.Joints[j].Kind));
            Assert.Equal(m.JointList.Length, m.InverseBind.Length);
        }
        // no two meshes share a joint list with the same binds, so none can share a skeleton or a bind table
        Assert.Equal(meshes, outfit.Meshes.Select(m => string.Join(',', m.JointList) + "/" + m.InverseBind[0].M41.ToString("R")).Distinct().Count());
    }

    [Fact]
    public void Every_vertex_has_four_influences_into_its_own_joint_list_and_the_weights_add_up_to_one()
    {
        var sk = Skeleton();
        var outfit = OutfitGenerator.Generate(sk, new(20, 3));
        var alphas = new HashSet<OutfitFaceAlpha>();
        int manyInfluences = 0, vertices = 0;
        foreach (var m in outfit.Meshes)
        {
            Assert.InRange(m.Faces.Length, 1, 8);
            foreach (var f in m.Faces)
            {
                alphas.Add(f.Alpha);
                Assert.Equal(f.VertexCount * 4, f.Joints.Length);
                Assert.Equal(f.VertexCount * 4, f.Weights.Length);
                Assert.Equal(f.VertexCount, f.Normals.Length);
                Assert.Equal(f.VertexCount, f.Uvs.Length);
                Assert.All(f.Indices, i => Assert.InRange(i, 0, f.VertexCount - 1));
                Assert.All(f.Joints, j => Assert.InRange(j, 0, m.JointList.Length - 1));
                for (int v = 0; v < f.VertexCount; v++)
                {
                    float sum = 0;
                    int nonZero = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        float w = f.Weights[v * 4 + k];
                        Assert.InRange(w, 0f, 1f);
                        sum += w;
                        if (w > 0) nonZero++;
                    }
                    Assert.InRange(sum, 0.9999f, 1.0001f);
                    if (nonZero >= 3) manyInfluences++;
                    vertices++;
                }
            }
        }
        Assert.True(manyInfluences * 2 > vertices, $"{manyInfluences} of {vertices} vertices carry three or four weights");
        Assert.Equal(3, alphas.Count);
    }

    [Fact]
    public void An_outfit_of_twenty_meshes_is_about_the_size_of_a_commercial_one()
    {
        var sk = Skeleton();
        long vertices = 0, listed = 0;
        for (int seed = 1; seed <= 5; seed++)
        {
            var o = OutfitGenerator.Generate(sk, new(20, seed));
            vertices += o.VertexCount;
            listed += o.ListedJoints;
        }
        Assert.InRange(vertices / 5, 50_000, 160_000);
        Assert.InRange(listed / 5, 700, 1_400);
    }

    [Fact]
    public void A_crowd_puts_every_avatar_on_its_own_spot_on_a_grid_round_the_centre()
    {
        var slots = CrowdLayout.Place(25, 25, 2.5f);
        Assert.Equal(25, slots.Length);
        Assert.Equal(25, slots.Select(s => (s.X, s.Y)).Distinct().Count());
        Assert.Equal(0f, slots.Average(s => s.X), 3);
        Assert.Equal(0f, slots.Average(s => s.Y), 3);
        Assert.Equal(10f, slots.Max(s => s.X) - slots.Min(s => s.X), 3);   // five columns 2.5 m apart
        Assert.Equal(slots, CrowdLayout.Place(25, 25, 2.5f));
    }

    [Theory]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(100)]
    public void A_crowd_is_a_mix_of_walkers_and_standers_and_every_outfit_is_worn(int count)
    {
        var slots = CrowdLayout.Place(count, 12, 2.5f);
        int walking = slots.Count(s => s.Walking);
        Assert.InRange(walking, count / 2, count - 1);
        Assert.InRange(slots.Count(s => !s.Walking), 1, count / 2);
        Assert.Equal(Enumerable.Range(0, 12), slots.Select(s => s.Outfit).Distinct().Order());
        var perOutfit = slots.GroupBy(s => s.Outfit).Select(g => g.Count()).ToList();
        Assert.InRange(perOutfit.Max() - perOutfit.Min(), 0, 1);
    }

    [Fact]
    public void As_many_outfits_as_avatars_means_nobody_shares()
    {
        var slots = CrowdLayout.Place(40, 40, 2.5f);
        Assert.Equal(40, slots.Select(s => s.Outfit).Distinct().Count());
    }
}
