// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Meshing;
using Viewer.Core.World;

namespace Viewer.Core.Interaction;

/// <summary>A ray in region coordinates (x east, y north, z up). The direction need not be a unit vector.</summary>
public readonly record struct PickRay(Vector3 Origin, Vector3 Direction);

/// <summary>Where a ray met a prim: which prim and face, the surface coordinates, and the point and frame in region coordinates.</summary>
public sealed record PickHit(ObjectId Id, uint LocalId, Guid FullId, uint RootLocalId, int Face, Vector2 Uv, Vector2 St,
    Vector3 Position, Vector3 Normal, Vector3 Binormal, float Distance);

public enum PickKind { None, Object, Avatar, Land }

public sealed record PickResult(PickKind Kind, PickHit? Hit, AvatarState? Avatar, Vector3 Point, float Distance);

/// <summary>The generated faces of plain prim shapes, kept by whoever picks often (a session holds one). Not shared between sessions.</summary>
public sealed class PickGeometryCache
{
    private readonly Dictionary<PrimShape, IReadOnlyList<FaceGeometry>> _faces = new();

    public IReadOnlyList<FaceGeometry> Faces(PrimShape? shape)
    {
        // A sculpt or mesh prim is tested as a box: its faces are not generated here, so its face and surface coordinates are approximate.
        var key = shape is null || shape.IsSculpt || shape.SculptType == PrimShape.SculptTypeMesh ? PrimShape.Box : shape;
        lock (_faces)
        {
            if (!_faces.TryGetValue(key, out var faces))
            {
                faces = PrimGeometry.Build(key, PrimDetail.Medium);
                if (_faces.Count > 4096)
                    _faces.Clear();
                _faces[key] = faces;
            }
            return faces;
        }
    }
}

/// <summary>Finds what a ray from the camera meets: the nearest prim, avatar or ground.</summary>
public static class ObjectPicker
{
    /// <summary>The avatar's pick box: half-width and half-depth in metres (its height comes from its size).</summary>
    private const float AvatarHalfWidth = 0.3f;

    /// <summary>Tests one prim at a world pose: its generated faces for a plain prim, its bounding box for a sculpt or mesh.</summary>
    public static PickHit? PickPrim(ObjectState prim, Vector3 position, Quaternion rotation, PickRay ray, uint rootLocalId = 0, PickGeometryCache? cache = null)
    {
        var inv = Quaternion.Inverse(rotation);
        var scale = prim.Scale;
        if (!(scale.X > 0f && scale.Y > 0f && scale.Z > 0f))
            return null;
        var o = Vector3.Transform(ray.Origin - position, inv) / scale;
        var d = Vector3.Transform(ray.Direction, inv) / scale;
        if (!SlabHit(o, d, new Vector3(-0.5f), new Vector3(0.5f), out _))
            return null;

        var faces = (cache ?? new PickGeometryCache()).Faces(prim.Shape);
        float bestT = float.MaxValue, bu = 0, bv = 0;
        int bestFace = -1;
        Vector3 bp0 = default, bp1 = default, bp2 = default, bn0 = default, bn1 = default, bn2 = default;
        Vector2 buv0 = default, buv1 = default, buv2 = default;
        foreach (var f in faces)
        {
            var pos = f.Positions;
            var uv = f.Uvs;
            var nor = f.Normals;
            for (int t = 0; t + 2 < f.Indices.Length; t += 3)
            {
                int i0 = f.Indices[t], i1 = f.Indices[t + 1], i2 = f.Indices[t + 2];
                var p0 = At(pos, i0); var p1 = At(pos, i1); var p2 = At(pos, i2);
                if (!Triangle(o, d, p0, p1, p2, out float tt, out float u, out float v) || tt >= bestT)
                    continue;
                bestT = tt; bestFace = f.Face; bu = u; bv = v;
                bp0 = p0; bp1 = p1; bp2 = p2;
                buv0 = Uv(uv, i0); buv1 = Uv(uv, i1); buv2 = Uv(uv, i2);
                bn0 = Normal(nor, i0); bn1 = Normal(nor, i1); bn2 = Normal(nor, i2);
            }
        }
        if (bestFace < 0)
            return null;

        float w = 1f - bu - bv;
        var st = buv0 * w + buv1 * bu + buv2 * bv;
        var nLocal = bn0 * w + bn1 * bu + bn2 * bv;
        if (nLocal.LengthSquared() < 1e-8f)
            nLocal = Vector3.Cross(bp1 - bp0, bp2 - bp0);
        if (Vector3.Dot(nLocal, d) > 0f)
            nLocal = -nLocal;   // the side that faces the viewer
        var normal = Vector3.Normalize(Vector3.Transform(nLocal / scale, rotation));

        // The tangent along increasing U: from the triangle's edges and its UV steps.
        var e1 = bp1 - bp0; var e2 = bp2 - bp0;
        var d1 = buv1 - buv0; var d2 = buv2 - buv0;
        float det = d1.X * d2.Y - d2.X * d1.Y;
        Vector3 tangentLocal = Math.Abs(det) > 1e-9f ? (e1 * d2.Y - e2 * d1.Y) / det : e1;
        var tangent = Vector3.Transform(tangentLocal * scale, rotation);
        tangent -= normal * Vector3.Dot(tangent, normal);
        if (tangent.LengthSquared() < 1e-10f)
            tangent = Math.Abs(normal.Z) < 0.9f ? Vector3.Cross(Vector3.UnitZ, normal) : Vector3.UnitX;
        var binormal = Vector3.Normalize(tangent);

        var id = prim.Id;
        return new PickHit(id, id.LocalId, prim.FullId, rootLocalId == 0 ? id.LocalId : rootLocalId, bestFace, st, st,
            ray.Origin + ray.Direction * bestT, normal, binormal, bestT * ray.Direction.Length());
    }

