// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.Avatars;

/// <summary>What an outfit is made of. Same options, same skeleton, same result, byte for byte.</summary>
/// <param name="MeshCount">Rigged mesh objects in the outfit.</param>
/// <param name="Seed">The only source of randomness.</param>
/// <param name="MedianVertices">The middle of the vertex count of one mesh object (counts follow a log-normal spread).</param>
/// <param name="BindJitter">How far, in metres, a mesh's own idea of a joint's rest position may differ from the skeleton's
/// (the rotation jitter is 30 times this, in degrees); it makes every mesh's inverse bind matrices its own.</param>
/// <param name="MedianJoints">The middle of the length of one mesh's joint list.</param>
public sealed record OutfitOptions(int MeshCount, int Seed, int MedianVertices = 3500, float BindJitter = 0.015f, int MedianJoints = 45);

/// <summary>
/// Makes avatars shaped like commercial outfits: a number of rigged mesh objects, each a sleeve of faces wrapped round a
/// chain of skeleton joints, with its own joint list drawn from the whole skeleton (the chain's joints plus unused ones, as
/// real lists carry), up to four weights a vertex, its own inverse bind matrices, between one and eight faces, some of them
/// masked or blended. Geometry is a swept ring grid; no outfit looks like clothes, but the counts that decide skinning
/// cost (joints a list, influences, vertices, faces) follow what outfits have.
/// </summary>
public static class OutfitGenerator
{
    public static Outfit Generate(CharacterSkeleton skeleton, OutfitOptions options)
    {
        if (options.MeshCount < 1) throw new ArgumentOutOfRangeException(nameof(options), "an outfit needs a mesh");
        var rnd = new Random(options.Seed);
        var bones = skeleton.Joints.Where(j => j.Kind == JointKind.Bone).Select(j => j.Index).ToArray();
        var meshes = new OutfitMesh[options.MeshCount];
        for (int m = 0; m < meshes.Length; m++) meshes[m] = Mesh(skeleton, bones, options, rnd, m);
        return new Outfit { Seed = options.Seed, Meshes = meshes };
    }

