// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Viewer.Shell;

/// <summary>
/// Reads heights from a region's terrain patches (the core's TerrainStore through <c>WorldState.TryGetPatch</c>): patch
/// (px, py) is a row-major <c>size x size</c> array, index <c>y * size + x</c>, in metres. Any region size (256 m legacy,
/// var regions up to 1024 m and beyond, non-square).
/// </summary>
public delegate bool PatchSource(int px, int py, out float[] heights);

/// <summary>One mesh chunk of a region's terrain, in engine space (x east, y up, z = -north), ready to upload.</summary>
/// <param name="Composition">: per-vertex terrain composition value (0..3) when the region has terrain settings.</param>
/// <param name="Jitter">: per-vertex jitter (0..1), the second coordinate of the blend ramp, with the composition value.</param>
public sealed record TerrainChunk(int ChunkX, int ChunkY, float[] Positions, float[] Normals, float[] Colors, int[] Indices, float[]? Composition = null, float[]? Jitter = null)
{
    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>
/// Builds terrain chunks from patches. A chunk covers <see cref="ChunkPatches"/> x <see cref="ChunkPatches"/> patches
/// (64 m of 16 m patches), so a 1024 m region is 256 chunks of at most 65 x 65 vertices; a patch that has not arrived
/// leaves a hole (its quads are skipped), never a spike. Edge vertices take their height from whichever neighbouring patch
/// is present, so chunks meet without cracks. Pure and deterministic: the goldens depend on it.
/// </summary>
public sealed class TerrainGrid(int sizeX, int sizeY, int patchSize, PatchSource patches)
{
    public const int ChunkPatches = 4;

    /// <summary>The region's terrain texturing settings (null: height-coloured ground as before).</summary>
    public Viewer.Core.World.RegionTerrain? Composition { get; init; }
    /// <summary>The region's global origin in metres (the composition noise is continuous across regions).</summary>
    public double OriginX { get; init; }
    public double OriginY { get; init; }

    /// <summary>Height of the ground just outside the region, at grid vertex (gx, gy) of this region's own frame (gx or gy below 0, or at
    /// least the size), from a region that lies there; null when none does. Edge vertices and normals read it before clamping to the
    /// region's own last cell, so two regions meet in one surface.</summary>
    public Func<int, int, float?>? OutsideHeight { get; init; }

    public int SizeX { get; } = sizeX;
    public int SizeY { get; } = sizeY;
    public int PatchSize { get; } = patchSize;
    public int PatchesX => (SizeX + PatchSize - 1) / PatchSize;
    public int PatchesY => (SizeY + PatchSize - 1) / PatchSize;
    public int ChunksX => (PatchesX + ChunkPatches - 1) / ChunkPatches;
    public int ChunksY => (PatchesY + ChunkPatches - 1) / ChunkPatches;

    /// <summary>The chunks whose build reads patch (px, py): its own and the ones across an edge or corner it touches.</summary>
    public IEnumerable<(int X, int Y)> ChunksReading(int px, int py)
    {
        // chunk cx reads patches cx*ChunkPatches-1 .. cx*ChunkPatches+ChunkPatches (its own and the ring), so patch px is read by chunks (px-1)/p .. (px+1)/p
        for (int cy = Math.Max(0, (py - 1) / ChunkPatches); cy <= Math.Min(ChunksY - 1, (py + 1) / ChunkPatches); cy++)
            for (int cx = Math.Max(0, (px - 1) / ChunkPatches); cx <= Math.Min(ChunksX - 1, (px + 1) / ChunkPatches); cx++)
                yield return (cx, cy);
    }

    /// <summary>The chunk holding patch (px, py).</summary>
    public static (int X, int Y) ChunkOf(int px, int py) => (px / ChunkPatches, py / ChunkPatches);

    private bool Patch(int px, int py, out float[] h)
    {
        h = null!;
        return px >= 0 && py >= 0 && px < PatchesX && py < PatchesY && patches(px, py, out h) && h.Length >= PatchSize * PatchSize;
    }

