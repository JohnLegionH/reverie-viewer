// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Assets;
using Viewer.Core.Diagnostics;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Godot.Presentation;
using Viewer.Shell;

namespace Viewer.Godot;

public partial class TestRender
{
    /// <summary>
    /// --tr-trees=on|notrees: a few of the region's built-in trees of different species standing on the ground, as the viewer draws them (prims
    /// built from the species, see <see cref="TreeBuilder"/>). Checked: every prim of every tree is drawn (the layer's instance count equals the
    /// parts the builder made), the species texture is asked for and reaches the GPU with no face left waiting, and the picture holds tree-coloured
    /// pixels above the ground line. "missing" serves no texture, as a grid without it would: the trees are still all drawn, on the placeholder, and no face
    /// waits for ever. "notrees" skips the tree updates: the control, which must fail the same checks. The picture is compared with --tr-golden when one is given.
    /// </summary>
    private async Task<int> TreesRun(bool trees, bool served, string? golden, string? sharedEnv = null)
    {
        var rig = MakeRig();
        var log = new CoreLog(new DiscardSink(), "test-render");
        var homeSim = new SimRef(256000UL << 32 | 256000UL, "Home");
        var homeInfo = new RegionInfo("Home", 1000, 1000, 256, 256, true) { EnvironmentLlsd = sharedEnv is not null && _envFile is not null ? File.ReadAllText(_envFile) : null };
        var home = new WorldState(log);
        home.BeginRegion(homeSim, homeInfo);
        foreach (var (x, y, h) in Ground(0).Patches())
            home.ApplyPatch(homeSim, x, y, 16, h);

        // species 0 Pine 1, 1 Oak, 3 Palm 1, 14 Eucalyptus; scale 8 m per axis (the unit is the length of that vector, times 0.05)
        var species = new[] { 0, 1, 3, 14 };
        int expectedParts = 0;
        for (int i = 0; i < species.Length; i++)
        {
            float x = 120 + i * 26, y = 128;
            var tree = new TreeData(300u + (uint)i, Guid.NewGuid(), species[i], new System.Numerics.Vector3(x, y, (float)GroundHeight(x, y)),
                System.Numerics.Quaternion.Identity, new System.Numerics.Vector3(8, 8, 8));
            expectedParts += TreeBuilder.Build(tree).Count;
            if (trees)
                home.ApplyTree(homeSim, tree);
        }

        var homeView = MakeView("server", rig);
        // the species' texture is not on any grid here: one is made in memory, bark on the left half and, in the area the leaves take (the right
        // part of the upper half, as the official viewer's LEAF_LEFT to LEAF_TOP constants say), an opaque leaf shape with clear pixels round it,
        // so the leaves are drawn as leaves and not as bare squares
        var speciesTexture = LmvTexture.Encode(128, 128, TreeTexturePixels(128), true);
        // served=false (--tr-trees=missing): the source answers "not found" for it, as a grid without the texture would
        s_textureFetches.Clear();
        var noTextures = TextureSourceFromBytes(served
            ? species.Select(s => TreeBuilder.SpeciesOf(s).Texture).Distinct().ToDictionary(id => id, _ => speciesTexture)
            : new Dictionary<Guid, byte[]>());
        var treeMeshes = new MeshSource(new SessionMeshes(new SessionAssets(SharedAssets, _ => true, (id, _) => Task.FromResult(AssetFetchResult.Of(AssetFetchStatus.NotFound))), (id, _) => (null, null)));   // trees are made here; nothing is fetched
        var homePrims = new PrimLayer(rig.Scenario, home, treeMeshes, noTextures, _budget) { SimulatedFps = 60 };
        var homePresenter = new WorldPresenter(home, homeView, prims: homePrims,
            environment: new EnvironmentLayer(rig.Scenario, rig.Environment, rig.Sun) { HdrEnabled = sharedEnv is not null }) { DayFractionOverride = sharedEnv == "dusk" ? SharedEnvDay : sharedEnv is null ? 0.3 : null, Sky = sharedEnv == "classic" ? SkyChoice.Sunset : SkyChoice.Region };
        rig.Camera.LookAtFromPosition(new Vector3(100, (float)GroundHeight(100, 70) + 12f, -70), new Vector3(160, (float)GroundHeight(160, 128) + 6f, -128), Vector3.Up);
        if (sharedEnv is not null)
        {
            // the shared environment's dusk, seen the way the first picture of the fault was taken: the camera on the ground behind the trees, the sun low in front of it
            var sd = SharedEnvFrame(sharedEnv).SunDirection;
            float eyeX = sd.X > 0 ? 40f : 215f; const float eyeY = 152f;
            var eye = new Vector3(eyeX, (float)GroundHeight(eyeX, eyeY) + 3f, -eyeY);
            var toSun = new Vector3(sd.X, sd.Z, -sd.Y).Normalized();
            rig.Camera.Fov = 60f; rig.Camera.Near = 0.2f; rig.Camera.Far = 1000f;
            rig.Camera.LookAtFromPosition(eye, eye + toSun * 100f, Vector3.Up);
        }
        homePrims.Camera = rig.Camera.GlobalPosition;
        homePrims.ResidencyView = PrimLayer.ViewOf(rig.Camera, Width, Height);
        // the texture is fetched, decoded and uploaded as the layer is stepped, as the frame loop does (the flush alone never steps it): settle until
        // no face waits for data, with the settle's own cap, then draw a few frames
        await SettleMeshes(homePresenter, homePrims, treeMeshes, noTextures);
        homePresenter.Flush();
        await Frames(4);
        var img = rig.Texture.GetImage();
        img.Convert(Image.Format.Rgba8);
        Directory.CreateDirectory(_out);
        img.SavePng(Path.Combine(_out, _tag + ".png"));

        if (sharedEnv is not null) SharedEnvChecks(rig, img, sharedEnv);
        int above = AboveGroundLineNonSky(img.GetData(), Width, Height);
        _result["trees"] = new { on = trees, expected_parts = expectedParts, instances = homePrims.Instances, non_sky_pixels_above_ground_line = above, textures_waiting = homePrims.TexturesWaiting, gpu_textures = homePrims.GpuTextures, served, species_texture_fetches = s_textureFetches.Values.Sum() };
        if (homePrims.Instances != expectedParts)
            _failures.Add($"the trees were not all drawn: {homePrims.Instances} prims drawn, {expectedParts} built");
        if (trees && homePrims.TexturesWaiting > 0)
            _failures.Add($"faces still wait for the species texture: {homePrims.TexturesWaiting} faces waiting, {homePrims.GpuTextures} textures on the GPU");
        if (trees && s_textureFetches.Count == 0)
            _failures.Add("the species texture was never asked for");
        if (trees && served && homePrims.GpuTextures == 0)
            _failures.Add("the species texture did not reach the GPU: 0 textures on the GPU");
        if (trees && !served && homePrims.GpuTextures != 0)
            _failures.Add($"a texture the source does not have is on the GPU: {homePrims.GpuTextures}");
        if (above < 500)
            _failures.Add($"no trees in the picture: {above} non-sky pixels above the ground line");
        if (golden is not null)
        {
            if (!Path.IsPathFullyQualified(golden) || !File.Exists(golden))
                _failures.Add("golden not found: " + Path.GetFileName(golden));
            else
            {
                var g = Image.LoadFromFile(golden);
                g.Convert(Image.Format.Rgba8);
                var diff = g.GetWidth() != Width || g.GetHeight() != Height
                    ? new DiffResult(false, g.GetWidth(), g.GetHeight(), 0, 0, 0, 0, "golden size differs")
                    : ImageDiff.Compare(img.GetData(), g.GetData(), Width, Height, 4, DiffTolerance.Terrain);
                _result["diff"] = new Dictionary<string, object?>
                {
                    ["golden"] = Path.GetFileName(golden), ["pass"] = diff.Pass, ["differing"] = diff.Differing,
                    ["differing_fraction"] = diff.DifferingFraction, ["mean_abs_error"] = Math.Round(diff.MeanAbsError, 4),
                    ["max_channel_error"] = diff.MaxChannelError, ["tolerance"] = DiffTolerance.Terrain, ["summary"] = diff.ToString(),
                };
                if (!diff.Pass)
                    _failures.Add("diff " + diff);
            }
        }
        homePresenter.Dispose();
        homeView.Clear();
        home.Dispose();
        RemoveChild(rig.Viewport);
        rig.Viewport.Free();
        return _failures.Count == 0 ? 0 : 1;
    }

