// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.World;

namespace Viewer.Shell.Sound;

/// <summary>A prim as a box for footsteps: its centre and rotation in the region's frame, its size, and its material (the protocol's numbers).</summary>
public readonly record struct StepBox(Vector3 Center, Quaternion Rotation, Vector3 Size, int Material);

/// <summary>What a foot stands on: the height of the ground under it, whether that is the land, and the material of the object when it is not.</summary>
public readonly record struct StepGroundHit(float Ground, bool OnLand, int Material);

/// <summary>A foot that came down: which, where (region frame), on land or on an object of a material.</summary>
public sealed record StepHit(Foot Foot, Vector3 At, bool OnLand, int Material);

/// <summary>
/// The ground under a foot as the official viewer finds it for footsteps (llvoavatar.cpp, resolveHeightGlobal): a line from half a metre
/// above the foot to half a metre below it; the first object it meets gives the ground and the material, and the land does when it meets
/// none. Objects are taken as their prim boxes (the shape's own surface is not traced), which is exact for boxes and close for the floors,
/// decks and steps people walk on.
/// </summary>
public static class StepGround
{
    /// <summary>How far above and below the foot the line reaches.</summary>
    public const float Reach = 0.5f;

    /// <summary>The ground under <paramref name="foot"/>: the highest prim top the line meets, else the land (null when the land is not
    /// known there and no prim is met).</summary>
    public static StepGroundHit? Under(Vector3 foot, float? land, IReadOnlyList<StepBox> boxes)
    {
        float top = foot.Z + Reach, bottom = foot.Z - Reach;
        StepGroundHit? best = null;
        foreach (var b in boxes)
        {
            if (EntryHeight(b, foot.X, foot.Y, bottom, top) is not float z)
                continue;
            if (best is null || z > best.Value.Ground)
                best = new StepGroundHit(z, false, b.Material);
        }
        if (best is { } hit && (land is not float l || hit.Ground >= l))
            return hit;
        return land is float g ? new StepGroundHit(g, true, 0) : null;
    }

    // where a vertical line at (x, y), going down from top to bottom, first enters the box; null when it misses it in that stretch
    private static float? EntryHeight(StepBox b, float x, float y, float bottom, float top)
    {
        var inverse = Quaternion.Inverse(Quaternion.Normalize(b.Rotation));
        var start = Vector3.Transform(new Vector3(x, y, top) - b.Center, inverse);
        var dir = Vector3.Transform(new Vector3(0, 0, -(top - bottom)), inverse);
        var half = b.Size / 2;
        float t0 = 0, t1 = 1;
        for (int axis = 0; axis < 3; axis++)
        {
            float s = start[axis], d = dir[axis], h = half[axis];
            if (MathF.Abs(d) < 1e-7f)
            {
                if (s < -h || s > h) return null;
                continue;
            }
            float a = (-h - s) / d, c = (h - s) / d;
            if (a > c) (a, c) = (c, a);
            t0 = MathF.Max(t0, a);
            t1 = MathF.Min(t1, c);
            if (t0 > t1) return null;
        }
        return top - t0 * (top - bottom);
    }

    /// <summary>The prims within <paramref name="radius"/> of <paramref name="near"/> as boxes in the region's frame: roots by their own place, a
    /// linked child by its root's (the protocol states a child relative to its root). Attachments, and prims seated on an avatar, are left
    /// out: nobody stands on them.</summary>
    public static IReadOnlyList<StepBox> Boxes(IReadOnlyCollection<ObjectState> objects, Vector3 near, float radius)
    {
        var byLocal = new Dictionary<uint, ObjectState>();
        foreach (var o in objects)
            byLocal.TryAdd(o.Id.LocalId, o);
        var boxes = new List<StepBox>();
        foreach (var o in objects)
        {
            if (o.AttachmentPoint is not null || o.WearerAgentId is not null)
                continue;
            Vector3 center;
            Quaternion rotation;
            if (o.ParentLocalId == 0)
            {
                center = o.Position;
                rotation = o.Rotation;
            }
            else if (byLocal.TryGetValue(o.ParentLocalId, out var root) && root.ParentLocalId == 0 && root.AttachmentPoint is null && root.WearerAgentId is null)
            {
                center = root.Position + Vector3.Transform(o.Position, root.Rotation);
                rotation = root.Rotation * o.Rotation;
            }
            else
                continue;
            float reach = radius + o.Scale.Length() / 2;
            if (Vector3.DistanceSquared(center, near) > reach * reach)
                continue;
            boxes.Add(new StepBox(center, rotation, o.Scale, o.Material));
        }
        return boxes;
    }
}

/// <summary>
/// Footsteps for one avatar, frame by frame, the official way (llvoavatar.cpp updateFootstepSounds): each foot joint where the official
/// viewer puts it (the avatar's stated place less half its height, plus the joint's height over the avatar root in the pose; never where
/// a drawing puts the body's lowest point), its height over the ground under it (<see cref="StepGround"/>), and a step when a foot comes
/// down to 5 cm or less (<see cref="FootstepDetector"/>). The step plays the land step on the land, or the material's step on an object.
/// </summary>
public sealed class FootstepSampler
{
    private readonly FootstepDetector _detector = new();

