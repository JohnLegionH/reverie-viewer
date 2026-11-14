// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Avatars;
using Viewer.Core.World;
using Viewer.Godot.Presentation;
using Viewer.Shell;
using GVector3 = Godot.Vector3;

namespace Viewer.Godot;

public partial class TestRender
{
    /// <summary>
    /// The shape's morph targets on the default body, from synthetic region updates through the world state and the avatar presenter: four bare
    /// avatars side by side, seen from the front: a default female, the same with Body Fat and Torso Muscles at the top, a default male, and a male
    /// with Body Fat and Torso Muscles at the top. Pixel checks on the body's own colour, no golden: every avatar is drawn from a body of its own
    /// (its morphs applied), the heavy ones are broader at the belly than the default ones (by a margin worked out from the pictures), and the morphs
    /// leave the height alone (the skeleton is not changed by them).
    /// --tr-alter=nomorph (negative control): the heavy avatars carry the default parameters, so they are not broader and the checks must fail.
    /// </summary>
    private async Task<int> AvatarMorphLook(AvatarBody body, PreparedSkinnedSurface[] surfaces, string? alter)
    {
        if (body.Shape is null) { _failures.Add("the character folder gave no visual parameters"); return 1; }
        var rig = MakeRig(new Vector2I(960, 640));
        var world = new WorldState(new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "test-render"));
        world.BeginRegion(FixtureSim, new Viewer.Core.Session.RegionInfo("morph-look", 1000, 1000, 256, 256, true));
        double now = 0;
        var view = MakeView("server", rig);
        var self = Guid.Parse("5a4e0002-0000-4000-8000-000000000001");
        var agents = Enumerable.Range(1, 4).Select(i => Guid.Parse($"5a4e0002-0000-4000-8000-00000000001{i}")).ToArray();
        var avatars = new AvatarPresenter(rig.Scenario, world, self, Task.FromResult<AvatarPresenter.LoadedBody?>(new(body, surfaces)), null, null, null, () => now);
        var presenter = new WorldPresenter(world, view, avatars: avatars);
        var cam = rig.Camera;
        cam.Fov = 40f; cam.Near = 0.1f; cam.Far = 100f;
        cam.Position = new GVector3(134f, 21.1f, -128f);
        cam.LookAt(new GVector3(128f, 21.1f, -128f), GVector3.Up);

        VisualParamSet Wire(params (int Id, byte Value)[] v) => new(v.Select(x => x.Value), [.. v.Select(x => x.Id)]);
        bool morphed = alter != "nomorph";
        var params4 = new[]
        {
            Wire((80, 0)),
            morphed ? Wire((80, 0), (637, 255), (107, 255)) : Wire((80, 0)),
            Wire((80, 255)),
            morphed ? Wire((80, 255), (637, 255), (106, 255)) : Wire((80, 255)),
        };
        const float Soles = 20.0f;
        float[] ys = [125.75f, 127.25f, 128.75f, 130.25f];
        for (int i = 0; i < 4; i++)
        {
            var size = new System.Numerics.Vector3(0.45f, 0.6f, 1.8f);
            world.ApplyAvatar(FixtureSim, new((uint)(900 + i), agents[i], "Test User " + (i + 1), new(128, ys[i], Soles + 0.9f), System.Numerics.Quaternion.Identity, size));
            world.ApplyAppearance(FixtureSim, new AvatarAppearanceData(agents[i], [.. new Guid[45]], params4[i], 1, 1), self);
        }

