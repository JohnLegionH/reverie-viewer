// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Residency;

namespace Viewer.Core.World;

/// <summary>
/// Chooses the objects the glow picture draws as possible hiders of a glowing object (the rule is <see cref="GlowOccluders.CanHide"/>),
/// without testing every glowing object against every object. Each eye position, it works out every object's direction from the eye
/// and its angular radius once, files each glowing object in every cell of a coarse grid over the directions that its circle
/// touches, and tests an object only against the glowing objects filed in the cells its own circle touches: two circles that
/// overlap share a cell. A pair is decided by comparing cosines, so no trigonometry runs per pair; a pair that lands within rounding
/// of the threshold is decided by <see cref="GlowOccluders.CanHide"/> itself, so the chosen set is always the one the plain rule gives.
/// The scratch arrays are kept between calls, so a frame that reruns the choice allocates nothing once they are big enough.
/// </summary>
public sealed class GlowChooser
{
    // the grid: the unit sphere's directions are filed by their x, y and z in N slices each (cells that no direction reaches stay empty)
    private const int N = 16;
    private const int Cells = N * N * N;
    // a circle wider than this (radians) would touch too many cells; it is tested against every object (or every glowing object) instead
    private const float WideAngle = 0.25f;
    // a cosine this close to the threshold is decided by the plain rule, which rounds differently
    private const float CosBand = 1e-5f;

    private struct Disc
    {
        public Vector3 Dir;                 // unit direction from the eye
        public float Dist, Radius;          // distance from the eye to the centre, and the sphere's radius
        public float Alpha;                 // the angle the sphere covers seen from the eye
        public float SinPad, CosPad;        // sine and cosine of Alpha plus the plain rule's extra angle
        public float SinRaw, CosRaw;        // sine and cosine of Alpha
        public bool EyeInside;              // the eye is in the sphere: it fills every direction
        public bool Wide;                   // Alpha too wide for the grid
    }

    private Disc[] _targets = new Disc[64], _cands = new Disc[256];
    private readonly int[] _head = new int[Cells];
    private int[] _entryNext = new int[512], _entryTarget = new int[512];
    private int[] _seen = new int[64], _wideTargets = new int[16];

    /// <summary>
    /// Sets <c>selected[i]</c> to whether candidate <c>i</c> can hide any of the targets, as <see cref="GlowOccluders.CanHide"/> says of each pair.
    /// Returns the number of target-and-candidate pairs it tested one by one.
    /// </summary>
    public int Select(Vector3 eye, IReadOnlyList<Bounds> targets, IReadOnlyList<Bounds> candidates, bool[] selected)
    {
        int nt = targets.Count, nc = candidates.Count;
        if (nt == 0)
        {
            for (int i = 0; i < nc; i++) selected[i] = false;
            return 0;
        }
        if (_targets.Length < nt) { _targets = new Disc[Math.Max(nt, _targets.Length * 2)]; _seen = new int[_targets.Length]; }
        if (_cands.Length < nc) _cands = new Disc[Math.Max(nc, _cands.Length * 2)];
        Array.Fill(_head, -1);
        Array.Clear(_seen, 0, nt);
        int entries = 0, wides = 0;
        bool anyInside = false;
        float insideFar = float.NegativeInfinity;   // the farthest point of any glowing sphere the eye is inside

        for (int t = 0; t < nt; t++)
        {
            ref var d = ref _targets[t];
            Fill(ref d, eye, targets[t]);
            if (d.EyeInside) { anyInside = true; insideFar = Math.Max(insideFar, d.Dist + d.Radius); continue; }
            if (d.Wide)
            {
                if (_wideTargets.Length <= wides) Array.Resize(ref _wideTargets, wides * 2);
                _wideTargets[wides++] = t;
                continue;
            }
            Box(d.Dir, d.Alpha + AngleMargin, out int x0, out int x1, out int y0, out int y1, out int z0, out int z1);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++)
                    {
                        if (entries == _entryNext.Length) { Array.Resize(ref _entryNext, entries * 2); Array.Resize(ref _entryTarget, entries * 2); }
                        int cell = (x * N + y) * N + z;
                        _entryNext[entries] = _head[cell];
                        _entryTarget[entries] = t;
                        _head[cell] = entries++;
                    }
        }