    private static OutfitMesh Mesh(CharacterSkeleton sk, int[] bones, OutfitOptions o, Random rnd, int number)
    {
        var chain = Chain(sk, bones, rnd);
        // the joint list: the chain, the chain's neighbours that carry small weights, then unused joints up to the length
        int target = Math.Clamp((int)Math.Round(o.MedianJoints * Math.Exp(Normal(rnd) * 0.55)), 6, OutfitMesh.MaxJoints);
        var listed = new SortedSet<int>(chain);
        foreach (int j in chain.ToArray())
        {
            if (sk.Joints[j].Parent >= 0 && sk.Joints[sk.Joints[j].Parent].Kind == JointKind.Bone) listed.Add(sk.Joints[j].Parent);
        }
        int guard = 0;
        while (listed.Count < Math.Max(target, chain.Length) && guard++ < 10_000) listed.Add(bones[rnd.Next(bones.Length)]);
        while (listed.Count > OutfitMesh.MaxJoints) listed.Remove(listed.First(j => !chain.Contains(j)));
        var list = listed.ToArray();
        var slot = new Dictionary<int, int>();
        for (int i = 0; i < list.Length; i++) slot[list[i]] = i;
        var unused = list.Where(j => !chain.Contains(j)).ToArray();

        // each mesh's own rest pose for its joints: the inverse binds
        var inverse = new Matrix4x4[list.Length];
        for (int i = 0; i < list.Length; i++)
        {
            var axis = Vector3.Normalize(new Vector3(NormalF(rnd), NormalF(rnd), NormalF(rnd)) + new Vector3(0.001f));
            var rest = Matrix4x4.CreateFromAxisAngle(axis, (float)(Normal(rnd) * o.BindJitter * 30 * Math.PI / 180))
                * Matrix4x4.CreateTranslation(new Vector3(NormalF(rnd), NormalF(rnd), NormalF(rnd)) * o.BindJitter)
                * sk.DefaultWorld[list[i]];
            if (!Matrix4x4.Invert(rest, out inverse[i])) throw new InvalidOperationException("a rest pose cannot be inverted");
        }

        // geometry: rows along the chain, a ring of columns round it
        int vertices = Math.Clamp((int)Math.Round(o.MedianVertices * Math.Exp(Normal(rnd) * 0.8)), 200, 30_000);
        int faces = Math.Clamp(1 + (int)Math.Round(Math.Abs(Normal(rnd)) * 3.2), 1, 8);
        int cols = Math.Clamp((int)Math.Sqrt(vertices / 2.0), 12, 64);
        int rows = Math.Max(faces + 1, vertices / (cols + 1));
        var points = chain.Select(j => sk.DefaultWorld[j].Translation).ToList();
        // a sleeve reaches half a segment past the last joint, so a single joint still has a length
        var last = points[^1];
        var dir = points.Count > 1 ? Vector3.Normalize(points[^1] - points[^2] + new Vector3(0, 0, 0.0001f)) : Vector3.UnitZ;
        points.Add(last + dir * Math.Max(0.12f, points.Count > 1 ? Vector3.Distance(points[^2], points[^1]) * 0.5f : 0.12f));
        var seg = new float[points.Count - 1];
        float total = 0;
        for (int i = 0; i < seg.Length; i++) total += seg[i] = Math.Max(Vector3.Distance(points[i], points[i + 1]), 0.02f);
        float radius = Math.Clamp(total / points.Count * 0.5f, 0.04f, 0.14f) * (0.9f + 0.3f * (float)rnd.NextDouble());
        float phase = (float)(rnd.NextDouble() * Math.PI * 2);

        var influence = new int[4];
        var weight = new float[4];
        var pos = new Vector3[rows * (cols + 1)];
        var nrm = new Vector3[pos.Length];
        var uv = new Vector2[pos.Length];
        var jn = new byte[pos.Length * 4];
        var wt = new float[pos.Length * 4];
        for (int r = 0; r < rows; r++)
        {
            float along = total * r / (rows - 1);
            int k = 0;
            float acc = 0;
            while (k < seg.Length - 1 && acc + seg[k] < along) acc += seg[k++];
            float f = Math.Clamp((along - acc) / seg[k], 0f, 1f);
            var centre = Vector3.Lerp(points[k], points[k + 1], f);
            var t = Vector3.Normalize(points[k + 1] - points[k] + new Vector3(0, 0, 0.0001f));
            var refAxis = MathF.Abs(t.Z) > 0.9f ? Vector3.UnitX : Vector3.UnitZ;
            var n1 = Vector3.Normalize(Vector3.Cross(t, refAxis));
            var n2 = Vector3.Cross(t, n1);
            float rr = radius * (0.85f + 0.3f * MathF.Sin(MathF.PI * r / (rows - 1)));
            float g = k + f;   // the position along the chain's joints
            Influences(chain, g, rnd, unused, influence, weight);
            for (int c = 0; c <= cols; c++)
            {
                float a = phase + c * 2 * MathF.PI / cols;
                var ring = MathF.Cos(a) * n1 + MathF.Sin(a) * n2;
                int v = r * (cols + 1) + c;
                pos[v] = centre + ring * rr;
                nrm[v] = ring;
                uv[v] = new Vector2((float)c / cols, (float)r / (rows - 1));
                for (int q = 0; q < 4; q++) { jn[v * 4 + q] = (byte)slot[influence[q]]; wt[v * 4 + q] = weight[q]; }
            }
        }

        // faces: bands of rows, each with its own copy of the vertices it uses
        var outFaces = new OutfitFace[faces];
        var hue = (float)rnd.NextDouble();
        for (int fc = 0; fc < faces; fc++)
        {
            int r0 = (rows - 1) * fc / faces, r1 = (rows - 1) * (fc + 1) / faces;   // quad rows r0..r1-1
            int nr = r1 - r0 + 1;
            var fp = new Vector3[nr * (cols + 1)];
            var fn = new Vector3[fp.Length];
            var fu = new Vector2[fp.Length];
            var fj = new byte[fp.Length * 4];
            var fw = new float[fp.Length * 4];
            Array.Copy(pos, r0 * (cols + 1), fp, 0, fp.Length);
            Array.Copy(nrm, r0 * (cols + 1), fn, 0, fp.Length);
            Array.Copy(uv, r0 * (cols + 1), fu, 0, fp.Length);
            Array.Copy(jn, r0 * (cols + 1) * 4, fj, 0, fj.Length);
            Array.Copy(wt, r0 * (cols + 1) * 4, fw, 0, fw.Length);
            var idx = new List<int>((nr - 1) * cols * 6);
            for (int r = 0; r < nr - 1; r++)
                for (int c = 0; c < cols; c++)
                {
                    int a = r * (cols + 1) + c, b = a + 1, d = a + cols + 1, e = d + 1;
                    idx.AddRange([a, d, b, b, d, e]);
                }
            // wind every triangle so its geometric normal agrees with the ring's outward normal
            for (int i = 0; i < idx.Count; i += 3)
            {
                var gn = Vector3.Cross(fp[idx[i + 1]] - fp[idx[i]], fp[idx[i + 2]] - fp[idx[i]]);
                if (Vector3.Dot(gn, fn[idx[i]] + fn[idx[i + 1]] + fn[idx[i + 2]]) < 0) (idx[i + 1], idx[i + 2]) = (idx[i + 2], idx[i + 1]);
            }
            double roll = rnd.NextDouble();
            var alpha = roll < 0.72 ? OutfitFaceAlpha.Opaque : roll < 0.86 ? OutfitFaceAlpha.Mask : OutfitFaceAlpha.Blend;
            outFaces[fc] = new OutfitFace
            {
                Positions = fp, Normals = fn, Uvs = fu, Indices = idx.ToArray(), Joints = fj, Weights = fw, Alpha = alpha,
                Tint = Hsv((hue + 0.07f * fc) % 1f, 0.45f, 0.85f),
            };
        }
        return new OutfitMesh { Name = $"mesh{number:D2}", JointList = list, InverseBind = inverse, Faces = outFaces };
    }