    /// <summary>Height at grid vertex (gx, gy) in metres, from the first present patch that touches it; null if none does.</summary>
    public float? Height(int gx, int gy)
    {
        if (OutsideHeight is not null && (gx < 0 || gy < 0 || gx >= SizeX || gy >= SizeY) && OutsideHeight(gx, gy) is { } outside)
            return outside;
        foreach (var (cx, cy) in new[] { (gx, gy), (gx - 1, gy), (gx, gy - 1), (gx - 1, gy - 1) })
        {
            int x = Math.Clamp(cx, 0, SizeX - 1), y = Math.Clamp(cy, 0, SizeY - 1);
            if (Patch(x / PatchSize, y / PatchSize, out var h))
                return h[(y % PatchSize) * PatchSize + x % PatchSize];
        }
        return null;
    }

    /// <summary>The main thread's managed thread id (the presenter sets it when made), and how many
    /// chunks were built (<see cref="Build"/>) and encoded (<see cref="TerrainSurface.Prepare"/>) on it since the process
    /// started: the viewer builds and encodes every chunk on a worker, so both stay 0 (--tr-terrainprep).</summary>
    public static int MainThreadId { get; set; } = -1;
    private static long s_builtOnMain;
    public static long BuiltOnMainThread => Interlocked.Read(ref s_builtOnMain);
    internal static void CountIfMain(ref long counter)
    {
        if (Environment.CurrentManagedThreadId == MainThreadId) Interlocked.Increment(ref counter);
    }

    /// <summary>
    /// An immutable snapshot of what chunk (cx, cy)'s build reads, taken on the main thread: copies
    /// of the present patches from one patch before the chunk to one after it on each axis (its 4 x 4 and the ring its edge
    /// heights and normals read: a 6 x 6 block at most), with the grid's size, composition and origin. The snapshot's own
    /// grid builds exactly what this grid would (the render harness checks it byte for byte).
    /// </summary>
    public TerrainChunkSnapshot Snapshot(int cx, int cy)
    {
        int px0 = Math.Max(0, cx * ChunkPatches - 1), py0 = Math.Max(0, cy * ChunkPatches - 1);
        int px1 = Math.Min(PatchesX - 1, cx * ChunkPatches + ChunkPatches), py1 = Math.Min(PatchesY - 1, cy * ChunkPatches + ChunkPatches);
        int w = Math.Max(0, px1 - px0 + 1), h = Math.Max(0, py1 - py0 + 1);
        var copies = new float[]?[w * h];
        for (int py = py0; py <= py1; py++)
            for (int px = px0; px <= px1; px++)
                if (Patch(px, py, out var src))
                    copies[(py - py0) * w + (px - px0)] = (float[])src.Clone();
        return new TerrainChunkSnapshot(cx, cy, SizeX, SizeY, PatchSize, Composition, OriginX, OriginY, px0, py0, w, h, copies, OutsideCells(cx, cy));
    }

    /// <summary>The heights outside the region that building chunk (cx, cy) reads (its vertices and the taps of their normals reach one
    /// step past the chunk), copied now so the worker never touches live data. Null when no source is set; empty for an inner chunk.</summary>
    private Dictionary<(int, int), float>? OutsideCells(int cx, int cy)
    {
        if (OutsideHeight is null)
            return null;
        int step = ChunkPatches * PatchSize;
        int x0 = cx * step, y0 = cy * step;
        int x1 = Math.Min(x0 + step, SizeX), y1 = Math.Min(y0 + step, SizeY);
        var cells = new Dictionary<(int, int), float>();
        for (int gy = y0 - 1; gy <= y1 + 1; gy++)
            for (int gx = x0 - 1; gx <= x1 + 1; gx++)
                if ((gx < 0 || gy < 0 || gx >= SizeX || gy >= SizeY) && OutsideHeight(gx, gy) is { } h)
                    cells[(gx, gy)] = h;
        return cells;
    }

