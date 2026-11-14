// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System;
using System.Collections.Generic;
using Godot;
using Viewer.Core.World;
using Viewer.Godot.Presentation;

namespace Viewer.Godot;

/// <summary>
/// --tr-clouds=&lt;seconds&gt; draws the sky with the cloud layer, the scroll being what that many seconds at the frame's scroll rate come to
/// (a fixed scroll, so the picture is the same every run); --tr-cloud-probes then compares the picture's pixels with the colour the official
/// viewer shows there (<see cref="CloudLighting.ShownOverSky"/>; the core tests check it against an independent transcription of the cloud
/// shaders). --tr-alter=cloudsoff leaves the layer off while the probes still expect clouds: the negative control, which must fail.
/// </summary>
public partial class TestRender
{
    private bool _clouds, _cloudProbes;
    private string? _cloudSwitch;   // --tr-clouds-switch=off|offon: the clouds are switched off (then on again) while the view is up, as the Preferences setting does
    private int _cloudFrames;   // --tr-cloud-frames=N: the frame and render times over N frames (report only)
    private double _cloudScrollSeconds;

    private void ParseCloudArgs(string[] args)
    {
        _cloudProbes = Array.IndexOf(args, "--tr-cloud-probes") >= 0;
        _cloudFrames = int.TryParse(Arg(args, "--tr-cloud-frames="), out var cf) ? cf : 0;
        _cloudSwitch = Arg(args, "--tr-clouds-switch=");
        if (Arg(args, "--tr-clouds=") is { } s)
        {
            _clouds = true;
            _cloudScrollSeconds = double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The frame time and the engine's measured render times (CPU and GPU) over <see cref="_cloudFrames"/> frames of the picture as it stands; report only.</summary>
    private async System.Threading.Tasks.Task CloudFrameTimes(Viewport viewport)
    {
        var rid = viewport.GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(rid, true);
        await Frames(10);
        var frameMs = new List<double>(); var cpu = new List<double>(); var gpu = new List<double>();
        long last = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < _cloudFrames; i++)
        {
            await Frames(1);
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            frameMs.Add((now - last) * 1000.0 / System.Diagnostics.Stopwatch.Frequency); last = now;
            cpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeCpu(rid));
            gpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid));
        }
        _result["cloud_frames"] = new { frames = _cloudFrames, clouds_drawn = CloudsDrawn, frame_ms = Dist(frameMs), render_cpu_ms = Dist(cpu), render_gpu_ms = Dist(gpu) };
    }

    /// <summary>Switches the clouds as --tr-clouds-switch asks, after the scene is up with them drawn: "off" leaves them off; "offon" switches them off,
    /// lets frames pass, and switches them on again.</summary>
    private async System.Threading.Tasks.Task SwitchClouds(WorldPresenter presenter)
    {
        if (presenter.Environment is not { } environment || _cloudSwitch is not ("off" or "offon"))
            return;
        environment.CloudsEnabled = false;
        presenter.Frame();   // the presenter applies the sky in its frame update, as the running viewer does each frame
        presenter.Frame();
        await Frames(4);
        if (_cloudSwitch == "offon")
        {
            environment.CloudsEnabled = true;
            presenter.Frame();
            presenter.Frame();
            await Frames(4);
        }
    }

    /// <summary>Whether this render draws the cloud layer (the negative control leaves it off).</summary>
    private bool CloudsDrawn => _clouds && _lifecycleAlter != "cloudsoff";

    /// <summary>
    /// A grid of pixels above the horizon (not near the light's disc), each against the official colour for its view direction. The pass rule:
    /// at least 8 probes on cloud (opacity over 0.3) and 8 on clear sky, 90 percent of the probes within 0.03 on every channel, and the mean
    /// absolute difference under 0.012. Both groups are reported with their own means.
    /// </summary>
    private void CloudProbes(Image img, Camera3D cam, SkyFrame? frame)
    {
        if (frame is null) { _failures.Add("cloud probes: no environment frame"); return; }
        var scroll = CloudScroll.Advance(default, frame.Cloud.ScrollRate, _cloudScrollSeconds);
        var noise = CloudNoise.Default;
        int w = img.GetWidth(), h = img.GetHeight();
        int total = 0, within = 0, cloudN = 0, clearN = 0;
        double sumAll = 0, sumCloud = 0, sumClear = 0, sumSkyOnlyCloud = 0;
        var worst = new List<object>();
        for (int y = 6; y < h - 6; y += 18)
        for (int x = 6; x < w - 6; x += 22)
        {
            var n = cam.ProjectRayNormal(new Vector2(x, y));
            double elev = Math.Asin(Math.Clamp(n.Y, -1f, 1f)) * 180.0 / Math.PI;
            if (elev < 6.0) continue;
            if (AngleToLight(frame, n) < 6.0) continue;
            // region axes: x east, y north (minus the engine's z), z up
            var enu = new System.Numerics.Vector3(n.X, -n.Z, n.Y);
            var want = SkyLighting.ToDisplay(CloudLighting.ShownOverSky(frame, enu, scroll, noise));
            var pixel = img.GetPixel(x, y);
            double[] d = [Math.Abs(pixel.R - want.X), Math.Abs(pixel.G - want.Y), Math.Abs(pixel.B - want.Z)];
            double mean = (d[0] + d[1] + d[2]) / 3, max = Math.Max(d[0], Math.Max(d[1], d[2]));
            var alpha = CloudLighting.Evaluate(frame, enu, scroll, noise).Alpha;
            var skyOnly = SkyLighting.ToDisplay(SkyLighting.DomeColour(frame, (float)elev, CloudLighting.AzimuthFromLight(frame, enu)));
            total++; sumAll += mean;
            if (max <= 0.03) within++;
            else if (worst.Count < 6) worst.Add(new { x, y, elevation = Math.Round(elev, 1), alpha = Math.Round(alpha, 2), got = new[] { pixel.R, pixel.G, pixel.B }, want = new[] { want.X, want.Y, want.Z } });
            if (alpha > 0.3) { cloudN++; sumCloud += mean; sumSkyOnlyCloud += (Math.Abs(skyOnly.X - want.X) + Math.Abs(skyOnly.Y - want.Y) + Math.Abs(skyOnly.Z - want.Z)) / 3; }
            else if (alpha == 0) { clearN++; sumClear += mean; }
        }
        double meanAll = total > 0 ? sumAll / total : 1;
        bool pass = cloudN >= 8 && clearN >= 8 && within >= 0.9 * total && meanAll < 0.012;
        _result["cloud_probes"] = new
        {
            probes = total, within_0_03 = within, on_cloud = cloudN, on_clear_sky = clearN, mean_abs_diff = Math.Round(meanAll, 5),
            mean_on_cloud = cloudN > 0 ? Math.Round(sumCloud / cloudN, 5) : -1.0, mean_on_clear = clearN > 0 ? Math.Round(sumClear / clearN, 5) : -1.0,
            cloud_vs_sky_only = cloudN > 0 ? Math.Round(sumSkyOnlyCloud / cloudN, 4) : -1.0, scroll_seconds = _cloudScrollSeconds, worst, pass,
        };
        if (!pass) _failures.Add($"cloud probes: {within} of {total} within 0.03, {cloudN} on cloud, {clearN} on clear sky, mean difference {meanAll:0.0000}");
    }
}