    /// <summary>The day fraction of the shared-environment case: near the end of the day, the sun just above the horizon.</summary>
    private const double SharedEnvDay = 0.02;

    /// <summary>
    /// What the shared-environment picture must show (--tr-sharedenv): the sun's disc brighter at its centre than the sky beside it, the leaves still green, and the far land not
    /// washed out. The numbers are reported whether or not they pass. The picture is RGBA8 and already shown (tone-mapped).
    /// </summary>
    private SkyFrame SharedEnvFrame(string mode) => mode == "classic" ? RegionEnvironment.DefaultCycle.SkyAt(0.75)
        : (RegionEnvironment.FromLlsd(File.ReadAllText(_envFile!), out _) ?? RegionEnvironment.Default).SkyAt(SharedEnvDay);

    private void SharedEnvChecks(Rig rig, Image img, string mode)
    {
        static double Lum(Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
        var f = SharedEnvFrame(mode);
        var sd = f.SunDirection;
        var camPos = rig.Camera.GlobalPosition;
        var at = camPos + new Vector3(sd.X, sd.Z, -sd.Y).Normalized() * 1000f;
        if (rig.Camera.IsPositionBehind(at)) { _failures.Add("shared environment: the sun is not in front of the camera"); return; }
        var px = rig.Camera.UnprojectPosition(at);
        int cx = (int)px.X, cy = (int)px.Y;
        double centre = 0;
        for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) centre += Lum(img.GetPixel(Math.Clamp(cx + dx, 0, Width - 1), Math.Clamp(cy + dy, 0, Height - 1))) / 9.0;
        // the sky beside the disc: a ring well outside it (the disc is about 20 px across here), upper half only (the land may be below the horizon)
        double ring = 0; int n = 0;
        for (int k = 0; k < 8; k++)
        {
            double a = Math.PI * (k + 0.5) / 8.0;
            int x = cx + (int)Math.Round(45 * Math.Cos(a)), y = cy - (int)Math.Round(45 * Math.Sin(a));
            if (x < 0 || y < 0 || x >= Width || y >= Height) continue;
            ring += Lum(img.GetPixel(x, y)); n++;
        }
        ring = n > 0 ? ring / n : 0;
        // the leaves: pixels clearly green; their share of green over the other two channels, and the far land: the row just below the horizon, away from the trees
        double sat = 0; int leaves = 0; double r = 0, g = 0, b = 0;
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
        {
            var c = img.GetPixel(x, y);
            if (c.G > c.R * 1.08f && c.G > c.B * 1.08f && c.G > 0.05f) { double mx = Math.Max(c.R, Math.Max(c.G, c.B)), mn = Math.Min(c.R, Math.Min(c.G, c.B)); sat += (mx - mn) / mx; r += c.R; g += c.G; b += c.B; leaves++; }
        }
        // the land far off and the land close by, on the line from the camera to the sun's side: the colour the picture holds there
        object LandAt(float x)
        {
            var world = new Vector3(x, (float)GroundHeight(x, 176f), -176f);
            if (rig.Camera.IsPositionBehind(world)) return "behind";
            var p = rig.Camera.UnprojectPosition(world);
            var c = img.GetPixel(Math.Clamp((int)p.X, 0, Width - 1), Math.Clamp((int)p.Y, 0, Height - 1));
            return new { x, pixel = new[] { (int)p.X, (int)p.Y }, rgb = new[] { (int)Math.Round(c.R * 255), (int)Math.Round(c.G * 255), (int)Math.Round(c.B * 255) } };
        }
        _result["shared_env"] = new
        {
            land_far = LandAt(f.SunDirection.X > 0 ? 240f : 15f), land_mid = LandAt(f.SunDirection.X > 0 ? 140f : 115f), land_near = LandAt(f.SunDirection.X > 0 ? 70f : 185f),
            sun_pixel = new[] { cx, cy }, sun_centre_luminance = Math.Round(centre, 4), sky_beside_luminance = Math.Round(ring, 4),
            leaf_pixels = leaves, leaf_saturation = leaves > 0 ? Math.Round(sat / leaves, 4) : 0, leaf_mean_rgb = leaves > 0 ? new[] { Math.Round(r / leaves, 4), Math.Round(g / leaves, 4), Math.Round(b / leaves, 4) } : Array.Empty<double>(),
            frame_sun = new[] { sd.X, sd.Y, sd.Z }, sun_colour = new[] { f.SunColor.X, f.SunColor.Y, f.SunColor.Z },
            haze = new { density = f.DensityMultiplier, distance = f.DistanceMultiplier, haze_density = f.HazeDensity, blue_density = new[] { f.BlueDensity.X, f.BlueDensity.Y, f.BlueDensity.Z } },
        };
        if (leaves > 0 && sat / leaves < 0.45)
            _failures.Add($"shared environment: the leaves have lost their colour (saturation {sat / leaves:F2}, at least 0.45 expected)");
        if (cx >= 0 && cy >= 0 && cx < Width && cy < Height && centre <= ring + 0.02)
            _failures.Add($"shared environment: the sun's disc is not brighter than the sky beside it (centre {centre:F3}, sky {ring:F3})");
    }