    /// <summary>Builds chunk (cx, cy); null when none of its patches is present.</summary>
    public TerrainChunk? Build(int cx, int cy)
    {
        CountIfMain(ref s_builtOnMain);   // counted: chunks are meant to be built off the main thread
        int step = ChunkPatches * PatchSize;
        int x0 = cx * step, y0 = cy * step;
        int x1 = Math.Min(x0 + step, SizeX), y1 = Math.Min(y0 + step, SizeY);
        if (x0 >= x1 || y0 >= y1)
            return null;
        int w = x1 - x0 + 1, hgt = y1 - y0 + 1;
        var pos = new float[w * hgt * 3];
        var nrm = new float[w * hgt * 3];
        var col = new float[w * hgt * 4];
        var comp = Composition is null ? null : new float[w * hgt];
        var jit = Composition is null ? null : new float[w * hgt];
        for (int j = 0; j < hgt; j++)
            for (int i = 0; i < w; i++)
            {
                int gx = x0 + i, gy = y0 + j, v = j * w + i;
                float z = Height(gx, gy) ?? 0f;
                pos[v * 3] = gx;
                pos[v * 3 + 1] = z;
                pos[v * 3 + 2] = -gy;
                // Central differences in grid space, then into engine space (x, up, -north).
                float dx = (Height(gx + 1, gy) ?? z) - (Height(gx - 1, gy) ?? z);
                float dy = (Height(gx, gy + 1) ?? z) - (Height(gx, gy - 1) ?? z);
                float nx = -dx, ny = 2f, nz = dy;
                float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                nrm[v * 3] = nx / len;
                nrm[v * 3 + 1] = ny / len;
                nrm[v * 3 + 2] = nz / len;
                var (r, g, b) = Ramp(z);
                col[v * 4] = r;
                col[v * 4 + 1] = g;
                col[v * 4 + 2] = b;
                col[v * 4 + 3] = 1f;
                if (comp is not null)
                {
                        comp[v] = Viewer.Core.World.TerrainComposition.Value(Composition!, gx, gy, z, SizeX, SizeY, OriginX, OriginY);
                    jit![v] = Viewer.Core.World.TerrainComposition.Jitter(OriginX + gx, OriginY + gy);
                }
            }
        var idx = new List<int>((w - 1) * (hgt - 1) * 6);
        for (int j = 0; j < hgt - 1; j++)
            for (int i = 0; i < w - 1; i++)
            {
                int gx = x0 + i, gy = y0 + j;
                if (!Patch(gx / PatchSize, gy / PatchSize, out _))
                    continue;   // a hole where the patch has not arrived
                int a = j * w + i, b = a + 1, c = a + w, d = c + 1;
                idx.AddRange([a, c, b, b, c, d]);
            }
        return idx.Count == 0 ? null : new TerrainChunk(cx, cy, pos, nrm, col, idx.ToArray(), comp, jit);
    }

    /// <summary>Colour by height: sand below 20 m, grass to 40 m, rock above (deterministic, no textures yet).</summary>
    public static (float R, float G, float B) Ramp(float z)
    {
        static float L(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);
        if (z < 20f) { float t = z / 20f; return (L(0.55f, 0.76f, t), L(0.50f, 0.70f, t), L(0.35f, 0.45f, t)); }
        if (z < 40f) { float t = (z - 20f) / 20f; return (L(0.30f, 0.20f, t), L(0.55f, 0.45f, t), L(0.20f, 0.15f, t)); }
        { float t = (z - 40f) / 40f; return (L(0.45f, 0.85f, t), L(0.42f, 0.85f, t), L(0.40f, 0.85f, t)); }
    }
}

/// <summary>What one chunk's build reads (<see cref="TerrainGrid.Snapshot"/>), owned by the snapshot
/// (patch copies, never changed): <see cref="Prepare"/> runs on a worker.</summary>
public sealed class TerrainChunkSnapshot
{
    private readonly int _px0, _py0, _w, _h;
    private readonly float[]?[] _patches;
    private readonly TerrainGrid _grid;

