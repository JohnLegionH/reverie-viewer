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
    /// The shaped skeleton on the default body, from synthetic region updates through the world state and the avatar presenter: four bare
    /// avatars side by side, a default female, a default male, a tall male with long legs, and a default female hovering half a metre. Each
    /// stands on the same ground (the region's stated height is each one's own computed body height, as the grid is told it). Pixel checks on the
    /// body's own colour, no golden: the feet of the first three stand on one line, the tall one is as much taller than the female as its computed
    /// height says (within 15 %), the male is taller than the female (the male skeleton's longer neck), and the hovering one stands a half metre above the line.
    /// --tr-alter=noshape (negative control): the appearances carry no shape parameters, so the tall one is drawn as the female and the checks must fail.
    /// </summary>
    /// <summary>A picture is only taken once every drawn avatar's own body is the one its shape and hand pose ask for; otherwise the picture depends on how long
    /// the worker that makes the body took (<see cref="AvatarPresenter.BodiesPending"/>).</summary>
    private void ExpectBodiesSettled(AvatarPresenter avatars, string what)
    {
        if (avatars.BodiesPending != 0) _failures.Add($"{what}: {avatars.BodiesPending} avatar bodies were still being made when the picture was taken");
        if (!avatars.PosesCurrent) _failures.Add($"{what}: an avatar's pose was not yet the one for the clock when the picture was taken");
    }

    /// <summary>Runs frames until every drawn avatar's pose is the one for the current clock (a pose is worked out on a worker and handed over a frame or more later).</summary>
    private async Task SettlePoses(AvatarPresenter avatars, WorldPresenter presenter)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!avatars.PosesCurrent && sw.Elapsed < TimeSpan.FromSeconds(10)) { presenter.Frame(); await Frames(1); }
    }

    private async Task<int> AvatarShapeLook(AvatarBody body, PreparedSkinnedSurface[] surfaces, string? alter)
    {
        if (body.Shape is null) { _failures.Add("the character folder gave no visual parameters"); return 1; }
        var avatarRig = new AvatarRig(body.Skeleton);
        var rig = MakeRig(new Vector2I(960, 640));
        var world = new WorldState(new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "test-render"));
        world.BeginRegion(FixtureSim, new Viewer.Core.Session.RegionInfo("shape-look", 1000, 1000, 256, 256, true));
        double now = 0;
        var view = MakeView("server", rig);
        var self = Guid.Parse("5a4e0001-0000-4000-8000-000000000001");
        var agents = Enumerable.Range(1, 4).Select(i => Guid.Parse($"5a4e0001-0000-4000-8000-00000000001{i}")).ToArray();
        var avatars = new AvatarPresenter(rig.Scenario, world, self, Task.FromResult<AvatarPresenter.LoadedBody?>(new(body, surfaces)), null, null, null, () => now);
        var presenter = new WorldPresenter(world, view, avatars: avatars);
        var cam = rig.Camera;
        cam.Fov = 40f; cam.Near = 0.1f; cam.Far = 100f;
        cam.Position = new GVector3(134f, 21.1f, -128f);
        cam.LookAt(new GVector3(128f, 21.1f, -128f), GVector3.Up);

        VisualParamSet Wire(params (int Id, byte Value)[] v) => new(v.Select(x => x.Value), [.. v.Select(x => x.Id)]);
        bool shaped = alter != "noshape";
        var params4 = new[]
        {
            Wire((80, 0)),                                                                   // a default female
            Wire((80, 255)),                                                                 // a default male
            shaped ? Wire((33, 255), (80, 255), (692, 255)) : Wire((80, 0)),                 // tall: Height at the top, long legs, male
            shaped ? Wire((80, 0), (11001, 160)) : Wire((80, 0)),                            // hover of 0.51 m (byte 160 of -2..2)
        };
        var heights = params4.Select(p => AvatarShape.From(avatarRig, body.Shape, p).Measure(null)!.Value.Height).ToArray();
        const float Soles = 20.0f;
        float[] ys = [125.75f, 127.25f, 128.75f, 130.25f];
        for (int i = 0; i < 4; i++)
        {
            var size = new System.Numerics.Vector3(0.45f, 0.6f, heights[i]);
            world.ApplyAvatar(FixtureSim, new((uint)(800 + i), agents[i], "Test User " + (i + 1), new(128, ys[i], Soles + heights[i] / 2), System.Numerics.Quaternion.Identity, size));
            var faces = new Guid[45];
            world.ApplyAppearance(FixtureSim, new AvatarAppearanceData(agents[i], [.. faces], params4[i], 1, 1), self);
            if (i == 3 && shaped) world.ApplyHover(FixtureSim, agents[i], 0.51f);   // the hover another avatar draws is its message's AppearanceHover block
        }

        async Task<Image> Settle(Func<bool> ready, string what)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!ready() && sw.Elapsed < TimeSpan.FromSeconds(30)) { presenter.Frame(); await Frames(1); }
            if (!ready()) _failures.Add($"{what}: not settled in 30 s (avatars {avatars.Drawn})");
            presenter.Frame();
            await SettlePoses(avatars, presenter);
            await Frames(8);
            presenter.Frame();
            ExpectBodiesSettled(avatars, what);
            var img = rig.Texture.GetImage();
            img.Convert(Image.Format.Rgba8);
            return img;
        }
        var img = await Settle(() => avatars.Drawn == 4 && avatars.BodiesPending == 0, "four avatars");
        img.SavePng(Path.Combine(_out, _tag + "-shape.png"));
        world.Kill(FixtureSim, [800, 801, 802, 803]);
        var empty = (await Settle(() => avatars.Drawn == 0, "empty scene")).GetData();

        int w = img.GetWidth(), h = img.GetHeight();
        var data = img.GetData();
        // the body's own colour: differs from the empty scene and is warm (the skin tone: red well above blue; labels are white and dark)
        (int Top, int Bottom, int Count) Extent(int x0, int x1)
        {
            int top = int.MaxValue, bottom = -1, n = 0;
            for (int y = 0; y < h; y++)
                for (int x = x0; x < x1; x++)
                {
                    int i = (y * w + x) * 4;
                    int diff = Math.Abs(data[i] - empty[i]) + Math.Abs(data[i + 1] - empty[i + 1]) + Math.Abs(data[i + 2] - empty[i + 2]);
                    if (diff <= 40 || data[i] - data[i + 2] < 25) continue;
                    n++;
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            return (top, bottom, n);
        }
        int q = w / 4;
        var ext = Enumerable.Range(0, 4).Select(i => Extent(i * q, (i + 1) * q)).ToArray();
        int[] px = ext.Select(e => e.Bottom - e.Top).ToArray();
        _result["mode"] = "avatars-shape-look";
        _result["avatars_shape_look"] = new
        {
            computed_heights = heights.Select(x => Math.Round(x, 3)).ToArray(), pixel_heights = px, bottoms = ext.Select(e => e.Bottom).ToArray(), tops = ext.Select(e => e.Top).ToArray(),
            body_pixels = ext.Select(e => e.Count).ToArray(), altered = alter,
        };
        if (ext.Any(e => e.Count < 500)) _failures.Add($"an avatar is not drawn in its own colour: {string.Join(", ", ext.Select(e => e.Count))} body pixels");
        else
        {
            if (Math.Abs(ext[0].Bottom - ext[1].Bottom) > 4 || Math.Abs(ext[0].Bottom - ext[2].Bottom) > 4)
                _failures.Add($"the feet are not on one line: bottoms {ext[0].Bottom}, {ext[1].Bottom}, {ext[2].Bottom}");
            double expect = heights[2] / heights[0], got = (double)px[2] / px[0];
            if (Math.Abs(got / expect - 1) > 0.15) _failures.Add($"the tall avatar is {got:F3} times the female's drawn height, its computed height says {expect:F3}");
            if (got < 1.1) _failures.Add($"the tall avatar is only {got:F3} times the female's drawn height");
            if (px[1] <= px[0]) _failures.Add($"the male ({px[1]} px) is not taller than the female ({px[0]} px)");
            double pxPerMetre = px[0] / (double)heights[0];
            double lifted = (ext[0].Bottom - ext[3].Bottom) / pxPerMetre;
            if (Math.Abs(lifted - 0.51) > 0.12) _failures.Add($"the hovering avatar stands {lifted:F2} m above the line, its hover is 0.51 m");
        }
        presenter.Dispose();
        if (avatars.LiveResources != 0) _failures.Add($"{avatars.LiveResources} avatar resources live after the presenter went");
        view.Clear();
        if (view is Node n) { n.GetParent()?.RemoveChild(n); n.Free(); }
        world.Dispose();
        return _failures.Count == 0 ? 0 : 1;
    }
}
