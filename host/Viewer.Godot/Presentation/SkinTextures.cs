// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using Viewer.Core.Avatars;
using Viewer.Core.Meshing;

namespace Viewer.Godot.Presentation;

/// <summary>
/// The two textures the shader that skins worn rigged meshes reads (<see cref="FaceShader.Skin"/>; the layout is
/// <see cref="SkinTables"/>'s): <see cref="Bones"/>, a row of joint matrices for each avatar that wears a rigged mesh, rewritten every frame
/// the avatar is posed; and <see cref="Atlas"/>, a pair of rows for each rigged mesh asset (its joints' bind matrices and the
/// skeleton node each joint is), written when the mesh is first worn and shared by every avatar wearing it. Both are RGBA float and
/// grow by doubling (the texture keeps its engine id, so no material changes) up to a limit (rows) or by a quarter at a time up to the device's limit (tables); past it a row or table is refused (-1) until one is freed.
/// Main thread only.
/// </summary>
public sealed class SkinTextures : IDisposable
{
    public const int StartRows = 16, MaxRows = 1024;

    private readonly int _entries;             // matrices in an avatar's row: the nodes and the avatar root
    private readonly int _boneWidth;           // texels in an avatar's row
    private byte[] _boneBytes;
    private Image _boneImage;
    private int _boneRows;
    private bool _boneDirty;
    private readonly List<bool> _rowUsed = [];
    internal const int AtlasRowTexels = MeshSkinBlock.MaxJoints * SkinTables.TexelsPerMatrix;
    private const int AtlasWidth = AtlasRowTexels;
    private byte[] _atlasBytes;
    private Image? _atlasImage;
    private ImageTexture? _atlasTexture;
    private SkinAtlas? _gpu;      // the atlas as a texture on the rendering device (no copy kept here); null: the image path below
    private bool _atlasDirty;

    /// <summary>True (the default): the atlas lives on the rendering device and grows and takes new tables there (<see cref="SkinAtlas"/>), so the main thread
    /// never copies the whole atlas. False, or with no rendering device: the atlas is an image texture handed over whole each time (the comparison
    /// in the arrival checks, <c>--tr-alter=imageatlas</c>).</summary>
    public static bool DeviceAtlas { get; set; } = true;
    private readonly TableAllocator _tables;
    private int _atlasTables;   // tables the texture holds: the allocator's capacity, after the next resize

    /// <param name="maxTables">The most tables the atlas may hold (two rows each); the texture is that many times two rows high, so the
    /// device's largest 2D texture bounds it (<see cref="DeviceMaxTables"/>).</param>
    public SkinTextures(int nodeCount, int maxTables = 0)
    {
        _entries = nodeCount + 1;
        _boneWidth = _entries * SkinTables.TexelsPerMatrix;
        _boneRows = StartRows;
        _boneBytes = new byte[_boneWidth * _boneRows * 16];
        _boneImage = Image.CreateFromData(_boneWidth, _boneRows, false, Image.Format.Rgbaf, _boneBytes);
        Bones = ImageTexture.CreateFromImage(_boneImage);
        for (int i = 0; i < _boneRows; i++) _rowUsed.Add(false);
        _tables = new TableAllocator(maxTables > 0 ? maxTables : DeviceMaxTables(), growthDivisor: DeviceAtlas && RenderingServer.GetRenderingDevice() is not null ? 2 : 4);
        _atlasTables = _tables.Capacity;
        _gpu = DeviceAtlas ? SkinAtlas.TryCreate(_atlasTables) : null;
        if (_gpu is not null) { _atlasBytes = []; return; }
        _atlasBytes = new byte[AtlasWidth * _atlasTables * 2 * 16];
        _atlasImage = Image.CreateFromData(AtlasWidth, _atlasTables * 2, false, Image.Format.Rgbaf, _atlasBytes);
        _atlasTexture = ImageTexture.CreateFromImage(_atlasImage);
    }

    /// <summary>A row of joint matrices for each avatar (<see cref="AcquireRow"/>).</summary>
    public ImageTexture Bones { get; }

