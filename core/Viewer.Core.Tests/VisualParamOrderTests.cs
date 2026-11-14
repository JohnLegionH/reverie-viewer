// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Immutable;
using LibreMetaverse;
using Viewer.Core.Diagnostics;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// LibreMetaverse &lt;= 3.1.4 put visual parameters on the wire in the wrong order; 3.1.5
/// fixed it (ascending parameter id). The core decodes a received AvatarAppearance by parameter id using the pinned
/// library's own order. A known blob is decoded through the production adapter path and checked by id, against both
/// hand-placed values and LibreMetaverse 3.1.6's own decoder; the negative control shows the check fails when the
/// order is reversed.
/// </summary>
public class VisualParamOrderTests
{
    private static readonly int[] Order = VisualParams.Group0ParamIds;

    /// <summary>A blob of 64s (female, and unlike every placed value) with known bytes at the positions 3.1.6 assigns to params 1, 33 (height), 80 (male), last.</summary>
    private static List<byte> KnownBlob()
    {
        var blob = Enumerable.Repeat((byte)64, Order.Length).ToList();
        blob[Array.IndexOf(Order, 1)] = 17;
        blob[Array.IndexOf(Order, 33)] = 200;
        blob[Array.IndexOf(Order, 80)] = 255;
        blob[^1] = 99;
        return blob;
    }

    private static AvatarAppearanceEventArgs Packet(List<byte> blob)
    {
        var te = new Primitive.TextureEntry(UUID.Zero);
        te.CreateFace(8).TextureID = new UUID("aaaaaaaa-0000-0000-0000-000000000008");
        te.CreateFace(9).TextureID = AppearanceManager.DEFAULT_AVATAR_TEXTURE;   // the library default is "no bake"
        return new AvatarAppearanceEventArgs(null!, new UUID("bbbbbbbb-0000-0000-0000-000000000001"), false, te.DefaultTexture!,
            te.FaceTextures, blob, 1, 5, AppearanceFlags.None, 0);
    }

    [Fact]
    public void The_pinned_wire_order_is_ascending_parameter_id()
    {
        Assert.Equal(253, Order.Length);
        Assert.True(Order.Zip(Order.Skip(1)).All(p => p.First < p.Second), "3.1.6 wire order must be ascending by id");
    }

    [Fact]
    public void Decoding_a_known_AvatarAppearance_yields_the_expected_parameter_values_by_id()
    {
        var e = Packet(KnownBlob());
        using var transport = new LmvTransport(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "test"));
        var d = transport.ToAppearanceData(e);   // the production path (the transport's own copy of the order)

        Assert.Equal(e.AvatarID.Guid, d.AgentId);
        Assert.Equal(253, d.VisualParams.Count);
        Assert.True(d.VisualParams.TryGet(1, out var big)); Assert.Equal(17, big);
        Assert.True(d.VisualParams.TryGet(33, out var height)); Assert.Equal(200, height);
        Assert.True(d.VisualParams.TryGet(80, out var male)); Assert.Equal(255, male);
        Assert.True(d.VisualParams.TryGet(Order[^1], out var last)); Assert.Equal(99, last);
        Assert.Equal("male", d.VisualParams.Sex);
        Assert.False(d.VisualParams.TryGet(999_999, out _));

        // Against LibreMetaverse 3.1.6's own decoder, for the parameters placed above.
        var lm = e.DecodeVisualParams();
        foreach (int id in new[] { 1, 33, 80 })
        {
            Assert.True(d.VisualParams.TryGet(id, out var b));
            var vp = VisualParams.Params[id];
            Assert.Equal(lm[id], Utils.ByteToFloat(b, vp.MinValue, vp.MaxValue), 5);
        }

        // Faces: the head bake is carried; the library default is not a bake.
        var bakes = AppearanceSlots.BakesOf(d.FaceTextures);
        Assert.Equal(new Guid("aaaaaaaa-0000-0000-0000-000000000008"), bakes["head"]);
        Assert.False(bakes.ContainsKey("upper"));
        Assert.Equal(1, d.AppearanceVersion);
        Assert.Equal(5, d.CofVersion);
    }

    [Fact]
    public void Negative_control_a_reversed_wire_order_decodes_the_wrong_values()
    {
        // The same packet decoded with the order reversed (the kind of error was): the by-id check above would fail.
        var reversed = Order.Reverse().ToImmutableArray();
        var d = LmvTransport.ToAppearanceData(Packet(KnownBlob()), reversed);
        Assert.True(d.VisualParams.TryGet(80, out var male));
        Assert.NotEqual(255, male);
        Assert.True(d.VisualParams.TryGet(33, out var height));
        Assert.NotEqual(200, height);
        Assert.NotEqual("male", d.VisualParams.Sex);
    }

    [Fact]
    public void The_received_bytes_are_copied_not_aliased()
    {
        var blob = KnownBlob();
        var d = LmvTransport.ToAppearanceData(Packet(blob), Order.ToImmutableArray());
        blob[Array.IndexOf(Order, 80)] = 0;
        Assert.True(d.VisualParams.TryGet(80, out var male));
        Assert.Equal(255, male);
    }
}
