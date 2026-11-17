// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using System.Numerics;
using System.Xml;
using Viewer.Core.Avatars;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// An attachment point's rotation attribute in avatar_lad.xml, turned into a rotation the way the official viewer does it.
/// The official viewer reads it with LLQuaternion::setQuat(roll, pitch, yaw) (LLVOAvatar::buildCharacter, llvoavatar.cpp),
/// which turns a vector about z first, then about y, then about x (fixed axes). Skeleton joints use a different rule
/// (mayaQ with order XYZ: x first, then y, then z), so the two must not share one conversion. Every expected axis below is
/// worked by hand from right-handed quarter turns: about z, x goes to y and y to -x; about y, z goes to x and x to -z;
/// about x, y goes to z and z to -y. Pure, runs in parallel.
/// </summary>
public class AttachmentPointRotationTests
{
    /// <summary>Degrees as the file writes them, and where the point's own x, y and z axes end up in its joint's frame.</summary>
    public static readonly TheoryData<string, Vector3, Vector3, Vector3> Table = new()
    {
        // Chest (point 1) "0 90 90": z then y. x -> y -> y; y -> -x -> z; z -> z -> x.
        { "0 90 90", new(0, 1, 0), new(0, 0, 1), new(1, 0, 0) },
        // Skull (point 2) "0 0 90": a single turn about z.
        { "0 0 90", new(0, 1, 0), new(-1, 0, 0), new(0, 0, 1) },
        // Spine (point 9) "0 -90 90": z then y -90 (x -> z, z -> -x). x -> y -> y; y -> -x -> -z; z -> z -> -x.
        { "0 -90 90", new(0, 1, 0), new(0, 0, -1), new(-1, 0, 0) },
        // y then x: x -> -z -> y; y -> y -> z; z -> x -> x.
        { "90 90 0", new(0, 1, 0), new(0, 0, 1), new(1, 0, 0) },
        // all three, z then y then x: x -> y -> y -> z; y -> -x -> z -> -y; z -> z -> x -> x.
        { "90 90 90", new(0, 0, 1), new(0, -1, 0), new(1, 0, 0) },
    };

    private static Vector3 Degrees(string s)
    {
        var p = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        return new(p[0], p[1], p[2]);
    }

    private static void Near(Vector3 expected, Vector3 actual, string what, float tol = 1e-4f) =>
        Assert.True((expected - actual).Length() <= tol, $"{what}: expected {expected}, got {actual}");

    [Theory]
    [MemberData(nameof(Table))]
    public void A_point_rotation_turns_about_z_first_then_y_then_x_as_the_official_viewer_reads_it(string degrees, Vector3 x, Vector3 y, Vector3 z)
    {
        var q = CharacterSkeleton.PointRotation(Degrees(degrees));
        Near(x, Vector3.Transform(Vector3.UnitX, q), $"{degrees} x");
        Near(y, Vector3.Transform(Vector3.UnitY, q), $"{degrees} y");
        Near(z, Vector3.Transform(Vector3.UnitZ, q), $"{degrees} z");
    }

    [CharacterFact]
    public void Every_point_in_the_definition_with_a_rotation_gets_the_official_viewers_rotation()
    {
        string dir = CharacterFactAttribute.Folder!;
        var lad = File.ReadAllBytes(Path.Combine(dir, "avatar_lad.xml"));
        var rig = new AvatarRig(CharacterSkeleton.Parse(File.ReadAllBytes(Path.Combine(dir, "avatar_skeleton.xml")), lad));
        var table = Table.Select(r => ((string)r[0], (Vector3)r[1], (Vector3)r[2], (Vector3)r[3])).ToDictionary(r => r.Item1);

        var doc = new XmlDocument();
        using (var ms = new MemoryStream(lad)) doc.Load(ms);
        int rotated = 0;
        foreach (var e in doc.GetElementsByTagName("attachment_point").OfType<XmlElement>())
        {
            var deg = Degrees(e.GetAttribute("rotation") is { Length: > 0 } r ? r : "0 0 0");
            if (deg == Vector3.Zero) continue;
            rotated++;
            string key = string.Join(' ', new[] { deg.X, deg.Y, deg.Z }.Select(v => v.ToString(CultureInfo.InvariantCulture)));
            Assert.True(table.TryGetValue(key, out var row), $"point {e.GetAttribute("id")} ({e.GetAttribute("name")}) has rotation {key}, which the table does not work out");
            var q = rig.DefaultRotation[rig.PointNode(int.Parse(e.GetAttribute("id"), CultureInfo.InvariantCulture))];
            string what = $"point {e.GetAttribute("id")} ({e.GetAttribute("name")}) {key}";
            Near(row.Item2, Vector3.Transform(Vector3.UnitX, q), what + " x");
            Near(row.Item3, Vector3.Transform(Vector3.UnitY, q), what + " y");
            Near(row.Item4, Vector3.Transform(Vector3.UnitZ, q), what + " z");
        }
        Assert.Equal(3, rotated);   // Chest, Skull and Spine in the official definition file
    }

    [CharacterFact]
    public void A_necklace_on_the_chest_point_sits_at_the_neck()
    {
        // A pendant on Chest (point 1), offset and turned in the point's frame so that the official viewer draws it at the
        // base of the neck, upright. Its offset and rotation are worked from the official point frame written out by hand
        // (Chest "0 90 90": the point's x is the body's y, its y the body's z, its z the body's x), not from Reverie's code.
        string dir = CharacterFactAttribute.Folder!;
        var rig = new AvatarRig(CharacterSkeleton.Parse(File.ReadAllBytes(Path.Combine(dir, "avatar_skeleton.xml")), File.ReadAllBytes(Path.Combine(dir, "avatar_lad.xml"))));
        var w = new Matrix4x4[rig.NodeCount];
        JointWorlds.Rest(rig, w);

        var pointInChest = new Matrix4x4(
            0, 1, 0, 0,
            0, 0, 1, 0,
            1, 0, 0, 0,
            0.15f, 0, -0.1f, 1);
        var officialPoint = pointInChest * w[rig.Find("mChest")];
        Assert.True(Matrix4x4.Invert(officialPoint, out var toPoint));
        var neck = Vector3.Transform(Vector3.Zero, w[rig.Find("mNeck")]);
        var offset = Vector3.Transform(neck, toPoint);
        // the pendant's own frame lines up with the body's: its rotation undoes the point's (the transpose of the rows above)
        var undo = Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            0, 0, 1, 0,
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 0, 1));

        var m = AttachmentPlacement.Rigid(rig, 1, w, offset, undo)!.Value;
        Near(neck, Vector3.Transform(Vector3.Zero, m), "necklace origin", 0.01f);
        // upright: the pendant's own +z is the body's +z, and its +x faces forward
        Near(Vector3.UnitZ, Vector3.TransformNormal(Vector3.UnitZ, m), "necklace up", 1e-3f);
        Near(Vector3.UnitX, Vector3.TransformNormal(Vector3.UnitX, m), "necklace forward", 1e-3f);
    }
}