    /// <summary>A pair of rows for each rigged mesh asset (<see cref="AcquireTable"/>).</summary>
    public Texture2D Atlas => _gpu?.Texture ?? _atlasTexture!;

    public int RowsInUse { get; private set; }
    public int TablesInUse => _tables.InUse;
    public int RowCapacity => _boneRows;
    public int TableCapacity => _atlasTables;
    public int MaxTables => _tables.MaxTables;

    /// <summary>Bytes of the atlas texture alone (what the engine holds; <see cref="Bytes"/> counts the copies kept here too).</summary>
    public long AtlasBytes => TableAllocator.Bytes(_atlasTables);

    /// <summary>The atlas limit the device allows: half its largest 2D texture (a table is two rows), at most <see cref="TableAllocator.DefaultMaxTables"/>.</summary>
    private static int DeviceMaxTables()
    {
        long side = (long)(RenderingServer.GetRenderingDevice()?.LimitGet(RenderingDevice.Limit.MaxTextureSize2D) ?? 0UL);
        return side >= 2 ? (int)Math.Min(side / 2, TableAllocator.DefaultMaxTables) : 4096 / 2;
    }

    /// <summary>Entries in an avatar's row (the rig's nodes and the avatar root): the size <see cref="Row"/> has.</summary>
    public int RowEntries => _entries;

    /// <summary>Texture bytes held, both textures.</summary>
    public long Bytes => _boneBytes.LongLength + _atlasBytes.LongLength;

    /// <summary>Takes a row for an avatar; -1 when the limit is reached.</summary>
    public int AcquireRow()
    {
        for (int i = 0; i < _rowUsed.Count; i++)
            if (!_rowUsed[i]) return Take(i);
        if (_boneRows >= MaxRows) return -1;
        GrowBones();
        return Take(_rowUsed.IndexOf(false));
    }

    private int Take(int row)
    {
        _rowUsed[row] = true;
        RowsInUse++;
        return row;
    }

    public void ReleaseRow(int row)
    {
        if (row < 0 || row >= _rowUsed.Count || !_rowUsed[row]) return;
        _rowUsed[row] = false;
        RowsInUse--;
    }

    /// <summary>The avatar's row, to write its <see cref="SkinTables.PackBones"/> into; call <see cref="Touch"/> after.</summary>
    public Span<float> Row(int row) => MemoryMarshal.Cast<byte, float>(_boneBytes.AsSpan(row * _boneWidth * 16, _boneWidth * 16));

    public void Touch() => _boneDirty = true;

    /// <summary>Hands the engine what was written since the last call: the avatars' rows and the new tables, one texture update each however many
    /// were written (a table is written into memory when it is acquired; the whole atlas goes to the engine here, once a frame).</summary>
    public void Flush()
    {
        if (_boneDirty)
        {
            _boneDirty = false;
            using (FrameProbe.Begin(FrameProbe.SkinFlushBones))
            {
                _boneImage.SetData(_boneWidth, _boneRows, false, Image.Format.Rgbaf, _boneBytes);
                Bones.Update(_boneImage);
            }
        }
        if (_tables.Trim() < _atlasTables) ResizeAtlas();   // the empty top of the atlas goes back when few tables are left
        if (_gpu is not null)
        {
            using (FrameProbe.Begin(FrameProbe.SkinFlushAtlas)) _gpu.Flush();   // the tables written since, copied into place on the device
        }
        else if (_atlasDirty)
        {
            _atlasDirty = false;
            using (FrameProbe.Begin(FrameProbe.SkinFlushAtlas))
            {
                _atlasImage!.SetData(AtlasWidth, _atlasTables * 2, false, Image.Format.Rgbaf, _atlasBytes);
                _atlasTexture!.Update(_atlasImage);
            }
        }
    }

    private void GrowBones()
    {
        int rows = _boneRows * 2;
        var bytes = new byte[_boneWidth * rows * 16];
        _boneBytes.CopyTo(bytes, 0);
        var image = Image.CreateFromData(_boneWidth, rows, false, Image.Format.Rgbaf, bytes);
        Bones.SetImage(image);   // the same texture id with more rows: no material changes
        _boneImage.Dispose();
        _boneImage = image;
        _boneBytes = bytes;
        for (int i = _boneRows; i < rows; i++) _rowUsed.Add(false);
        _boneRows = rows;
    }

