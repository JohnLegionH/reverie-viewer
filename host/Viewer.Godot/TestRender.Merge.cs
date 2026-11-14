// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Godot;
using Viewer.Godot.Presentation;

namespace Viewer.Godot;

/// <summary>The census and merge helpers the render runs share (PrimLayer.Merge.cs).</summary>
public partial class TestRender
{
    /// <summary>
    /// Frames run until nothing more will merge: no object is still waiting out its still time, no cell is being built, and at least nine
    /// tenths of the objects that can merge are merged (or 60 s have passed). A scene with nothing that can merge is settled as soon as
    /// that has held for 30 frames; the loop used to wait the whole 60 s for such a scene, because it asked for objects that could merge.
    /// </summary>
    private async Task MergeSettle(PrimLayer prims, Camera3D cam)
    {
        var settle = Stopwatch.StartNew();
        int quiet = 0;
        while (settle.Elapsed.TotalSeconds < 60 && quiet < 30)
        {
            prims.ViewCamera = cam;
            prims.UpdateBlendOrder();
            await Frames(1);
            quiet = prims.MergeWaitingObjects == 0 && prims.MergedObjects >= 0.9 * prims.MergeableObjects && !prims.MergeBusy ? quiet + 1 : 0;
        }
        _result["merge_settle_seconds"] = Math.Round(settle.Elapsed.TotalSeconds, 2);
    }

    /// <summary>The layer's census (kinds, materials, what merging leaves) beside the engine's own draw-call counts for the last frame drawn.</summary>
    private object CensusReport(PrimLayer prims, GlowPass glowPass, Rig rig, int terrainChunks)
    {
        var vp = rig.Viewport.GetViewportRid();
        return new
        {
            layer = prims.DrawCensus(rig.Camera, [16f, 32f, 64f]),
            engine = new
            {
                total_draw_calls = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame),
                view_visible_draw_calls = RenderingServer.ViewportGetRenderInfo(vp, RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.DrawCallsInFrame),
                view_shadow_draw_calls = RenderingServer.ViewportGetRenderInfo(vp, RenderingServer.ViewportRenderInfoType.Shadow, RenderingServer.ViewportRenderInfo.DrawCallsInFrame),
                glow_pass_draw_calls = glowPass.PassDrawCalls(),
                terrain_chunks = terrainChunks,
            },
        };
    }
}