    /// <summary>The nearest of the world's prims (not attachments), avatars (not <paramref name="ignoreAgent"/>) and the ground.</summary>
    public static PickResult Pick(WorldState world, PickRay ray, Func<float, float, float>? ground, float maxDistance = 300f, Guid? ignoreAgent = null, PickGeometryCache? cache = null,
        Func<ObjectState, Matrix4x4?>? attachmentFrame = null)
    {
        cache ??= new PickGeometryCache();
        float dirLength = ray.Direction.Length();
        if (!(dirLength > 0f))
            return new(PickKind.None, null, null, default, 0f);
        var snap = world.Snapshot();
        var byLocal = snap.Objects.ToDictionary(o => o.Key, o => o.Value);
        PickHit? bestHit = null;
        foreach (var o in byLocal.Values)
        {
            if (!TryPose(o, byLocal, 0, attachmentFrame, out var pos, out var rot, out uint root))
                continue;
            var hit = PickPrim(o, pos, rot, ray, root, cache);
            if (hit is not null && hit.Distance <= maxDistance && (bestHit is null || hit.Distance < bestHit.Distance))
                bestHit = hit;
        }
        AvatarState? bestAvatar = null;
        float avatarDistance = float.MaxValue;
        foreach (var a in snap.Avatars.Values)
        {
            if (ignoreAgent is { } me && a.AgentId == me)
                continue;
            var half = new Vector3(AvatarHalfWidth, AvatarHalfWidth, Math.Max(0.5f, a.Scale.Z) / 2f);
            if (SlabHit(ray.Origin - a.Position, ray.Direction, -half, half, out float t) && t * dirLength <= maxDistance && t * dirLength < avatarDistance)
            {
                bestAvatar = a;
                avatarDistance = t * dirLength;
            }
        }
        float objectDistance = bestHit?.Distance ?? float.MaxValue;
        float nearest = Math.Min(objectDistance, avatarDistance);
        if (ground is not null && Ground(ray, ground, Math.Min(maxDistance, nearest), out var point, out float groundDistance))
            return new(PickKind.Land, null, null, point, groundDistance);
        if (bestHit is not null && objectDistance <= avatarDistance)
            return new(PickKind.Object, bestHit, null, bestHit.Position, bestHit.Distance);
        if (bestAvatar is not null)
            return new(PickKind.Avatar, null, bestAvatar, ray.Origin + ray.Direction * (avatarDistance / dirLength), avatarDistance);
        return new(PickKind.None, null, null, default, 0f);
    }

    /// <summary>A prim's place in the region: a linked child sits in its parent's frame. False for an attachment, a child of one, or a prim whose parent is not known.</summary>
    public static bool WorldPose(WorldState world, ObjectState o, out Vector3 position, out Quaternion rotation) =>
        WorldPose(world, o, 0, out position, out rotation);

    private static bool WorldPose(WorldState world, ObjectState o, int depth, out Vector3 position, out Quaternion rotation)
    {
        position = o.Position; rotation = o.Rotation;
        if (o.AttachmentPoint is not null || depth > 8)
            return false;
        if (o.ParentLocalId == 0)
            return true;
        if (world.Object(new ObjectId(o.Id.Epoch, o.ParentLocalId)) is not { } parent || !WorldPose(world, parent, depth + 1, out var pp, out var pr))
            return false;
        position = pp + Vector3.Transform(o.Position, pr);
        rotation = pr * o.Rotation;
        return true;
    }

