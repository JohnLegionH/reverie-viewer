// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.World;
using Viewer.Godot.Presentation;
using Vector3 = Godot.Vector3;

namespace Viewer.Godot;

/// <summary>
/// --tr-sundisc=on|off: the sun's disc drawn from the sky's sun picture. A picture that is white inside a circle across half the square (clear outside it, with a soft red rim) is given to
/// the sky as the sun's picture, the sun's scale is 3, and the camera looks along the sun's direction. The disc's width in pixels along the middle row must be the square's size
/// worked out from the official geometry (<see cref="SunDisc.Axes"/>: the body factor times the disk radius times the scale, grown towards the horizon), the centre must be much
/// brighter than the sky beside the disc, and no term of the light's strength may enter (the same picture under a sun
/// light of a tenth the strength is the same disc). "off" leaves the picture out (the procedural disc is drawn): the negative control, which must fail.
/// </summary>
public partial class TestRender
{
    /// <summary>Pixels the measured disc width may differ from the expected.</summary>
    private const int SunDiscWidthTolerance = 2;

    private async Task<int> SunDiscRun(string kind)
    {
        const int size = 256;
        const float fovDegrees = 20f, scale = 3f;
        // a dim sky, so that the sky near the sun is not already white and what the picture adds can be told from it
        var plain = SkinFrame(0.5f);
        var frame = plain with { SunScale = scale, Ambient = plain.Ambient * 0.2f, BlueHorizon = plain.BlueHorizon * 0.2f, BlueDensity = plain.BlueDensity * 0.2f };
        var rig = MakeRig(new Vector2I(size, size));
        rig.Camera.Fov = fovDegrees;
        // the sun's picture: 64 by 64, white and opaque inside a circle of radius 0.25 of its width, a half-clear red rim out to 0.31, clear outside
        const int pw = 64;
        var rgba = new byte[pw * pw * 4];
        for (int y = 0; y < pw; y++)
            for (int x = 0; x < pw; x++)
            {
                float dx = (x + 0.5f) / pw - 0.5f, dy = (y + 0.5f) / pw - 0.5f;
                float r = MathF.Sqrt(dx * dx + dy * dy);
                int o = (y * pw + x) * 4;
                if (r <= 0.25f) { rgba[o] = 255; rgba[o + 1] = 255; rgba[o + 2] = 255; rgba[o + 3] = 255; }
                else if (r <= 0.31f) { rgba[o] = 255; rgba[o + 1] = 90; rgba[o + 2] = 60; rgba[o + 3] = 128; }
            }
        var clear = new byte[4 * 4 * 4];   // a picture with no alpha anywhere
        var rows = new List<object>();
        float[] strengths = [1f, 0.1f];
        var centres = new List<float>();
        var layer = new EnvironmentLayer(rig.Scenario, rig.Environment, rig.Sun) { HdrEnabled = true };
        EnvironmentLayer.DynamicExposure = false;   // the exposure would scale the disc and the sky beside it alike; the sizes and the relation between them are what is checked here
        foreach (float strength in strengths)
        {
            var f = frame with { SunColor = frame.SunColor * strength };
            var look = new Vector3(f.SunDirection.X, f.SunDirection.Z, -f.SunDirection.Y);
            rig.Camera.Transform = new Transform3D(Basis.LookingAt(look, Vector3.Up), Vector3.Zero);
            const int mid = size / 2;
            // first the same view with a picture that is clear everywhere (no disc at all, procedural or other), then with the sun's picture: the difference between the two rows is
            // exactly what the picture adds, whatever the sky's own gradient near the sun is
            layer.Apply(RegionEnvironment.Default, f, null, 256, 256);
            if (kind == "on") layer.SetSunTexture(SunDisc.DefaultTextureId, clear, 4, 4);
            layer.Apply(RegionEnvironment.Default, f, null, 256, 256);
            await Frames(4);
            var baseline = rig.Texture.GetImage();
            if (kind == "on") layer.SetSunTexture(SunDisc.DefaultTextureId, rgba, pw, pw);
            layer.Apply(RegionEnvironment.Default, f, null, 256, 256);
            await Frames(4);
            var img = rig.Texture.GetImage();
            float Lum(Image i, int x, int y) { var c = i.GetPixel(x, y); return 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B; }
            float centre = Lum(img, mid, mid), beside = Lum(baseline, mid, mid);
            centres.Add(centre);
            int count = 0;
            for (int x = 0; x < size; x++) if (Lum(img, x, mid) - Lum(baseline, x, mid) > 0.02f) count++;
            var (_, _, halfAcross, _) = SunDisc.Axes(f.SunDirection, scale);
            float pixelsPerTan = size / 2f / MathF.Tan(Mathf.DegToRad(fovDegrees) / 2f);
            // the picture shows something out to 0.31 of its width from the middle; near the middle of the view tan and the distance on the square's plane agree
            int expected = (int)MathF.Round(2f * (0.31f * 2f * halfAcross) * pixelsPerTan);
            rows.Add(new { sun_light_strength = strength, centre, centre_without_picture = beside, disc_width_px = count, expected_width_px = expected });
            // at full strength and at a tenth of it the disc is the picture all the same: as wide as the official square gives, and bright at its centre (no term for the light's strength)
            if (centre < 0.8f) _failures.Add($"sun disc at light strength {strength}: the centre ({centre:F3}) is not bright (the same view without the picture: {beside:F3})");
            if (Math.Abs(count - expected) > SunDiscWidthTolerance) _failures.Add($"sun disc at light strength {strength}: its width is {count} px, expected {expected} px from the official square (allowed {SunDiscWidthTolerance})");
            if (strength == 1f) img.SavePng(System.IO.Path.Combine(_out, $"{_tag}.png"));
        }
        _result["sundisc"] = new { kind, tolerance_px = SunDiscWidthTolerance, texture_in_use = layer.SunTextureId?.ToString(), rows };
        EnvironmentLayer.DynamicExposure = true;
        layer.Clear();
        RemoveChild(rig.Viewport);
        rig.Viewport.Free();
        await Frames(1);
        return _failures.Count == 0 ? 0 : 1;
    }
}
