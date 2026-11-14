// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.World;
using Viewer.Godot.Presentation;

namespace Viewer.Godot;

/// <summary>
/// --tr-skycycle: the five time-of-day skies (the region's clock, sunrise, midday, sunset, midnight) applied four times in a row
/// through the environment layer, a few frames drawn after each. Measured on both backends: the engine allocates a sky's memory at its
/// first use and applying any sky again allocates nothing, so after the first cycle the video memory must stay flat (within 8 KiB,
/// the allowance of the warm-up's repeat check); growth in a later cycle is a leak. Then a fresh sky (a new viewport and layer) four
/// times, each let go of before the next: the memory must be free as soon as the layer is cleared and the held-value queue drained,
/// without waiting for a collection (a texture handed to a material through an implicit variant is held until the collector runs).
/// </summary>
public partial class TestRender
{
    /// <summary>Video memory allowed to move between samples once the first cycle is done.</summary>
    private const long SkyCycleFlatBytes = 8192;

    /// <summary>The engine's counts with the held values drained and the garbage collected: what something still holds.</summary>
    private Res CollectedResources()
    {
        VariantDisposal.DrainAll("skycycle");
        for (int i = 0; i < 2; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
        }
        return Resources();
    }

    private async Task<int> SkyCycleRun()
    {
        var rig = MakeRig(new Vector2I(256, 256));
        await Frames(2);
        var layer = new EnvironmentLayer(rig.Scenario, rig.Environment, rig.Sun);
        var region = RegionEnvironment.Default;
        var choices = new[] { SkyChoice.Region, SkyChoice.Sunrise, SkyChoice.Midday, SkyChoice.Sunset, SkyChoice.Midnight };
        var start = CollectedResources();
        var samples = new List<object>();
        var afterEach = new List<long>();
        const int cycles = 4;
        for (int cycle = 0; cycle < cycles; cycle++)
            foreach (var choice in choices)
            {
                var frame = choice == SkyChoice.Region ? region.SkyAt(0.3) : region.SkyFor(choice, 0, out _);
                layer.Apply(region, frame, 0f, 256, 256);
                await Frames(3);
                var r = CollectedResources();
                afterEach.Add(r.VideoMem);
                samples.Add(new { cycle, sky = choice.ToString(), video_mem = r.VideoMem, buffer_mem = r.BufferMem, objects = r.Objects });
            }
        var sky = rig.World.Environment?.Sky;
        _result["skycycle"] = new
        {
            start_video_mem = start.VideoMem,
            sky_process_mode = sky?.ProcessMode.ToString(),
            sky_radiance_size = sky?.RadianceSize.ToString(),
            sky_material = sky?.SkyMaterial?.GetType().Name,
            samples,
            first_cycle_adds = afterEach[choices.Length - 1] - start.VideoMem,
            later_cycles_add = afterEach[^1] - afterEach[choices.Length - 1],
        };
        // flat after the first full cycle: every sample from then on within the allowance of the first cycle's last
        long reference = afterEach[choices.Length - 1];
        for (int k = choices.Length; k < afterEach.Count; k++)
            if (Math.Abs(afterEach[k] - reference) > SkyCycleFlatBytes)
                _failures.Add($"sky {k / choices.Length}.{choices[k % choices.Length]}: video memory {afterEach[k]}, after the first cycle {reference} (moved {afterEach[k] - reference} B, allowed {SkyCycleFlatBytes})");
        layer.Clear();
        RemoveChild(rig.Viewport);
        rig.Viewport.Free();
        await Frames(1);
        // the sky made new each time (a new viewport, a new sky object, all let go after): what the layer lets go of must be free at once. Counted
        // without a collection (only the held-value queue drained, as a frame does): a texture the layer's materials hold through an implicit
        // variant stays alive until the collector finds it, so each use added the profile texture's memory (198,144 B on Vulkan) until a collection.
        var lifetimes = new List<object>();
        var afterLife = new List<long>();
        for (int life = 0; life < 4; life++)
        {
            var rig2 = MakeRig(new Vector2I(256, 256));
            var layer2 = new EnvironmentLayer(rig2.Scenario, rig2.Environment, rig2.Sun);
            layer2.Apply(region, region.SkyAt(0.3), 0f, 256, 256);
            await Frames(3);
            layer2.Clear();
            RemoveChild(rig2.Viewport);
            rig2.Viewport.Free();
            await Frames(3);
            VariantDisposal.DrainAll("skycycle");
            long held = Resources().VideoMem;   // no collection
            afterLife.Add(held);
            lifetimes.Add(new { life, video_mem_without_collection = held });
        }
        _result["skycycle_lifetimes"] = new { lifetimes, video_mem_collected = CollectedResources().VideoMem };
        for (int life = 1; life < afterLife.Count; life++)
            if (afterLife[life] - afterLife[0] > SkyCycleFlatBytes)
                _failures.Add($"fresh sky {life}: video memory {afterLife[life]} after it was let go, {afterLife[0]} after the first (grew {afterLife[life] - afterLife[0]} B, allowed {SkyCycleFlatBytes}): something the layer let go of is still held");
        return _failures.Count == 0 ? 0 : 1;
    }
}
