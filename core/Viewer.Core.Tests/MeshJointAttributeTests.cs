// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using System.Text;
using Viewer.Core.Avatars;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The avatar definition's optional <c>joint</c> attribute on a <c>mesh</c> element, naming the joint an unweighted mesh (an eyeball) rides on,
/// and separate left and right eye mesh files. Absent, the placement is what it always was (the left eye on mEyeLeft, the right on mEyeRight).
/// Small synthetic files are used (the skeleton of <see cref="AvatarTests"/>); the stock character folder is read where the machine has it.
/// Every member builds its own objects, so the class runs in parallel.
/// </summary>
public class MeshJointAttributeTests
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private const string Skeleton = AvatarTests.SkeletonXml;

    /// <summary>A definition with a joint on the left eye only, a second file for the right eye, and a joint on a level of detail other than 0 (ignored).</summary>
    private const string WithJoint = """
        <linden_avatar version="2.0">
          <skeleton file_name="avatar_skeleton.xml"/>
          <mesh type="eyeBallLeftMesh" lod="0" file_name="eye_left.llm" joint="mNeck"/>
          <mesh type="eyeBallRightMesh" lod="0" file_name="eye_right.llm"/>
          <mesh type="eyeBallLeftMesh" lod="1" file_name="eye_left_1.llm" joint="mChest"/>
        </linden_avatar>
        """;

    private const string WithoutJoint = """
        <linden_avatar version="2.0">
          <skeleton file_name="avatar_skeleton.xml"/>
          <mesh type="eyeBallLeftMesh" lod="0" file_name="eye_left.llm"/>
          <mesh type="eyeBallRightMesh" lod="0" file_name="eye_right.llm"/>
        </linden_avatar>
        """;

    private static readonly IReadOnlyList<(string Type, string Bake, string? RigidJoint)> Eyes =
        [("eyeBallLeftMesh", "eyes", "mCollarLeft"), ("eyeBallRightMesh", "eyes", "mChest")];

    private static byte[] Ball(Vector3 at) => AvatarTests.Llm([at + new Vector3(0.01f, 0, 0), at, at + Vector3.UnitY * 0.01f], [0, 1, 2], null, [], detail: false);

    private static Func<string, byte[]> Files(string definition, Vector3 left, Vector3 right) => name => name switch
    {
        "avatar_skeleton.xml" => Utf8(Skeleton),
        "avatar_lad.xml" => Utf8(definition),
        "eye_left.llm" => Ball(left),
        "eye_right.llm" => Ball(right),
        _ => throw new FileNotFoundException(name),
    };

    [Fact]
    public void The_joint_attribute_is_read_per_mesh_type_at_full_detail_only()
    {
        var joints = AvatarBody.MeshJoints(Utf8(WithJoint));
        Assert.Equal(new Dictionary<string, string> { ["eyeBallLeftMesh"] = "mNeck" }, joints);
        Assert.Empty(AvatarBody.MeshJoints(Utf8(WithoutJoint)));
    }

    [Fact]
    public void A_definition_with_no_joint_attribute_resolves_to_the_default_parts_exactly()
    {
        Assert.Equal(AvatarBody.DefaultParts, AvatarBody.ResolveParts(Utf8(WithoutJoint), AvatarBody.DefaultParts));
    }

    [Fact]
    public void A_joint_attribute_replaces_that_parts_joint_and_no_other()
    {
        var parts = AvatarBody.ResolveParts(Utf8(WithJoint), AvatarBody.DefaultParts);
        Assert.Equal(AvatarBody.DefaultParts.Count, parts.Count);
        foreach (var (resolved, stock) in parts.Zip(AvatarBody.DefaultParts))
        {
            Assert.Equal((stock.Type, stock.Bake), (resolved.Type, resolved.Bake));
            Assert.Equal(stock.Type == "eyeBallLeftMesh" ? "mNeck" : stock.RigidJoint, resolved.RigidJoint);
        }
    }

    [Fact]
    public void An_eye_mesh_bound_to_another_joint_is_drawn_at_that_joint()
    {
        var read = Files(WithJoint, new Vector3(0.05f, 0, 0), new Vector3(0.05f, 0, 0));
        var body = AvatarBody.LoadFrom(read, Eyes);
        var s = body.Skeleton;
        var left = body.Parts.Single(p => p.Type == "eyeBallLeftMesh");
        var right = body.Parts.Single(p => p.Type == "eyeBallRightMesh");
        // the definition binds the left eye to the neck: it rides the neck, from the neck's default place (z 1.55)
        Assert.Equal(1.55f, left.Positions[1].Z, 3);
        Assert.Contains(s.IndexOf("mNeck"), body.Palette);
        // the right eye names nothing: it keeps the joint the caller's list gave it (the chest, z 1.3)
        Assert.Equal(1.3f, right.Positions[1].Z, 3);
        Assert.DoesNotContain(s.IndexOf("mCollarLeft"), body.Palette);
    }

    [Fact]
    public void Without_the_attribute_each_eye_keeps_the_joint_it_always_had()
    {
        var body = AvatarBody.LoadFrom(Files(WithoutJoint, new Vector3(0.05f, 0, 0), new Vector3(0.05f, 0, 0)), Eyes);
        Assert.Equal(1.5f, body.Parts.Single(p => p.Type == "eyeBallLeftMesh").Positions[1].Z, 3);   // the collar (z 1.5)
        Assert.Equal(1.3f, body.Parts.Single(p => p.Type == "eyeBallRightMesh").Positions[1].Z, 3);
    }

    [Fact]
    public void Separate_left_and_right_eye_files_are_each_drawn_as_their_own_mesh()
    {
        var body = AvatarBody.LoadFrom(Files(WithoutJoint, new Vector3(0.05f, 0.03f, 0), new Vector3(0.05f, -0.03f, 0)), Eyes);
        var left = body.Parts.Single(p => p.Type == "eyeBallLeftMesh");
        var right = body.Parts.Single(p => p.Type == "eyeBallRightMesh");
        // each file's own vertices (before the joint's place is added): left at y +0.03, right at y -0.03, from the joints' default y (0.1 and 0)
        Assert.Equal(0.03f + 0.1f, left.Positions[1].Y, 3);
        Assert.Equal(-0.03f + 0f, right.Positions[1].Y, 3);
        Assert.NotEqual(left.Mesh.Positions[1], right.Mesh.Positions[1]);
    }

    [Fact]
    public void A_joint_that_is_not_in_the_skeleton_is_refused_with_its_name()
    {
        var def = WithoutJoint.Replace("file_name=\"eye_left.llm\"", "file_name=\"eye_left.llm\" joint=\"mNoSuchJoint\"");
        var ex = Assert.Throws<InvalidDataException>(() => AvatarBody.LoadFrom(Files(def, Vector3.Zero, Vector3.Zero), Eyes));
        Assert.Contains("mNoSuchJoint", ex.Message);
    }

    [CharacterFact]
    public void The_stock_character_definition_resolves_to_the_default_parts_exactly()
    {
        var lad = File.ReadAllBytes(Path.Combine(CharacterFactAttribute.Folder!, AvatarBody.DefinitionFile));
        Assert.Empty(AvatarBody.MeshJoints(lad));
        Assert.Equal(AvatarBody.DefaultParts, AvatarBody.ResolveParts(lad, AvatarBody.DefaultParts));
    }
}
