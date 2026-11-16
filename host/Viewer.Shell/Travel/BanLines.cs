// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Session;

namespace Viewer.Shell.Travel;

/// <summary>The "show ban lines" setting: never, when the agent is refused, or while a refusing parcel is near.</summary>
public enum BanLineMode { Hide = 0, OnCollision = 1, OnProximity = 2 }

/// <summary>Which way a wall face turns: toward the side the agent is on.</summary>
public enum WallFacing { South, North, West, East }

/// <summary>One 4 m piece of wall in region coordinates: its two ends, the ground under each end, the top, and its opacity.</summary>
public readonly record struct WallQuad(float X1, float Y1, float X2, float Y2, float Z1, float Z2, float Top, float Alpha, WallFacing Facing);

/// <summary>The wall drawn at the edge of a parcel that refuses the agent (behaviour of the official viewer's ban lines).</summary>
public static class BanWall
{
    public const int CellsPerEdge = 64;
    public const float StepMeters = 4f;
    public const float ParcelHeight = 50f;
    public const float BanHeight = 5000f;
    public const float MaxAlpha = 0.95f;
    public const byte EastMask = 1, NorthMask = 2, WestMask = 4, SouthMask = 8;
    public const int TileSize = 256;

    /// <summary>The wall segments of a parcel from its cell bitmap: (cells + 1) squared bytes, each holding a south and a west bit; an edge two
    /// set cells share is switched on twice, so it is off (only the outline stays). A bitmap shorter than the grid counts as zeros.</summary>
    public static byte[] SegmentsFromBitmap(byte[] bitmap, int cells = CellsPerEdge)
    {
        int stride = cells + 1;
        var seg = new byte[stride * stride];
        for (int y = 0; y < cells; y++)
        {
            for (int x = 0; x < cells; x++)
            {
                int bit = x + y * cells;
                if (bit / 8 >= bitmap.Length || (bitmap[bit / 8] & (1 << (bit % 8))) == 0)
                    continue;
                int o = x + y * stride;
                seg[o] ^= SouthMask;
                seg[o + stride] ^= SouthMask;
                seg[o] ^= WestMask;
                seg[o + 1] ^= WestMask;
            }
        }
        return seg;
    }

    /// <summary>The parcel squares per region edge that a parcel bitmap covers (one bit per 4 m square): 64 for a 256 m region, more for
    /// a larger one.</summary>
    public static int CellsFor(byte[] bitmap) => bitmap.Length == 0 ? CellsPerEdge : Math.Max(1, (int)Math.Round(Math.Sqrt(bitmap.Length * 8.0)));

    /// <summary>A ban is a wall 5000 m tall; a refusal by the access list or the group, 50 m.</summary>
    public static float HeightFor(ParcelDenial denial) => denial == ParcelDenial.Banned ? BanHeight : ParcelHeight;

    /// <summary>The opacity for a squared distance (in metres) from the agent to the piece, offset as the official viewer offsets it:
    /// fully shown inside 5 m, gone beyond 13 m, 30 over the squared distance between.</summary>
    public static float Alpha(float distanceSquared)
    {
        if (distanceSquared < 25f) return MaxAlpha;
        if (distanceSquared > 169f) return 0f;
        return Math.Clamp(30f / distanceSquared, 0f, MaxAlpha);
    }