    /// <summary>How many pixels that are neither sky-coloured nor ground-coloured (light green and cream) lie in the picture: what a tree (dark green
    /// or grey placeholder leaves, brown or grey bark) adds to a scene of sky and ground. RGBA8 data.</summary>
    private static int AboveGroundLineNonSky(byte[] rgba, int width, int height)
    {
        int count = 0;
        for (int i = 0; i < width * height; i++)
        {
            byte r = rgba[i * 4], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2];
            bool sky = b > r + 40 && b > g + 10;
            bool ground = r >= 100 && g > 60 && b < g - 15;   // the light green of the grass ramp and the cream of the sand: green clear of blue and not dark (grey leaves are not, nor are dark green leaves or brown bark)
            if (!sky && !ground)
                count++;
        }
        return count;
    }

    /// <summary>RGBA pixels of a species texture made for the tree case: bark on the left half, a leaf shape (opaque, with clear pixels round it) in the upper right.</summary>
    private static byte[] TreeTexturePixels(int n)
    {
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float u = (x + 0.5f) / n, v = 1f - (y + 0.5f) / n;   // v up, as the texture coordinates are
                byte r, g, b, a = 255;
                if (u < 0.5f)
                    (r, g, b) = (96, 70, 44);   // bark
                else if (v >= 0.5f && u >= 0.52f && u <= 0.98f)
                {
                    // an ellipse filling the leaf area; outside it the pixel is clear
                    float cx = 0.75f, cy = 0.76f, dx = (u - cx) / 0.22f, dy = (v - cy) / 0.23f;
                    (r, g, b) = dx * dx + dy * dy <= 1f ? ((byte)54, (byte)128, (byte)46) : ((byte)0, (byte)0, (byte)0);
                    if (dx * dx + dy * dy > 1f) a = 0;
                }
                else
                {
                    (r, g, b) = (0, 0, 0);
                    a = 0;
                }
                int k = (y * n + x) * 4;
                px[k] = r; px[k + 1] = g; px[k + 2] = b; px[k + 3] = a;
            }
        return px;
    }
}