    /// <summary>A path down the skeleton from a joint with children, two to eight joints long.</summary>
    private static int[] Chain(CharacterSkeleton sk, int[] bones, Random rnd)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            int start = bones[rnd.Next(bones.Length)];
            var chain = new List<int> { start };
            int want = 2 + rnd.Next(7);
            while (chain.Count < want)
            {
                var kids = sk.Children[chain[^1]].Where(c => sk.Joints[c].Kind == JointKind.Bone).ToList();
                if (kids.Count == 0) break;
                chain.Add(kids[rnd.Next(kids.Count)]);
            }
            if (chain.Count < 2) continue;
            // a chain of joints all within a few centimetres would give a sleeve with no length
            if (Vector3.Distance(sk.DefaultWorld[chain[0]].Translation, sk.DefaultWorld[chain[^1]].Translation) < 0.08f) continue;
            return chain.ToArray();
        }
        return [bones[0], bones[Math.Min(1, bones.Length - 1)]];
    }

    /// <summary>The four joints and weights at position <paramref name="g"/> along the chain (the whole part is the joint
    /// below, the fraction the joint above), with a little weight on the neighbours either side and, for some vertices, on
    /// an unused joint of the list.</summary>
    private static void Influences(int[] chain, float g, Random rnd, int[] unused, int[] joint, float[] weight)
    {
        int k = Math.Min((int)g, chain.Length - 1);
        float f = k >= chain.Length - 1 ? 0f : g - k;
        float edge = 0.04f + 0.08f * (float)rnd.NextDouble();
        joint[0] = chain[k]; weight[0] = 1f - f;
        joint[1] = chain[Math.Min(k + 1, chain.Length - 1)]; weight[1] = f;
        joint[2] = k >= 1 ? chain[k - 1] : chain[k]; weight[2] = k >= 1 ? edge : 0f;
        joint[3] = k + 2 < chain.Length ? chain[k + 2] : chain[Math.Min(k + 1, chain.Length - 1)]; weight[3] = k + 2 < chain.Length ? edge : 0f;
        if (unused.Length > 0 && rnd.NextDouble() < 0.15) { joint[3] = unused[rnd.Next(unused.Length)]; weight[3] = 0.02f; }
        float sum = weight[0] + weight[1] + weight[2] + weight[3];
        for (int i = 0; i < 4; i++) weight[i] /= sum;
        // a slot with no weight keeps the first joint so no entry is out of range
        for (int i = 0; i < 4; i++) if (weight[i] == 0f) joint[i] = joint[0];
    }

    private static float NormalF(Random rnd) => (float)Normal(rnd);

    private static double Normal(Random rnd)
    {
        double u1 = 1.0 - rnd.NextDouble(), u2 = rnd.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static Vector3 Hsv(float h, float s, float v)
    {
        float c = v * s, x = c * (1 - MathF.Abs(h * 6 % 2 - 1)), m = v - c;
        var (r, g, b) = (int)(h * 6) switch { 0 => (c, x, 0f), 1 => (x, c, 0f), 2 => (0f, c, x), 3 => (0f, x, c), 4 => (x, 0f, c), _ => (c, 0f, x) };
        return new Vector3(r + m, g + m, b + m);
    }
}