    /// <summary>The share of the avatar's own height that a gap between its soles and the ground may reach and still count as resting
    /// (0.29 m for a 1.9 m avatar); a larger gap is a jump or a fall.</summary>
    public const float RestingGapShare = 0.15f;

    /// <summary>The height taken for an avatar whose own height is not known.</summary>
    public const float DefaultBodyHeight = 1.9f;

    /// <summary>The largest resting gap for an avatar <paramref name="bodyHeight"/> tall (0 or less: not known).</summary>
    public static float RestingGapLimit(float bodyHeight) => RestingGapShare * (bodyHeight > 0.1f && float.IsFinite(bodyHeight) ? bodyHeight : DefaultBodyHeight);

    /// <summary>The gap taken off the feet in the last frame that walked (0 when none).</summary>
    public float LastGap { get; private set; }

    /// <summary>One frame: the feet (region frame, placed as above), the avatar's soles (its place less half its height), whether a walk, run or landing plays, whether the avatar sits, the land's
    /// height at a point, the prims near the avatar, and the avatar's own height (its shape's). Returns the foot that came down, or null.</summary>
    public StepHit? Update(Vector3 left, Vector3 right, Vector3 soles, bool stepAnimationPlaying, bool sitting, Func<float, float, float?> land, IReadOnlyList<StepBox> boxes, float bodyHeight = 0f)
    {
        if (sitting || !stepAnimationPlaying)
        {
            _detector.Update(1, 1, stepAnimationPlaying, sitting);
            return null;
        }
        if (StepGround.Under(left, land(left.X, left.Y), boxes) is not { } lg || StepGround.Under(right, land(right.X, right.Y), boxes) is not { } rg)
            return null;
        // the gap the grid rests the avatar at over the ground under it: an OpenSim region makes the avatar's physical box 0.2 m taller
        // than the height it keeps for it and rests that box on the ground, so the stated place is about 0.1 m higher than the official
        // rule expects and no foot would come within its 5 cm. The gap is taken off the feet so a walk steps as it does where the avatar
        // rests on the ground. This goes beyond the official rule; the gap allowed is a share of the avatar's own height, and a larger
        // gap is a jump or a fall.
        float gap = StepGround.Under(soles, land(soles.X, soles.Y), boxes) is { } under ? soles.Z - under.Ground : 0f;
        if (gap < 0f || gap > RestingGapLimit(bodyHeight))
            gap = 0f;
        LastGap = gap;
        // the official viewer keeps the heights at no less than 20 cm under the ground
        var foot = _detector.Update(MathF.Max(-0.2f, left.Z - lg.Ground - gap), MathF.Max(-0.2f, right.Z - rg.Ground - gap), true, false);
        return foot switch
        {
            Foot.Left => new StepHit(Foot.Left, left, lg.OnLand, lg.Material),
            Foot.Right => new StepHit(Foot.Right, right, rg.OnLand, rg.Material),
            _ => null,
        };
    }
}

/// <summary>
/// One walk of the user's own avatar, summed up for the viewer log when it ends: how many frames walked, each foot's lowest and highest
/// height over the ground under it, the resting gap taken off, the avatar's own height and the region's height for it, the steps, and
/// whether the ground was land. One line per walk, so a person's report of silent steps can be read from the log.
/// </summary>
public sealed class WalkSummary
{
    private int _frames, _steps, _onLand;
    private float _leftLow = float.PositiveInfinity, _leftHigh = float.NegativeInfinity, _rightLow = float.PositiveInfinity, _rightHigh = float.NegativeInfinity;
    private float _gap, _body, _region;

    /// <summary>One walking frame of the own avatar.</summary>
    public void Frame(float left, float right, float gap, float bodyHeight, float regionHeight, bool stepped, bool onLand)
    {
        _frames++;
        if (float.IsFinite(left)) { _leftLow = MathF.Min(_leftLow, left); _leftHigh = MathF.Max(_leftHigh, left); }
        if (float.IsFinite(right)) { _rightLow = MathF.Min(_rightLow, right); _rightHigh = MathF.Max(_rightHigh, right); }
        _gap = gap; _body = bodyHeight; _region = regionHeight;
        if (stepped) _steps++;
        if (onLand) _onLand++;
    }

    /// <summary>The walk ended: its summary as log fields (null when no frame walked), and a fresh start.</summary>
    public (string Key, object? Value)[]? End()
    {
        if (_frames == 0) return null;
        static double R(float v) => float.IsFinite(v) ? Math.Round(v, 3) : double.NaN;
        var fields = new (string, object?)[]
        {
            ("frames", _frames), ("steps", _steps), ("left_low", R(_leftLow)), ("left_high", R(_leftHigh)), ("right_low", R(_rightLow)),
            ("right_high", R(_rightHigh)), ("gap", R(_gap)), ("body_height", R(_body)), ("region_height", R(_region)), ("on_land_frames", _onLand),
        };
        _frames = _steps = _onLand = 0;
        _leftLow = _rightLow = float.PositiveInfinity; _leftHigh = _rightHigh = float.NegativeInfinity;
        return fields;
    }
}
