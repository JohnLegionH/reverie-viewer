// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using Godot;
using Viewer.Core.Avatars;

namespace Viewer.Godot.Presentation;

/// <summary>
/// The bind-table atlas (<see cref="SkinTextures"/>) held as a texture of its own on the rendering device, so that the main thread never moves the whole atlas.
/// A texture made from an <see cref="Image"/> has to be handed over whole to be grown or changed: with several hundred tables that is a copy of
/// several megabytes in the engine each time a table is added, and again to make it bigger. Here the atlas grows by creating the larger texture on the device
/// and copying the old one into it there (nothing passes through managed memory), and new tables are written by copying a few rows from a small staging
/// texture into place. The texture keeps its identity (<see cref="Texture"/>) when it grows, so no material changes. Nothing is kept on the CPU side: the
/// tables a frame adds are packed once and gone after <see cref="Flush"/>. Main thread only; null from <see cref="TryCreate"/> without a rendering device
/// (the compatibility renderer), where <see cref="SkinTextures"/> keeps the image path.
/// </summary>
internal sealed class SkinAtlas : IDisposable
{
    private readonly RenderingDevice _rd;
    private Rid _tex;
    private readonly Texture2Drd _texture;
    private readonly List<(int Slot, int Joints, float[] Matrices, float[] Nodes)> _pending = [];

    /// <summary>Texels in a row of the atlas: a table's joints (<see cref="SkinTextures.AtlasRowTexels"/>).</summary>
    public const int Width = SkinTextures.AtlasRowTexels;
    private const int FloatsPerRow = Width * 4;

    private SkinAtlas(RenderingDevice rd, int tables)
    {
        _rd = rd;
        _tex = Make(tables);
        Tables = tables;
        _texture = new Texture2Drd { TextureRdRid = _tex };
    }

    /// <summary>Tables the texture holds.</summary>
    public int Tables { get; private set; }

    /// <summary>The texture the shaders read; the same object for the atlas's life.</summary>
    public Texture2D Texture => _texture;

    /// <summary>Tables written and not yet on the device.</summary>
    public int Pending => _pending.Count;

    public static SkinAtlas? TryCreate(int tables)
    {
        var rd = RenderingServer.GetRenderingDevice();
        if (rd is null) return null;
        try { return new SkinAtlas(rd, tables); }
        catch (Exception) { return null; }
    }

    private Rid Make(int tables)
    {
        using var format = new RDTextureFormat
        {
            Width = (uint)Width, Height = (uint)(tables * 2), Depth = 1, ArrayLayers = 1, Mipmaps = 1,
            Format = RenderingDevice.DataFormat.R32G32B32A32Sfloat,
            TextureType = RenderingDevice.TextureType.Type2D,
            Samples = RenderingDevice.TextureSamples.Samples1,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanUpdateBit
                | RenderingDevice.TextureUsageBits.CanCopyFromBit | RenderingDevice.TextureUsageBits.CanCopyToBit,
        };
        using var view = new RDTextureView();
        var rid = _rd.TextureCreate(format, view);
        if (!rid.IsValid) throw new InvalidOperationException("the rendering device made no atlas texture");
        return rid;   // (rows nobody has written are not read: a table's two rows are written whole when it is made)
    }

    /// <summary>Makes the texture <paramref name="tables"/> tables high, keeping the rows of the tables it had (the rest of a smaller texture is dropped). The device
    /// does the copy.</summary>
    public void Resize(int tables)
    {
        if (tables == Tables) return;
        var next = Make(tables);
        _rd.TextureCopy(_tex, next, Vector3.Zero, Vector3.Zero, new Vector3(Width, Math.Min(Tables, tables) * 2, 1), 0, 0, 0, 0);
        var old = _tex;
        _tex = next;
        _texture.TextureRdRid = next;   // the same Texture2D, now backed by the larger texture
        Tables = tables;
        _rd.FreeRid(old);
        _pending.RemoveAll(p => p.Slot >= tables);
    }

    /// <summary>Queues a table for the next <see cref="Flush"/>: <paramref name="matrices"/> are its joints' matrices (<see cref="SkinTables.FloatsPerMatrix"/> floats
    /// each, three texels) and <paramref name="nodes"/> the node of each joint. Only the texels a table uses are written; the rest of its rows are never read.</summary>
    public void Write(int slot, ReadOnlySpan<float> matrices, ReadOnlySpan<float> nodes)
    {
        _pending.RemoveAll(p => p.Slot == slot);   // a slot written again before the flush keeps its latest rows
        _pending.Add((slot, nodes.Length, matrices.ToArray(), nodes.ToArray()));
    }

    /// <summary>Puts the queued tables on the device: one small staging texture holding all of them (as wide as the widest table), and a copy of each one's
    /// two rows into place.</summary>
    public void Flush()
    {
        if (_pending.Count == 0) return;
        int k = _pending.Count;
        int width = 1;
        foreach (var p in _pending) width = Math.Max(width, p.Joints * SkinTables.TexelsPerMatrix);
        int stride = width * 4;   // floats in a staging row
        var floats = new float[k * 2 * stride];
        for (int i = 0; i < k; i++)
        {
            var p = _pending[i];
            p.Matrices.AsSpan(0, p.Joints * SkinTables.FloatsPerMatrix).CopyTo(floats.AsSpan(i * 2 * stride));
            for (int j = 0; j < p.Joints; j++) floats[(i * 2 + 1) * stride + j * 4] = p.Nodes[j];   // the node is the texel's first channel
        }
        var bytes = new byte[floats.Length * sizeof(float)];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        using (var image = Image.CreateFromData(width, k * 2, false, Image.Format.Rgbaf, bytes))
        using (var staging = ImageTexture.CreateFromImage(image))
        {
            var from = RenderingServer.TextureGetRdTexture(staging.GetRid());
            for (int i = 0; i < k; i++)
            {
                var p = _pending[i];
                if (p.Joints == 0) continue;
                _rd.TextureCopy(from, _tex, new Vector3(0, i * 2, 0), new Vector3(0, p.Slot * 2, 0), new Vector3(p.Joints * SkinTables.TexelsPerMatrix, 1, 1), 0, 0, 0, 0);
                _rd.TextureCopy(from, _tex, new Vector3(0, i * 2 + 1, 0), new Vector3(0, p.Slot * 2 + 1, 0), new Vector3(p.Joints, 1, 1), 0, 0, 0, 0);
            }
        }
        _pending.Clear();
    }

    public void Dispose()
    {
        _texture.Dispose();
        if (_tex.IsValid) _rd.FreeRid(_tex);
        _tex = default;
    }
}