        async Task<Image> Settle(Func<bool> ready, string what)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!ready() && sw.Elapsed < TimeSpan.FromSeconds(30)) { presenter.Frame(); await Frames(1); }
            if (!ready()) _failures.Add($"{what}: not settled in 30 s (avatars {avatars.Drawn}, morphed {avatars.Morphed}, hand-overs {avatars.MorphHandOvers})");
            presenter.Frame();
            await Frames(8);
            presenter.Frame();
            var img = rig.Texture.GetImage();
            img.Convert(Image.Format.Rgba8);
            return img;
        }
        var img = await Settle(() => avatars.Drawn == 4 && avatars.Morphed == 4, "four morphed avatars");
        img.SavePng(Path.Combine(_out, _tag + "-morph.png"));
        int morphedCount = avatars.Morphed;
        world.Kill(FixtureSim, [900, 901, 902, 903]);
        var empty = (await Settle(() => avatars.Drawn == 0, "empty scene")).GetData();

        int w = img.GetWidth(), h = img.GetHeight();
        var data = img.GetData();
        // the name labels are white text with a dark outline: find the lowest row with white text and take only the rows below it (the body is darker than 215 in some channel)
        int labelBottom = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (Math.Min(data[i], Math.Min(data[i + 1], data[i + 2])) > 215 && Math.Min(empty[i], Math.Min(empty[i + 1], empty[i + 2])) <= 215) labelBottom = Math.Max(labelBottom, y);
            }
        labelBottom += 8;
        bool Body(int x, int y)
        {
            if (y <= labelBottom) return false;
            int i = (y * w + x) * 4;
            return Math.Abs(data[i] - empty[i]) + Math.Abs(data[i + 1] - empty[i + 1]) + Math.Abs(data[i + 2] - empty[i + 2]) > 40;
        }
        // per avatar: the rows it covers and its breadth (body pixels in a row) at the belly, the median over the rows from 40 % to 50 % of its height down
        (int Top, int Bottom, int Count, int Belly, int Chest) Measure(int x0, int x1)
        {
            int top = int.MaxValue, bottom = -1, n = 0;
            var rows = new int[h];
            for (int y = 0; y < h; y++)
                for (int x = x0; x < x1; x++)
                    if (Body(x, y)) { n++; rows[y]++; top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
            if (bottom < 0) return (0, 0, 0, 0, 0);
            int Median(double a, double b)
            {
                var r = Enumerable.Range((int)(top + a * (bottom - top)), Math.Max(1, (int)((b - a) * (bottom - top)))).Select(y => rows[y]).Order().ToArray();
                return r[r.Length / 2];
            }
            return (top, bottom, n, Median(0.40, 0.50), Median(0.22, 0.30));
        }
        int q = w / 4;
        var ext = Enumerable.Range(0, 4).Select(i => Measure(i * q, (i + 1) * q)).ToArray();
        _result["mode"] = "avatars-morph-look";
        _result["avatars_morph_look"] = new
        {
            label_bottom = labelBottom, morphed = morphedCount, heights_px = ext.Select(e => e.Bottom - e.Top).ToArray(), belly_px = ext.Select(e => e.Belly).ToArray(), chest_px = ext.Select(e => e.Chest).ToArray(),
            body_pixels = ext.Select(e => e.Count).ToArray(), altered = alter,
        };
        if (ext.Any(e => e.Count < 500)) _failures.Add($"an avatar is not drawn in its own colour: {string.Join(", ", ext.Select(e => e.Count))} body pixels");
        else
        {
            if (morphedCount != 4) _failures.Add($"{morphedCount} of 4 avatars draw a body of their own");
            // the morphs do not change the height
            if (Math.Abs((ext[1].Bottom - ext[1].Top) - (ext[0].Bottom - ext[0].Top)) > 4) _failures.Add($"the heavy female's height differs from the default female's: {ext[1].Bottom - ext[1].Top} vs {ext[0].Bottom - ext[0].Top} px");
            if (Math.Abs((ext[3].Bottom - ext[3].Top) - (ext[2].Bottom - ext[2].Top)) > 4) _failures.Add($"the heavy male's height differs from the default male's: {ext[3].Bottom - ext[3].Top} vs {ext[2].Bottom - ext[2].Top} px");
            if (ext[1].Belly < ext[0].Belly * 1.08) _failures.Add($"the heavy female is not broader at the belly: {ext[1].Belly} vs {ext[0].Belly} px");
            if (ext[3].Belly < ext[2].Belly * 1.08) _failures.Add($"the heavy male is not broader at the belly: {ext[3].Belly} vs {ext[2].Belly} px");
        }
        presenter.Dispose();
        if (avatars.LiveResources != 0) _failures.Add($"{avatars.LiveResources} avatar resources live after the presenter went");
        view.Clear();
        if (view is Node n) { n.GetParent()?.RemoveChild(n); n.Free(); }
        world.Dispose();
        return _failures.Count == 0 ? 0 : 1;
    }
}