    internal TerrainChunkSnapshot(int cx, int cy, int sizeX, int sizeY, int patchSize, Viewer.Core.World.RegionTerrain? composition, double originX, double originY,
        int px0, int py0, int w, int h, float[]?[] patches, Dictionary<(int, int), float>? outside = null)
    {
        ChunkX = cx; ChunkY = cy;
        _px0 = px0; _py0 = py0; _w = w; _h = h; _patches = patches;
        _grid = new TerrainGrid(sizeX, sizeY, patchSize, Lookup)
        {
            Composition = composition, OriginX = originX, OriginY = originY,
            OutsideHeight = outside is null ? null : (gx, gy) => outside.TryGetValue((gx, gy), out var v) ? v : null,
        };
    }

    public int ChunkX { get; }
    public int ChunkY { get; }
    /// <summary>Patches copied (present ones only).</summary>
    public int PatchesCopied => _patches.Count(p => p is not null);

    private bool Lookup(int px, int py, out float[] heights)
    {
        heights = null!;
        int i = px - _px0, j = py - _py0;
        if (i < 0 || j < 0 || i >= _w || j >= _h || _patches[j * _w + i] is not { } p) return false;
        heights = p;
        return true;
    }

    /// <summary>Worker: the chunk built from the snapshot and encoded in the engine's layout (plain C# data).</summary>
    public PreparedTerrainChunk Prepare()
    {
        var chunk = _grid.Build(ChunkX, ChunkY);
        return chunk is null ? new(ChunkX, ChunkY, null, 0, false) : new(ChunkX, ChunkY, TerrainSurface.Prepare(chunk), chunk.VertexCount, chunk.Composition is not null);
    }
}

/// <summary>A chunk's one surface in the engine's layout: engine axes and winding as the chunk is made, its colours,
/// and its composition value in UV.x for the terrain shader. Engine-free (moved here from the host's
/// ServerTerrainView, so a worker can make it).</summary>
public static class TerrainSurface
{
    private static long s_encodedOnMain;
    /// <summary>Chunks encoded on the main thread since the process started (see <see cref="TerrainGrid.MainThreadId"/>).</summary>
    public static long EncodedOnMainThread => Interlocked.Read(ref s_encodedOnMain);

    public static PreparedSurface Prepare(TerrainChunk c)
    {
        TerrainGrid.CountIfMain(ref s_encodedOnMain);
        float[]? uv = null;
        if (c.Composition is { } comp)
        {
            uv = new float[c.VertexCount * 2];
            for (int i = 0; i < c.VertexCount; i++) { uv[i * 2] = comp[i]; if (c.Jitter is { } jit) uv[i * 2 + 1] = jit[i]; }
        }
        return MeshSurfaceData.Encode(c.Positions, c.Normals, c.Indices, uv, c.Colors, slAxes: false, reverseWinding: false);
    }
}

/// <summary>
/// A terrain fixture file (L4): gzip of one JSON header line, a newline, then the heights as little-endian int16
/// centimetres above <c>offset_m</c>, row-major (y then x), <c>size_x * size_y</c> values. Split into patches the way a
/// simulator sends them, and fed into the core as LandPatch events, so the render reads the core's TerrainStore.
/// </summary>
public sealed record TerrainFixture(string Name, int SizeX, int SizeY, int PatchSize, float[] Heights)
{
    public const string Format = "nv-terrain-1";

    public float At(int x, int y) => Heights[y * SizeX + x];

    /// <summary>The patches in simulator order (row by row), each row-major <c>PatchSize x PatchSize</c>.</summary>
    public IEnumerable<(int X, int Y, float[] Heights)> Patches()
    {
        for (int py = 0; py * PatchSize < SizeY; py++)
            for (int px = 0; px * PatchSize < SizeX; px++)
            {
                var h = new float[PatchSize * PatchSize];
                for (int y = 0; y < PatchSize; y++)
                    for (int x = 0; x < PatchSize; x++)
                        h[y * PatchSize + x] = At(Math.Min(px * PatchSize + x, SizeX - 1), Math.Min(py * PatchSize + y, SizeY - 1));
                yield return (px, py, h);
            }
    }

