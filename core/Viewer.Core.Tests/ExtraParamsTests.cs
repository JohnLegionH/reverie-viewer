// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Buffers.Binary;
using System.Net;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Viewer.Core.Diagnostics;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The PBR material ids an object update carries in its render material parameter (extra parameter 0x80), which the
/// protocol library skips: read from full and compressed updates, raised only when they change, merged into the faces.
/// </summary>
public class ExtraParamsTests
{
    private static readonly Guid MatA = Guid.Parse("0b2f6a44-8e1c-4b7d-a3f2-6d1e9c0a5b02");
    private static readonly Guid MatB = Guid.Parse("7d3e1f90-2a6b-4c8d-9e0f-1a2b3c4d5e06");

    private static byte[] Uuid(Guid g) => new UUID(g).GetBytes();

    /// <summary>An extra parameter block: a count, then (type, length, body) per parameter.</summary>
    private static byte[] Block(params (ushort Type, byte[] Body)[] ps)
    {
        var b = new List<byte> { (byte)ps.Length };
        foreach (var (t, body) in ps)
        {
            var h = new byte[6];
            BinaryPrimitives.WriteUInt16LittleEndian(h, t);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(2), (uint)body.Length);
            b.AddRange(h);
            b.AddRange(body);
        }
        return b.ToArray();
    }

    private static byte[] RenderBody(params (byte Face, Guid Id)[] entries) =>
        new[] { (byte)entries.Length }.Concat(entries.SelectMany(e => new[] { e.Face }.Concat(Uuid(e.Id)))).ToArray();

    [Fact]
    public void A_uuid_reads_as_the_library_reads_it()
    {
        Assert.Equal(new UUID(Uuid(MatA), 0).Guid, ExtraParams.UuidFromBytes(Uuid(MatA)));
        Assert.Equal(MatA, ExtraParams.UuidFromBytes(Uuid(MatA)));
    }

    [Fact]
    public void The_render_material_parameter_is_found_among_the_others()
    {
        var block = Block((0x10, new byte[16]), (0x30, new byte[17]), (ExtraParams.RenderMaterialType, RenderBody((0, MatA), (3, MatB))));
        var e = ExtraParams.RenderMaterials(block, 0);
        Assert.Equal([new RenderMaterialEntry(0, MatA), new RenderMaterialEntry(3, MatB)], e);
        Assert.Empty(ExtraParams.RenderMaterials(Block((ExtraParams.RenderMaterialType, RenderBody())), 0)!);
        Assert.Null(ExtraParams.RenderMaterials(Block((0x10, new byte[16])), 0));   // no render material parameter
        Assert.Null(ExtraParams.RenderMaterials([], 0));
    }

    [Fact]
    public void A_block_that_runs_past_its_data_is_refused()
    {
        var block = Block((ExtraParams.RenderMaterialType, RenderBody((0, MatA))));
        for (int cut = 1; cut < block.Length; cut++)
            Assert.Null(ExtraParams.RenderMaterials(block.AsSpan(0, cut), 0));
        var lying = Block((ExtraParams.RenderMaterialType, [2, 0, .. Uuid(MatA)]));   // says two entries, holds one
        Assert.Null(ExtraParams.RenderMaterials(lying, 0));
    }

    /// <summary>A compressed update with every optional field before the extra parameters set.</summary>
    private static byte[] Compressed(uint local, byte pcode, uint flags, byte[] extra)
    {
        var d = new List<byte>();
        d.AddRange(Uuid(Guid.NewGuid()));
        d.AddRange(BitConverter.GetBytes(local));
        d.Add(pcode);
        d.Add(0);                       // state
        d.AddRange(new byte[4]);        // CRC
        d.Add(3);                       // material
        d.Add(0);                       // click action
        d.AddRange(new byte[36]);       // scale, position, rotation
        d.AddRange(BitConverter.GetBytes(flags));
        d.AddRange(new byte[16]);       // owner
        if ((flags & 0x80) != 0) d.AddRange(new byte[12]);
        if ((flags & 0x20) != 0) d.AddRange(BitConverter.GetBytes(77u));
        if ((flags & 0x02) != 0) d.Add(1);
        else if ((flags & 0x01) != 0) d.AddRange([3, 9, 9, 9]);
        if ((flags & 0x04) != 0) { d.AddRange("hello"u8.ToArray()); d.Add(0); d.AddRange(new byte[4]); }
        if ((flags & 0x200) != 0) { d.AddRange("http://example.org/"u8.ToArray()); d.Add(0); }
        if ((flags & 0x08) != 0) d.AddRange(new byte[86]);
        d.AddRange(extra);
        d.AddRange(new byte[16]);       // sound and what follows (ignored)
        return d.ToArray();
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x80u | 0x20 | 0x01 | 0x04 | 0x200 | 0x08)]
    [InlineData(0x02u | 0x04)]
    public void The_compressed_layout_leads_to_the_extra_parameters(uint flags)
    {
        var extra = Block((ExtraParams.RenderMaterialType, RenderBody((1, MatA))));
        var d = Compressed(4242, (byte)PCode.Prim, flags, extra);
        Assert.True(ExtraParams.CompressedLayout(d, out uint local, out byte pcode, out int at));
        Assert.Equal(4242u, local);
        Assert.Equal((byte)PCode.Prim, pcode);
        Assert.Equal([new RenderMaterialEntry(1, MatA)], ExtraParams.RenderMaterials(d, at));
        Assert.False(ExtraParams.CompressedLayout(d.AsSpan(0, 60), out _, out _, out _));
    }

    [Fact]
    public void The_parameter_decides_every_face_PBR_id_and_keeps_the_other_references()
    {
        var te = new FaceMaterials(new FaceMaterial(1, 0, Guid.Empty, MatB), [null, new FaceMaterial(0, 2, MatA, Guid.Empty)]);
        var m = FaceMaterials.WithRenderMaterials(te, [new RenderMaterialEntry(2, MatA)])!;
        Assert.Equal(Guid.Empty, m[0].RenderMaterialId);   // no entry: none, whatever the texture entry said
        Assert.Equal(1, m[0].Bump);
        Assert.Equal(MatA, m[1].MaterialId);                  // the legacy reference stays
        Assert.Equal(MatA, m[2].RenderMaterialId);
        Assert.Equal(te, FaceMaterials.WithRenderMaterials(te, null));   // no parameter: the texture entry's
        Assert.Null(FaceMaterials.WithRenderMaterials(null, []));
        var only = FaceMaterials.WithRenderMaterials(null, [new RenderMaterialEntry(0, MatB)])!;
        Assert.Equal(MatB, only[0].RenderMaterialId);
        Assert.Equal(Guid.Empty, only[1].RenderMaterialId);   // faces without an entry have none
    }

    [Fact]
    public void The_world_applies_PBR_ids_before_or_after_the_object_and_keeps_them_across_updates()
    {
        var sim = new object();
        var w = new WorldState(new CoreLog(new ListSink(), "test"));
        w.BeginRegion(sim, new RegionInfo("Here", 1000, 1000, 256, 256, true));
        var changes = new List<WorldChange>();
        w.Changed += changes.Add;
        PrimData Prim(uint id) => new(id, Guid.NewGuid(), 0, 0, default, System.Numerics.Quaternion.Identity, System.Numerics.Vector3.One);
        w.ApplyRenderMaterials(sim, 5, [new RenderMaterialEntry(0, MatA)]);   // before its object
        var p5 = Prim(5);
        w.ApplyObject(sim, p5);
        Assert.Equal(MatA, w.Snapshot().Objects[5].Materials![0].RenderMaterialId);
        w.ApplyObject(sim, p5 with { Position = new System.Numerics.Vector3(1, 1, 1) });
        Assert.Equal(MatA, w.Snapshot().Objects[5].Materials![0].RenderMaterialId);   // kept across the update
        Assert.Equal(Guid.Empty, w.Snapshot().Objects[5].Materials![3].RenderMaterialId);
        changes.Clear();
        w.ApplyRenderMaterials(sim, 5, [new RenderMaterialEntry(0, MatA)]);   // the same again
        Assert.Empty(changes);
        w.ApplyRenderMaterials(sim, 5, null);   // removed
        Assert.Null(w.Snapshot().Objects[5].Materials);
        Assert.Single(changes);
    }

    // ---------------- through the adapter ----------------

    private static (LmvTransport T, Simulator Sim, List<RenderMaterialsReceived> Raised) Adapter()
    {
        var t = new LmvTransport(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "test"));
        var sim = new Simulator(t.Client, new IPEndPoint(IPAddress.Parse("192.0.2.10"), 9000), 256000UL << 32 | 256000UL, 256, 256);
        var raised = new List<RenderMaterialsReceived>();
        t.Event += e => { if (e is RenderMaterialsReceived r) lock (raised) raised.Add(r); };
        return (t, sim, raised);
    }

    private static PacketReceivedEventArgs Full(Simulator sim, uint local, byte pcode, byte[] extra) =>
        new(new ObjectUpdatePacket { ObjectData = [new ObjectUpdatePacket.ObjectDataBlock { ID = local, PCode = pcode, ExtraParams = extra }] }, sim);

    [Fact]
    public void A_full_update_raises_its_PBR_ids_only_when_they_change()
    {
        var (t, sim, raised) = Adapter();
        using (t)
        {
            var withA = Block((ExtraParams.RenderMaterialType, RenderBody((0, MatA))));
            t.OnObjectUpdatePacket(null, Full(sim, 9, (byte)PCode.Prim, Block()));          // none, never had any: nothing
            t.OnObjectUpdatePacket(null, Full(sim, 9, (byte)PCode.Prim, withA));
            t.OnObjectUpdatePacket(null, Full(sim, 9, (byte)PCode.Prim, withA));            // unchanged: nothing
            t.OnObjectUpdatePacket(null, Full(sim, 9, (byte)PCode.Prim, Block()));          // removed
            t.OnObjectUpdatePacket(null, Full(sim, 10, (byte)PCode.Tree, withA));           // not a prim
            Assert.Equal(2, raised.Count);
            Assert.Equal([new RenderMaterialEntry(0, MatA)], raised[0].Faces);
            Assert.Equal(9u, raised[0].LocalId);
            Assert.Null(raised[1].Faces);
        }
    }

    [Fact]
    public void A_compressed_update_raises_its_PBR_ids()
    {
        var (t, sim, raised) = Adapter();
        using (t)
        {
            var d = Compressed(31, (byte)PCode.Prim, 0x20 | 0x04, Block((ExtraParams.RenderMaterialType, RenderBody((2, MatB)))));
            t.OnObjectUpdateCompressedPacket(null, new PacketReceivedEventArgs(
                new ObjectUpdateCompressedPacket { ObjectData = [new ObjectUpdateCompressedPacket.ObjectDataBlock { Data = d }] }, sim));
            var r = Assert.Single(raised);
            Assert.Equal(31u, r.LocalId);
            Assert.Equal([new RenderMaterialEntry(2, MatB)], r.Faces);
        }
    }
}