    /// <summary>A prim's pose in the region: a linked child sits in its parent's frame. A root attachment has a pose only when
    /// <paramref name="attachmentFrame"/> gives the frame of its attachment point (the object's own offset sits in that frame); otherwise it,
    /// and its children, have none.</summary>
    private static bool TryPose(ObjectState o, Dictionary<uint, ObjectState> byLocal, int depth, Func<ObjectState, Matrix4x4?>? attachmentFrame,
        out Vector3 position, out Quaternion rotation, out uint root)
    {
        position = o.Position; rotation = o.Rotation; root = o.Id.LocalId;
        if (depth > 8)
            return false;
        if (o.AttachmentPoint is not null)
        {
            if (attachmentFrame?.Invoke(o) is not { } frame)
                return false;
            var world = Viewer.Core.Avatars.AttachmentPlacement.ObjectInPoint(o.Position, o.Rotation) * frame;
            if (!Matrix4x4.Decompose(world, out _, out rotation, out position))
                return false;
            return true;
        }
        if (o.ParentLocalId == 0)
            return true;
        if (!byLocal.TryGetValue(o.ParentLocalId, out var parent) || !TryPose(parent, byLocal, depth + 1, attachmentFrame, out var pp, out var pr, out root))
            return false;   // its parent is an avatar, or not here yet
        position = pp + Vector3.Transform(o.Position, pr);
        rotation = pr * o.Rotation;
        return true;
    }

    private static bool Ground(PickRay ray, Func<float, float, float> ground, float limit, out Vector3 point, out float distance)
    {
        point = default; distance = 0f;
        var dir = Vector3.Normalize(ray.Direction);
        if (ray.Origin.Z < ground(ray.Origin.X, ray.Origin.Y))
            return false;
        float prev = 0f;
        for (float s = 1f; ; s += 1f)
        {
            float at = Math.Min(s, limit);
            var p = ray.Origin + dir * at;
            if (p.Z < ground(p.X, p.Y))
            {
                float lo = prev, hi = at;
                for (int i = 0; i < 24; i++)
                {
                    float mid = (lo + hi) / 2f;
                    var m = ray.Origin + dir * mid;
                    if (m.Z < ground(m.X, m.Y)) hi = mid; else lo = mid;
                }
                distance = hi;
                point = ray.Origin + dir * hi;
                return true;
            }
            prev = at;
            if (at >= limit)
                return false;
        }
    }

    private static Vector3 At(float[] a, int i) => new(a[i * 3], a[i * 3 + 1], a[i * 3 + 2]);
    private static Vector3 Normal(float[] a, int i) => a.Length >= (i + 1) * 3 ? At(a, i) : default;
    private static Vector2 Uv(float[] a, int i) => a.Length >= (i + 1) * 2 ? new(a[i * 2], a[i * 2 + 1]) : default;

    /// <summary>Ray against a box: the entry parameter along the direction (0 when the origin is inside).</summary>
    private static bool SlabHit(Vector3 o, Vector3 d, Vector3 min, Vector3 max, out float t)
    {
        float tmin = 0f, tmax = float.MaxValue;
        for (int axis = 0; axis < 3; axis++)
        {
            float oa = axis == 0 ? o.X : axis == 1 ? o.Y : o.Z, da = axis == 0 ? d.X : axis == 1 ? d.Y : d.Z;
            float lo = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z, hi = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;
            if (Math.Abs(da) < 1e-12f)
            {
                if (oa < lo || oa > hi) { t = 0; return false; }
                continue;
            }
            float t1 = (lo - oa) / da, t2 = (hi - oa) / da;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = Math.Max(tmin, t1);
            tmax = Math.Min(tmax, t2);
            if (tmin > tmax) { t = 0; return false; }
        }
        t = tmin;
        return true;
    }

    /// <summary>Moller-Trumbore, double sided: the ray parameter and the barycentric weights of the second and third corners.</summary>
    private static bool Triangle(Vector3 o, Vector3 d, Vector3 p0, Vector3 p1, Vector3 p2, out float t, out float u, out float v)
    {
        t = u = v = 0f;
        var e1 = p1 - p0; var e2 = p2 - p0;
        var pv = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, pv);
        if (Math.Abs(det) < 1e-12f)
            return false;
        float inv = 1f / det;
        var tv = o - p0;
        u = Vector3.Dot(tv, pv) * inv;
        if (u < 0f || u > 1f) return false;
        var qv = Vector3.Cross(tv, e1);
        v = Vector3.Dot(d, qv) * inv;
        if (v < 0f || u + v > 1f) return false;
        t = Vector3.Dot(e2, qv) * inv;
        return t > 1e-6f;
    }
}
