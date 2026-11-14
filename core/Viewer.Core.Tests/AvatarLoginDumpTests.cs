// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using System.Text;
using Viewer.Core.Avatars;
using Viewer.Core.Meshing;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The own-avatar dump written at each login: the joints a worn mesh moves and pins, and the character folder's fingerprint. Pure and independent: each test makes its own folder under the test temp root. Runs in parallel.</summary>
public class AvatarLoginDumpTests
{
    private const string SkeletonXml = """
        <linden_skeleton version="2.0">
          <bone name="mPelvis" pos="0 0 1" rot="0 0 0" scale="1 1 1" support="base">
            <bone name="mTorso" pos="0 0 0.1" rot="0 0 0" scale="1 1 1" support="base">
              <bone name="mChest" pos="0 0 0.2" rot="0 0 0" scale="1 1 1" support="base"/>
            </bone>
          </bone>
        </linden_skeleton>
        """;

    private static AvatarRig Rig() => new(CharacterSkeleton.Parse(Encoding.UTF8.GetBytes(SkeletonXml)));

    private static Guid Id(byte last) => new(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, last });

    private static string NewFolder() => TestTemp.NewFolder("login-dump");

    // ---- the wearables ----

    [Fact]
    public void The_worn_wearables_are_listed_by_type_and_layer_with_the_alpha_layers_marked_and_objects_left_out()
    {
        var current = new List<Viewer.Core.Inventory.OutfitEntry>
        {
            new(Id(1), Id(11), "Boots", 0, Viewer.Core.Inventory.WearableType.None, 0, true),
            new(Id(2), Id(12), "Upper alpha", 2, Viewer.Core.Inventory.WearableType.Alpha, 1, true),
            new(Id(3), Id(13), "Shirt", 2, Viewer.Core.Inventory.WearableType.Shirt, 0, true),
            new(Id(4), Id(14), "Lower alpha", 2, Viewer.Core.Inventory.WearableType.Alpha, 0, true),
            new(Id(5), Id(15), "Shape", 1, Viewer.Core.Inventory.WearableType.Shape, 0, false),
        };

        var worn = AvatarLoginDump.Wearables(current);

        Assert.Equal(new[] { "shape", "shirt", "alpha", "alpha" }, worn.Select(w => w.Type).ToArray());
        Assert.Equal(new[] { 0, 0, 0, 1 }, worn.Select(w => w.Layer).ToArray());
        Assert.Equal(new[] { Id(15), Id(13), Id(14), Id(12) }, worn.Select(w => w.Item).ToArray());
        Assert.Equal(new[] { "Shape", "Shirt", "Lower alpha", "Upper alpha" }, worn.Select(w => w.Name).ToArray());
        Assert.Equal(new[] { false, false, true, true }, worn.Select(w => w.Alpha).ToArray());
        Assert.Equal(new[] { true, false, false, false }, worn.Select(w => w.BodyPart).ToArray());
    }

    // ---- the overrides ----

    [Fact]
    public void A_mesh_that_moves_a_joint_and_pins_its_scale_is_listed_with_the_position_it_won()
    {
        var rig = Rig();
        var overrides = new JointOverrides(rig);
        var skin = new MeshSkinBlock(["mTorso", "mChest"], [Matrix4x4.Identity, Matrix4x4.Identity], Matrix4x4.Identity,
            [Matrix4x4.CreateTranslation(0, 0, 0.3f), Matrix4x4.CreateTranslation(0, 0, 0.2f)], 0.05f, true, false);   // the chest stays where it is
        Assert.True(overrides.Add(Id(7), skin));
        var report = AvatarLoginDump.Describe(overrides, rig);
        Assert.Equal(1, report.MeshCount);
        Assert.Equal(0.05f, report.PelvisFixupMetres);
        var torso = Assert.Single(report.Joints);
        Assert.Equal("mTorso", torso.Joint);
        Assert.Equal(new[] { 0f, 0f, 0.3f }, torso.Position);
        Assert.Equal(Id(7), torso.Winner);
        Assert.Equal(["mTorso"], report.ScaleLocked);
    }

    [Fact]
    public void An_avatar_wearing_no_rigged_mesh_has_an_empty_report()
    {
        var rig = Rig();
        var report = AvatarLoginDump.Describe(new JointOverrides(rig), rig);
        Assert.Equal(0, report.MeshCount);
        Assert.Equal(0f, report.PelvisFixupMetres);
        Assert.Empty(report.Joints);
        Assert.Empty(report.ScaleLocked);
    }

    // ---- the character folder ----

    [Fact]
    public void A_folder_is_fingerprinted_by_its_files_names_and_bytes()
    {
        string folder = NewFolder();
        File.WriteAllText(Path.Combine(folder, "a.xml"), "alpha");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "sub", "b.llm"), "beta");
        var first = AvatarLoginDump.Fingerprint(folder);
        Assert.NotNull(first);
        Assert.Equal(2, first!.Files);
        Assert.Equal(9, first.Bytes);
        Assert.Equal(2, first.FileHashes.Count);
        Assert.Contains("sub/b.llm", first.FileHashes.Keys);
        Assert.Equal(64, first.Hash.Length);
        Assert.Equal(first.Hash, AvatarLoginDump.Fingerprint(folder)!.Hash);   // the same folder, the same hash
        File.WriteAllText(Path.Combine(folder, "a.xml"), "alphb");               // one byte changes
        var changed = AvatarLoginDump.Fingerprint(folder)!;
        Assert.NotEqual(first.Hash, changed.Hash);
        Assert.NotEqual(first.FileHashes["a.xml"], changed.FileHashes["a.xml"]);
        Assert.Equal(first.FileHashes["sub/b.llm"], changed.FileHashes["sub/b.llm"]);
    }

    [Fact]
    public void A_missing_folder_has_no_fingerprint()
    {
        Assert.Null(AvatarLoginDump.Fingerprint(Path.Combine(NewFolder(), "absent")));
        Assert.Null(AvatarLoginDump.Fingerprint(null));
    }
}