    /// <summary>The table of a mesh asset's skin, made the first time it is asked for and counted by users after; its slot for the
    /// shader's <c>skin_row</c> (the first of its two rows is slot * 2). -1 when the limit is reached or the skin has no usable joint.</summary>
    public int AcquireTable(Guid asset, AvatarRig rig, MeshSkinBlock skin, Viewer.Shell.PreparedRiggedLod? prepared = null)
    {
        if (!_tables.Contains(asset) && skin.UsableJoints == 0) return -1;
        int slot = _tables.Acquire(asset, out bool isNew);
        if (slot < 0 || !isNew) return slot;
        if (_tables.Capacity > _atlasTables) ResizeAtlas();
        int n = skin.UsableJoints;
        float[] matrices, nodes;
        if (_gpu is not null)
        {
            if (prepared is not null && prepared.Matrices.Length == n * SkinTables.FloatsPerMatrix && prepared.Nodes.Length == n) (matrices, nodes) = (prepared.Matrices, prepared.Nodes);
            else
            {
                matrices = new float[n * SkinTables.FloatsPerMatrix];
                nodes = new float[n];
                SkinTables.BuildTable(rig, skin, matrices, nodes);
            }
            _gpu.Write(slot, matrices, nodes);
            return slot;
        }
        if (prepared is not null && prepared.Matrices.Length == n * SkinTables.FloatsPerMatrix && prepared.Nodes.Length == n)
            (matrices, nodes) = (prepared.Matrices, prepared.Nodes);   // made off the main thread
        else
        {
            matrices = new float[n * SkinTables.FloatsPerMatrix];
            nodes = new float[n];
            SkinTables.BuildTable(rig, skin, matrices, nodes);
        }
        var floats = MemoryMarshal.Cast<byte, float>(_atlasBytes.AsSpan());
        var first = floats.Slice(slot * 2 * AtlasWidth * 4, AtlasWidth * 4);
        var second = floats.Slice((slot * 2 + 1) * AtlasWidth * 4, AtlasWidth * 4);
        first.Clear();
        second.Clear();
        matrices.CopyTo(first);
        for (int j = 0; j < n; j++) second[j * 4] = nodes[j];   // the node is the texel's first channel
        _atlasDirty = true;   // handed to the engine by Flush
        return slot;
    }

    /// <summary>One user of the asset's table is gone; the slot is free for another asset when the last one goes (true then).</summary>
    public bool ReleaseTable(Guid asset) => _tables.Release(asset);

    /// <summary>Makes the texture the allocator's capacity high, keeping the tables in it (the same texture id: no material changes).</summary>
    private void ResizeAtlas()
    {
        using var probe = FrameProbe.Begin(FrameProbe.SkinResize);
        int tables = _tables.Capacity;
        if (_gpu is not null)
        {
            _gpu.Resize(tables);
            _atlasTables = tables;
            return;
        }
        var bytes = new byte[AtlasWidth * tables * 2 * 16];
        _atlasBytes.AsSpan(0, Math.Min(_atlasBytes.Length, bytes.Length)).CopyTo(bytes);
        var image = Image.CreateFromData(AtlasWidth, tables * 2, false, Image.Format.Rgbaf, bytes);
        _atlasTexture!.SetImage(image);
        _atlasImage!.Dispose();
        _atlasImage = image;
        _atlasBytes = bytes;
        _atlasTables = tables;
    }

    /// <summary>The textures and images, for the lifecycle check.</summary>
    internal IEnumerable<(string Kind, GodotObject Object)> EngineObjects()
    {
        yield return ("skin bone texture", Bones);
        yield return ("skin atlas texture", Atlas);
    }

    public void Dispose()
    {
        Bones.Dispose();
        _gpu?.Dispose();
        _atlasTexture?.Dispose();
        _boneImage.Dispose();
        _atlasImage?.Dispose();
    }
}
