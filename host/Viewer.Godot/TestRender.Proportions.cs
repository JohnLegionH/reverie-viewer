// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

public partial class TestRender
{
    /// <summary>The widest a drawn sphere's or cube's width and height may differ, as a fraction of its height (rounding of the silhouette's edge pixels only).</summary>
    private const double ProportionTolerance = 0.012;

    /// <summary>
    /// --tr-proportions=PCT:STEP[,STEP...] [--tr-alter=stretch]: the window's own 3D view, drawn as the viewer draws it (the root viewport, the viewer's vertical field of view,
    /// the window's content scale at PCT percent and no content-scale mode, as the shipping build has it), shows a sphere and a cube as a circle and a square.
    /// Each STEP is a window size (WxH), "maximized" or "fullscreen"; the steps run in order in one window, so a size after another is a resize. After each step
    /// the run draws the sphere alone and then the cube alone, dead ahead of the camera, and FAILS unless
    ///   - the picture's size equals the window's size in pixels,
    ///   - the camera's projection has a vertical field of view of 60 degrees and an aspect equal to the picture's width over its height,
    ///   - the sphere's silhouette is as wide as it is high and the cube's likewise, within <see cref="ProportionTolerance"/>.
    /// </summary>
    private async Task<int> ProportionsRun(string spec, string? alter)
    {
        var head = spec.Split(':', 2);
        if (head.Length != 2 || !int.TryParse(head[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pct) || pct < 50 || pct > 400)
            throw new ArgumentException("--tr-proportions must be PCT:STEP[,STEP...], e.g. 150:1280x720,1920x1080,maximized");
        var steps = head[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var window = GetWindow();
        window.ContentScaleMode = Window.ContentScaleModeEnum.Disabled;   // the shipping build never sets one
        window.ContentScaleSize = Vector2I.Zero;
        window.ContentScaleFactor = pct / 100f;
        window.Mode = Window.ModeEnum.Windowed;

        var stage = new Node3D { Name = "proportions" };
        if (alter == "stretch") stage.Scale = new Vector3(1.2f, 1f, 1f);   // the negative control: a picture stretched sideways, which the run MUST fail
        AddChild(stage);
        var environment = new WorldEnvironment
        {
            Environment = new global::Godot.Environment { BackgroundMode = global::Godot.Environment.BGMode.Color, BackgroundColor = Colors.Black, AmbientLightSource = global::Godot.Environment.AmbientSource.Disabled },
        };
        stage.AddChild(environment);
        var camera = new Camera3D { Current = true, Fov = CameraDefaults.VerticalFovDegrees, Near = 0.1f, Far = 100f };
        stage.AddChild(camera);
        var white = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = Colors.White };
        var sphere = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 96, Rings = 48, Material = white }, Position = new Vector3(0, 0, -2.2f) };
        var cube = new MeshInstance3D { Mesh = new BoxMesh { Size = Vector3.One, Material = white }, Position = new Vector3(0, 0, -2.2f) };
        stage.AddChild(sphere);
        stage.AddChild(cube);

        var results = new List<Dictionary<string, object?>>();
        var fails = new List<string>();
        foreach (var step in steps)
        {
            if (step == "maximized") window.Mode = Window.ModeEnum.Maximized;
            else if (step == "fullscreen") window.Mode = Window.ModeEnum.Fullscreen;
            else
            {
                var wh = step.Split('x');
                if (wh.Length != 2 || !int.TryParse(wh[0], NumberStyles.None, CultureInfo.InvariantCulture, out int w) || !int.TryParse(wh[1], NumberStyles.None, CultureInfo.InvariantCulture, out int h))
                    throw new ArgumentException("a step must be WxH, maximized or fullscreen: " + step);
                window.Mode = Window.ModeEnum.Windowed;
                window.Size = new Vector2I(w, h);
            }
            await Frames(8);   // the operating system applies the size, the engine resizes its viewport, a frame is drawn at the new size
            var rec = new Dictionary<string, object?> { ["step"] = step, ["window"] = $"{window.Size.X}x{window.Size.Y}", ["mode"] = window.Mode.ToString(), ["scale_percent"] = pct };

            sphere.Visible = true; cube.Visible = false;
            await Frames(3);
            var sphereImage = Snapshot();
            sphere.Visible = false; cube.Visible = true;
            await Frames(3);
            var cubeImage = Snapshot();
            sphere.Visible = true;

            int pw = sphereImage.GetWidth(), ph = sphereImage.GetHeight();
            var projection = camera.GetCameraProjection();
            double fovV = 2 * Math.Atan(1.0 / projection.Y.Y) * 180 / Math.PI;
            double aspect = projection.Y.Y / projection.X.X;
            rec["picture"] = $"{pw}x{ph}";
            rec["vertical_fov_degrees"] = Math.Round(fovV, 4);
            rec["projection_aspect"] = Math.Round(aspect, 5);
            rec["picture_aspect"] = Math.Round((double)pw / ph, 5);
            if (pw != window.Size.X || ph != window.Size.Y)
                fails.Add($"{step}: the picture is {pw}x{ph} but the window is {window.Size.X}x{window.Size.Y}");
            if (Math.Abs(fovV - CameraDefaults.VerticalFovDegrees) > 0.01)
                fails.Add($"{step}: the vertical field of view is {fovV:F3} degrees, not {CameraDefaults.VerticalFovDegrees}");
            if (Math.Abs(aspect - (double)pw / ph) > 0.001)
                fails.Add($"{step}: the projection's aspect is {aspect:F4} but the picture's is {(double)pw / ph:F4}");
            foreach (var (name, image) in new[] { ("sphere", sphereImage), ("cube", cubeImage) })
            {
                var box = BrightBox(image);
                if (box is not { } b)
                {
                    fails.Add($"{step}: the {name} is not in the picture");
                    continue;
                }
                double ratio = (double)b.Width / b.Height;
                rec[name] = $"{b.Width}x{b.Height} px";
                rec[name + "_width_over_height"] = Math.Round(ratio, 4);
                if (Math.Abs(ratio - 1.0) > ProportionTolerance || b.Height < ph / 5)
                    fails.Add($"{step}: the {name} is {b.Width}x{b.Height} px (width over height {ratio:F4})");
                if (name == "sphere" && step == steps[^1])
                {
                    Directory.CreateDirectory(_out);
                    image.SavePng(Path.Combine(_out, _tag + ".png"));
                }
            }
            results.Add(rec);
        }
        _result["proportions"] = new Dictionary<string, object?> { ["steps"] = results, ["problems"] = fails, ["tolerance"] = ProportionTolerance };
        foreach (var f in fails)
            _failures.Add("proportions: " + f);
        stage.QueueFree();
        return _failures.Count == 0 ? 0 : 1;
    }

    private ViewportTexture? _windowTexture;

    private Image Snapshot()
    {
        _windowTexture ??= GetViewport().GetTexture();   // read once and kept (ReadBackScanTests)
        var img = _windowTexture.GetImage();
        img.Convert(Image.Format.Rgba8);
        return img;
    }

    /// <summary>The bounding box of the pixels brighter than half, or null when there are none.</summary>
    private static (int Left, int Top, int Width, int Height)? BrightBox(Image img)
    {
        int w = img.GetWidth(), h = img.GetHeight();
        var data = img.GetData();
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                if (data[row + x * 4] < 128) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        return maxX < 0 ? null : (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
}