        int pairs = 0;
        for (int i = 0; i < nc; i++)
        {
            ref var c = ref _cands[i];
            Fill(ref c, eye, candidates[i]);
            bool hides = false;
            if (c.EyeInside) hides = true;                                                    // the eye is inside it
            else if (anyInside && c.Dist - c.Radius < insideFar) hides = true;               // a glowing sphere around the eye reaches past its nearest point
            else if (c.Wide)
            {
                for (int t = 0; t < nt && !hides; t++)
                {
                    if (_targets[t].EyeInside) continue;
                    pairs++;
                    hides = Hides(eye, in c, in _targets[t], targets[t], candidates[i]);
                }
            }
            else
            {
                for (int w = 0; w < wides && !hides; w++)
                {
                    int t = _wideTargets[w];
                    pairs++;
                    hides = Hides(eye, in c, in _targets[t], targets[t], candidates[i]);
                }
                if (!hides)
                {
                    Box(c.Dir, c.Alpha + AngleMargin, out int x0, out int x1, out int y0, out int y1, out int z0, out int z1);
                    for (int x = x0; x <= x1 && !hides; x++)
                        for (int y = y0; y <= y1 && !hides; y++)
                            for (int z = z0; z <= z1 && !hides; z++)
                                for (int e = _head[(x * N + y) * N + z]; e >= 0 && !hides; e = _entryNext[e])
                                {
                                    int t = _entryTarget[e];
                                    if (_seen[t] == i + 1) continue;   // already tested through another cell
                                    _seen[t] = i + 1;
                                    pairs++;
                                    hides = Hides(eye, in c, in _targets[t], targets[t], candidates[i]);
                                }
                }
            }
            selected[i] = hides;
        }
        return pairs;
    }

    private const float AngleMargin = GlowOccluders.AngleMargin;

    private static void Fill(ref Disc d, Vector3 eye, Bounds b)
    {
        var to = b.Centre - eye;
        float dist = to.Length();
        d.Dist = dist;
        d.Radius = b.Radius;
        d.EyeInside = dist <= b.Radius;
        if (d.EyeInside) { d.Wide = true; return; }
        d.Dir = to / dist;
        float a = MathF.Asin(Math.Min(1f, b.Radius / dist));
        d.Alpha = a;
        d.SinRaw = MathF.Sin(a);
        d.CosRaw = MathF.Cos(a);
        d.SinPad = MathF.Sin(a + AngleMargin);
        d.CosPad = MathF.Cos(a + AngleMargin);
        d.Wide = a + AngleMargin > WideAngle;
    }

    /// <summary>The cells a circle of angular radius <paramref name="alpha"/> about the unit vector <paramref name="dir"/> can touch: every point of it is within the chord's length of <paramref name="dir"/> along each axis.</summary>
    private static void Box(Vector3 dir, float alpha, out int x0, out int x1, out int y0, out int y1, out int z0, out int z1)
    {
        float rho = 2f * MathF.Sin(Math.Min(alpha, MathF.PI) * 0.5f) + 1e-4f;
        x0 = Cell(dir.X - rho); x1 = Cell(dir.X + rho);
        y0 = Cell(dir.Y - rho); y1 = Cell(dir.Y + rho);
        z0 = Cell(dir.Z - rho); z1 = Cell(dir.Z + rho);
    }

    private static int Cell(float v) => Math.Clamp((int)MathF.Floor((v + 1f) * (N * 0.5f)), 0, N - 1);

    /// <summary>The plain rule for one pair of spheres neither of which the eye is inside, with the angle compared as a cosine.</summary>
    private static bool Hides(Vector3 eye, in Disc c, in Disc t, Bounds target, Bounds candidate)
    {
        if (c.Dist - c.Radius >= t.Dist + t.Radius) return false;           // all of it is farther than all of the target
        float reach = c.Alpha + t.Alpha + AngleMargin;
        if (reach >= MathF.PI - 1e-3f) return GlowOccluders.CanHide(eye, target, candidate);   // wider than any angle between directions: rare
        float cos = Vector3.Dot(c.Dir, t.Dir);
        float threshold = c.CosPad * t.CosRaw - c.SinPad * t.SinRaw;       // cos(candidate angle + extra + target angle)
        if (MathF.Abs(cos - threshold) < CosBand) return GlowOccluders.CanHide(eye, target, candidate);
        return cos > threshold;
    }
}