    public static IReadOnlyList<WallQuad> Quads(byte[] segments, int cells, Vector2 agent, Func<float, float, float> ground, float height)
    {
        const float Offset = 5f;
        const float Shift = 0.1f;   // keeps the wall off the ground's own depth
        int stride = cells + 1;
        float border = cells * StepMeters - 0.1f;   // heights are read just inside the region's edge
        var quads = new List<WallQuad>();
        void Add(float x1, float y1, float x2, float y2, float alpha, WallFacing facing)
        {
            float z1 = ground(Math.Min(x1, border), Math.Min(y1, border));
            float z2 = ground(Math.Min(x2, border), Math.Min(y2, border));
            quads.Add(new WallQuad(x1 + Shift, y1 + Shift, x2 + Shift, y2 + Shift, z1, z2, Math.Max(z1, z2) + height, alpha, facing));
        }
        for (int y = 0; y < stride; y++)
        {
            for (int x = 0; x < stride; x++)
            {
                byte mask = segments[x + y * stride];
                if ((mask & SouthMask) != 0)
                {
                    float x1 = x * StepMeters, y1 = y * StepMeters, x2 = x1 + StepMeters;
                    float dy = (agent.Y - y1) + Offset;
                    float dx = agent.X < x1 ? agent.X - x1 : agent.X > x2 ? agent.X - x2 : 0f;
                    Add(x1, y1, x2, y1, Alpha(dx * dx + dy * dy), agent.Y - y1 < 0f ? WallFacing.South : WallFacing.North);
                }
                if ((mask & WestMask) != 0)
                {
                    float x1 = x * StepMeters, y1 = y * StepMeters, y2 = y1 + StepMeters;
                    float dx = (agent.X - x1) + Offset;
                    float dy = agent.Y < y1 ? agent.Y - y1 : agent.Y > y2 ? agent.Y - y2 : 0f;
                    Add(x1, y1, x1, y2, Alpha(dx * dx + dy * dy), agent.X - x1 > 0f ? WallFacing.West : WallFacing.East);
                }
            }
        }
        return quads;
    }

    /// <summary>A 256 by 256 RGBA tile that repeats every 2 m both ways: a band of diagonal stripes across its middle, transparent elsewhere;
    /// yellow, or green for a parcel that sells passes.</summary>
    public static byte[] Tile(bool pass)
    {
        var tile = new byte[TileSize * TileSize * 4];
        (byte r, byte g, byte b)[] colours = pass
            ? new (byte, byte, byte)[] { (96, 180, 72), (150, 206, 120) }
            : new (byte, byte, byte)[] { (232, 222, 72), (244, 238, 168) };
        int bandTop = TileSize * 9 / 20, bandBottom = TileSize * 11 / 20;   // a tenth of the tile's height, about its middle
        for (int y = bandTop; y <= bandBottom; y++)
        {
            for (int x = 0; x < TileSize; x++)
            {
                var c = colours[((x + y) / 32) & 1];
                int o = (y * TileSize + x) * 4;
                tile[o] = c.r; tile[o + 1] = c.g; tile[o + 2] = c.b; tile[o + 3] = 255;
            }
        }
        return tile;
    }
}

/// <summary>When the wall is on show. Main thread only.</summary>
public sealed class BanLineState
{
    public const double CollisionSeconds = 10;   // on collision: the wall stays this long after a refusal
    public const double ProximitySeconds = 1;    // on proximity: each collision message keeps it this long

    private double? _timer;

    public BanLineMode Mode { get; set; } = BanLineMode.OnCollision;
    public ParcelDenial? Denial { get; private set; }
    public byte[]? Segments { get; private set; }

    /// <summary>The parcel squares per edge of the region the wall is in.</summary>
    public int Cells { get; private set; } = BanWall.CellsPerEdge;
    public bool UsePass { get; private set; }

    /// <summary>The region the last collision message came from: the wall is drawn in that region, which is not always the one the agent is in.</summary>
    public SimRef? Sim { get; private set; }

    /// <summary>The region says a nearby parcel refuses the agent: its outline becomes the wall. On proximity this also starts the clock.</summary>
    public void Collision(ParcelCollision c, double now)
    {
        Denial = c.Kind;
        Sim = c.Sim;
        UsePass = c.UsePassList;
        Cells = c.Bitmap is null ? BanWall.CellsPerEdge : BanWall.CellsFor(c.Bitmap);   // a larger region's bitmap has more squares per edge
        Segments = c.Bitmap is null ? null : BanWall.SegmentsFromBitmap(c.Bitmap, Cells);
        if (Mode == BanLineMode.OnProximity)
            _timer = now;
    }

    /// <summary>The agent was told it is refused: on collision this starts the clock.</summary>
    public void Denied(double now)
    {
        if (Mode == BanLineMode.OnCollision)
            _timer = now;
    }

    public void Clear()
    {
        Denial = null;
        Sim = null;
        Segments = null;
        UsePass = false;
        _timer = null;
    }

    public bool Drawing(double now) =>
        Mode != BanLineMode.Hide && Segments is not null && _timer is { } t
        && now - t <= (Mode == BanLineMode.OnProximity ? ProximitySeconds : CollisionSeconds);
}