    public byte[] Serialise(float offset = 0f)
    {
        var header = JsonSerializer.Serialize(new { format = Format, name = Name, size_x = SizeX, size_y = SizeY, patch = PatchSize, encoding = "int16-le-cm", offset_m = offset });
        using var raw = new MemoryStream();
        using (var gz = new GZipStream(raw, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            // Fixed gzip header bytes would still carry no timestamp in .NET; the output is deterministic.
            var head = Encoding.UTF8.GetBytes(header + "\n");
            gz.Write(head);
            var buf = new byte[Heights.Length * 2];
            for (int i = 0; i < Heights.Length; i++)
            {
                short cm = checked((short)MathF.Round((Heights[i] - offset) * 100f));
                buf[i * 2] = (byte)cm;
                buf[i * 2 + 1] = (byte)(cm >> 8);
            }
            gz.Write(buf);
        }
        return raw.ToArray();
    }

    public static TerrainFixture Parse(byte[] file)
    {
        using var gz = new GZipStream(new MemoryStream(file), CompressionMode.Decompress);
        using var all = new MemoryStream();
        gz.CopyTo(all);
        var bytes = all.ToArray();
        int nl = Array.IndexOf(bytes, (byte)'\n');
        if (nl < 0)
            throw new InvalidDataException("terrain fixture: no header line");
        using var doc = JsonDocument.Parse(bytes.AsMemory(0, nl));
        var r = doc.RootElement;
        if (r.GetProperty("format").GetString() != Format || r.GetProperty("encoding").GetString() != "int16-le-cm")
            throw new InvalidDataException("terrain fixture: unknown format");
        int sx = r.GetProperty("size_x").GetInt32(), sy = r.GetProperty("size_y").GetInt32(), ps = r.GetProperty("patch").GetInt32();
        float offset = r.GetProperty("offset_m").GetSingle();
        if (sx <= 0 || sy <= 0 || ps <= 0 || sx > 8192 || sy > 8192 || bytes.Length - nl - 1 != sx * sy * 2)
            throw new InvalidDataException("terrain fixture: bad size");
        var h = new float[sx * sy];
        for (int i = 0; i < h.Length; i++)
            h[i] = (short)(bytes[nl + 1 + i * 2] | bytes[nl + 2 + i * 2] << 8) / 100f + offset;
        return new(r.GetProperty("name").GetString() ?? "", sx, sy, ps, h);
    }

    public static string Sha256(byte[] file) => Convert.ToHexString(SHA256.HashData(file));
}

/// <summary>
/// Deterministic synthetic terrain (rolling hills, two peaks, a valley below the 20 m water line), used for the committed
/// L4 fixture and for the 1024 m representation measurement. Integer-seeded, no randomness.
/// </summary>
public static class SyntheticTerrain
{
    public static TerrainFixture Make(string name, int sizeX, int sizeY)
    {
        var h = new float[sizeX * sizeY];
        for (int y = 0; y < sizeY; y++)
            for (int x = 0; x < sizeX; x++)
            {
                double z = 24
                    + 6 * Math.Sin(x / 37.0) * Math.Cos(y / 29.0)
                    + 3 * Math.Sin((x + 2 * y) / 13.0)
                    + 30 * Math.Exp(-(Sq(x - sizeX * 0.3) + Sq(y - sizeY * 0.6)) / Sq(sizeX * 0.09))
                    + 18 * Math.Exp(-(Sq(x - sizeX * 0.72) + Sq(y - sizeY * 0.3)) / Sq(sizeX * 0.06))
                    - 14 * Math.Exp(-Sq(y - sizeY * 0.15) / Sq(sizeY * 0.08));
                h[y * sizeX + x] = (float)Math.Round(Math.Max(0.5, z), 2);
            }
        return new(name, sizeX, sizeY, 16, h);
        static double Sq(double v) => v * v;
    }
}
