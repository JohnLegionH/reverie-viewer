// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Immutable;
using System.Numerics;
using LibreMetaverse.Packets;
using Viewer.Core.Avatars;
using Viewer.Core.Diagnostics;
using Viewer.Core.Protocol;
using Viewer.Core.Replay;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The hover height of an avatar. The official viewer takes another avatar's hover from the AppearanceHover block of its
/// AvatarAppearance message (llvoavatar.cpp:9912-9923: the block's height when there is one, zero when the message has none, the
/// Hover visual parameter is not consulted), and its own from its preference (the Hover parameter's weight here); the message's
/// value for the own avatar is ignored.
/// </summary>
public class AvatarHoverTests
{
    private static readonly SimRef Here = new(256000UL << 32 | 256000UL, "Here");
    private static readonly Guid Other = Guid.Parse("00000000-0000-4000-8000-0000000000a1");
    private static readonly Guid Me = Guid.Parse("00000000-0000-4000-8000-0000000000a2");

    private static WorldState NewWorld()
    {
        var w = new WorldState(new CoreLog(new ListSink(), "test"));
        w.BeginRegion(Here, new RegionInfo("Here", 1000, 1000, 256, 256, false));
        return w;
    }

    private static AvatarAppearanceData Look(Guid agent) => new(agent, [], VisualParamSet.Empty, 1, 1);

    [Fact]
    public void The_hover_block_of_the_message_is_the_avatars_hover()
    {
        var w = NewWorld();
        w.ApplyAppearance(Here, Look(Other), Me);
        w.ApplyHover(Here, Other, 0.4f);
        Assert.Equal(0.4f, w.Appearance(Other)!.Hover);
    }

    [Fact]
    public void A_message_without_the_block_leaves_no_hover_and_a_later_block_replaces_it()
    {
        var w = NewWorld();
        w.ApplyHover(Here, Other, 0.4f);             // the block before the appearance is kept for it
        w.ApplyAppearance(Here, Look(Other), Me);
        Assert.Equal(0.4f, w.Appearance(Other)!.Hover);
        w.ApplyHover(Here, Other, null);             // the next message has no block
        Assert.Null(w.Appearance(Other)!.Hover);
        w.ApplyHover(Here, Other, -0.25f);
        Assert.Equal(-0.25f, w.Appearance(Other)!.Hover);
    }

    [Fact]
    public void Another_avatars_shape_takes_the_blocks_hover_and_zero_when_there_is_none_not_the_parameter()
    {
        var rig = ShapeSkeletonTests.Rig();
        var vp = ShapeSkeletonTests.Wire((11001, 255));                 // the Hover parameter at +2 m
        Assert.Equal(0.4f, AvatarShape.From(rig, ShapeSkeletonTests.Def(), vp, AvatarShape.HoverOverride(false, 0.4f)).Hover);
        Assert.Equal(0f, AvatarShape.From(rig, ShapeSkeletonTests.Def(), vp, AvatarShape.HoverOverride(false, null)).Hover);
    }

    [Fact]
    public void The_own_avatars_hover_is_the_parameters_weight_whatever_the_message_says()
    {
        var rig = ShapeSkeletonTests.Rig();
        var vp = ShapeSkeletonTests.Wire((11001, 255));
        Assert.Equal(2f, AvatarShape.From(rig, ShapeSkeletonTests.Def(), vp, AvatarShape.HoverOverride(true, 0.4f)).Hover);
        Assert.Equal(2f, AvatarShape.From(rig, ShapeSkeletonTests.Def(), vp, AvatarShape.HoverOverride(true, null)).Hover);
    }

    [Fact]
    public void The_transport_reads_the_hover_block_from_the_packet()
    {
        var with = new AvatarAppearancePacket { Sender = new() { ID = new LibreMetaverse.UUID(Other) }, AppearanceHover = [new() { HoverHeight = new LibreMetaverse.Vector3(0f, 0f, 0.4f) }] };
        var e = LmvTransport.ToHover(Here, with);
        Assert.Equal(Other, e.AgentId);
        Assert.Equal(0.4f, e.Z);
        var without = new AvatarAppearancePacket { Sender = new() { ID = new LibreMetaverse.UUID(Other) }, AppearanceHover = [] };
        Assert.Null(LmvTransport.ToHover(Here, without).Z);
    }

    [Fact]
    public void The_hover_event_survives_a_capture_file_and_its_scrub()
    {
        var dir = Path.Combine(TestPaths.RepoRoot, "tools", "test-results", "capture-replay");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "hover.jsonl.gz");
        var aliases = new CaptureFile.Aliases(Guid.Empty);
        using (var w = new CaptureFile.Writer(path, new CaptureHeader(CaptureFile.FormatVersion, "hover", Guid.Empty, 0, 0, "test")))
        {
            w.Write(new CaptureRecord(1, null, 0, CaptureFile.Scrub(new AvatarHoverReceived(Here, Other, 0.4f), aliases)));
            w.Write(new CaptureRecord(2, null, 0, CaptureFile.Scrub(new AvatarHoverReceived(Here, Other, null), aliases)));
        }
        var records = CaptureFile.Read(path).Records;
        var first = Assert.IsType<AvatarHoverReceived>(records[0].Event);
        Assert.NotEqual(Other, first.AgentId);        // scrubbed to an alias
        Assert.Equal(0.4f, first.Z);
        Assert.Null(Assert.IsType<AvatarHoverReceived>(records[1].Event).Z);
        Assert.Equal(first.AgentId, ((AvatarHoverReceived)records[1].Event).AgentId);
    }
}
