// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Appearance;
using Viewer.Core.Avatars;
using Viewer.Core.Inventory;
using Viewer.Core.World;
using Viewer.Godot.Presentation;
using Viewer.Shell;
using GVector3 = Godot.Vector3;

namespace Viewer.Godot;

public partial class TestRender
{
    /// <summary>
    /// The own avatar before and after a body-height change made through the wearable editor, on the default body: the avatar is drawn with the
    /// look the simulator sent, the Height slider of a shape wearable is moved to the top in <see cref="WearableEditor"/>, the edited weights
    /// become the avatar's visual parameters through <see cref="PreviewParams"/> and the world's local preview (the path the Appearance window
    /// uses), the avatar is drawn again, and then the edit is thrown away (Don't save) and it is drawn a third time. Pixel checks on the body's own
    /// colour: the second picture is as much taller as the computed heights say (within 15 %) and at least half as much taller as the computed heights say, the third is the first
    /// again (within 4 px). The first two pictures are put side by side in one image and compared with the golden when one is given.
    /// --tr-alter=nopreview (negative control): the edit is never handed to the avatar, so it does not grow and the checks must fail.
    /// </summary>
    private async Task<int> AvatarEditLook(AvatarBody body, PreparedSkinnedSurface[] surfaces, string characterFolder, string? golden, string? alter)
    {
        if (body.Shape is null) { _failures.Add("the character folder gave no visual parameters"); return 1; }
        var catalog = EditParamCatalog.Parse(File.ReadAllBytes(Path.Combine(characterFolder, "avatar_lad.xml")));
        var avatarRig = new AvatarRig(body.Skeleton);
        var rig = MakeRig(new Vector2I(960, 640));
        var world = new WorldState(new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "test-render"));
        world.BeginRegion(FixtureSim, new Viewer.Core.Session.RegionInfo("edit-look", 1000, 1000, 256, 256, true));
        double now = 0;
        var view = MakeView("server", rig);
        var self = Guid.Parse("5a4e0003-0000-4000-8000-000000000001");
        var avatars = new AvatarPresenter(rig.Scenario, world, self, Task.FromResult<AvatarPresenter.LoadedBody?>(new(body, surfaces)), null, null, null, () => now);
        var presenter = new WorldPresenter(world, view, avatars: avatars);
        var cam = rig.Camera;
        cam.Fov = 40f; cam.Near = 0.1f; cam.Far = 100f;
        cam.Position = new GVector3(134f, 21.1f, -128f);
        cam.LookAt(new GVector3(128f, 21.1f, -128f), GVector3.Up);

        VisualParamSet Wire(params (int Id, byte Value)[] v) => new(v.Select(x => x.Value), [.. v.Select(x => x.Id)]);
        // a default female whose look carries the Height parameter (halfway up its range is the default)
        var sent = Wire((33, PreviewParams.ByteOf(0f, -1f, 1f)), (80, 0));
        float Height(VisualParamSet p) => AvatarShape.From(avatarRig, body.Shape, p).Measure(null)!.Value.Height;
        const float Soles = 20.0f;
        float h0 = Height(sent);
        world.ApplyAvatar(FixtureSim, new(800, self, "Test User", new(128, 128, Soles + h0 / 2), System.Numerics.Quaternion.Identity, new System.Numerics.Vector3(0.45f, 0.6f, h0)));
        world.ApplyAppearance(FixtureSim, new AvatarAppearanceData(self, [.. new Guid[45]], sent, 1, 1), self);

        async Task<Image> Settle(Func<bool> ready, string what)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!ready() && sw.Elapsed < TimeSpan.FromSeconds(30)) { presenter.Frame(); await Frames(1); }
            if (!ready()) _failures.Add($"{what}: not settled in 30 s (avatars {avatars.Drawn}, morphed {avatars.Morphed})");
            presenter.Frame();
            await Frames(8);
            presenter.Frame();
            var img = rig.Texture.GetImage();
            img.Convert(Image.Format.Rgba8);
            return img;
        }
        var before = await Settle(() => avatars.Drawn == 1 && avatars.Morphed == 1, "the avatar as sent");

        // the edit, through the editor and the preview path
        var shape = new WearableAsset { Name = "Example shape", Type = WearableType.Shape, PermissionsLines = ["\tpermissions 0", "\t{", "\t}"], SaleInfoLines = ["\tsale_info\t0", "\t{", "\t}"] };
        shape.Params[33] = 0f;
        var editor = new WearableEditor(shape, catalog, true);
        editor.SetValue(33, 1f);
        var edited = PreviewParams.Apply(sent, catalog, editor.Values);
        if (alter != "nopreview")
            world.SetSelfPreview(self, edited);
        float h1 = Height(edited);
        // The avatar keeps its old morphed body until the new one, made off the main thread, is handed over, so "morphed" alone is true
        // before the edit shows: wait for a hand-over after the edit (none comes in the negative control, which hands the avatar nothing).
        int shapeVersionBefore = avatars.Shaped;
        int handOversAtEdit = avatars.MorphHandOvers;
        var after = await Settle(() => avatars.Drawn == 1 && avatars.Morphed == 1
            && (alter == "nopreview" || (world.DrawnVisualParams(self)!.SameAs(edited) && avatars.MorphHandOvers > handOversAtEdit)), "the avatar with the edit");
        await Frames(10);
        presenter.Frame();
        after = rig.Texture.GetImage();
        after.Convert(Image.Format.Rgba8);

        // Don't save: the edit is thrown away and the avatar goes back to what was sent
        editor.Revert();
        world.SetSelfPreview(self, null);
        int handOversAtRevert = avatars.MorphHandOvers;
        var back = await Settle(() => avatars.Drawn == 1 && world.DrawnVisualParams(self)!.SameAs(sent)
            && (alter == "nopreview" || avatars.MorphHandOvers > handOversAtRevert), "the avatar after the edit was thrown away");
        await Frames(10);
        presenter.Frame();
        back = rig.Texture.GetImage();
        back.Convert(Image.Format.Rgba8);

        world.Kill(FixtureSim, [800]);
        var empty = (await Settle(() => avatars.Drawn == 0, "empty scene")).GetData();

        int w = before.GetWidth(), h = before.GetHeight();
        (int Top, int Bottom, int Count) Extent(Image img)
        {
            var data = img.GetData();
            int top = int.MaxValue, bottom = -1, n = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    int diff = Math.Abs(data[i] - empty[i]) + Math.Abs(data[i + 1] - empty[i + 1]) + Math.Abs(data[i + 2] - empty[i + 2]);
                    if (diff <= 40 || data[i] - data[i + 2] < 25) continue;   // the body's own colour: differs from the empty scene and is warm
                    n++;
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            return (top, bottom, n);
        }
        var e0 = Extent(before);
        var e1 = Extent(after);
        var e2 = Extent(back);
        int p0 = e0.Bottom - e0.Top, p1 = e1.Bottom - e1.Top, p2 = e2.Bottom - e2.Top;

        // the first two pictures side by side
        var both = Image.CreateEmpty(w * 2, h, false, Image.Format.Rgba8);
        both.BlitRect(before, new Rect2I(0, 0, w, h), new Vector2I(0, 0));
        both.BlitRect(after, new Rect2I(0, 0, w, h), new Vector2I(w, 0));
        Directory.CreateDirectory(_out);
        both.SavePng(Path.Combine(_out, _tag + "-edit.png"));
        back.SavePng(Path.Combine(_out, _tag + "-edit-back.png"));

        _result["mode"] = "avatars-edit-look";
        _result["avatars_edit_look"] = new
        {
            computed_heights = new[] { Math.Round(h0, 3), Math.Round(h1, 3) }, pixel_heights = new[] { p0, p1, p2 }, body_pixels = new[] { e0.Count, e1.Count, e2.Count },
            tops = new[] { e0.Top, e1.Top, e2.Top }, bottoms = new[] { e0.Bottom, e1.Bottom, e2.Bottom }, shaped_before = shapeVersionBefore, altered = alter,
        };
        if (new[] { e0.Count, e1.Count, e2.Count }.Any(c => c < 500)) _failures.Add($"the avatar is not drawn in its own colour: {e0.Count}, {e1.Count}, {e2.Count} body pixels");
        else
        {
            double expect = h1 / (double)h0, got = p1 / (double)p0;
            if (Math.Abs(got / expect - 1) > 0.15) _failures.Add($"the edited avatar is {got:F3} times its drawn height, its computed height says {expect:F3}");
            if (expect < 1.03) _failures.Add($"the edit changes the computed height by only {expect:F3} times");
            if (got < 1.0 + (expect - 1.0) * 0.5) _failures.Add($"the edited avatar is only {got:F3} times its drawn height");
            if (Math.Abs(p2 - p0) > 4) _failures.Add($"after the edit was thrown away the avatar is {p2} px, it was {p0} px");
        }
        if (golden is not null)
        {
            if (!Path.IsPathFullyQualified(golden) || !File.Exists(golden)) _failures.Add("golden not found: " + Path.GetFileName(golden));
            else
            {
                var g = Image.LoadFromFile(golden);
                g.Convert(Image.Format.Rgba8);
                var diff = g.GetWidth() != both.GetWidth() || g.GetHeight() != both.GetHeight()
                    ? new DiffResult(false, g.GetWidth(), g.GetHeight(), 0, 0, 0, 0, "golden size differs")
                    : ImageDiff.Compare(both.GetData(), g.GetData(), both.GetWidth(), both.GetHeight(), 4, DiffTolerance.Terrain);
                _result["diff"] = new Dictionary<string, object?>
                {
                    ["golden"] = Path.GetFileName(golden), ["pass"] = diff.Pass, ["differing"] = diff.Differing, ["differing_fraction"] = diff.DifferingFraction,
                    ["mean_abs_error"] = Math.Round(diff.MeanAbsError, 4), ["max_channel_error"] = diff.MaxChannelError, ["tolerance"] = DiffTolerance.Terrain, ["summary"] = diff.ToString(),
                };
                if (!diff.Pass) _failures.Add("diff " + diff);
            }
        }
        presenter.Dispose();
        if (avatars.LiveResources != 0) _failures.Add($"{avatars.LiveResources} avatar resources live after the presenter went");
        view.Clear();
        if (view is Node n) { n.GetParent()?.RemoveChild(n); n.Free(); }
        world.Dispose();
        return _failures.Count == 0 ? 0 : 1;
    }
}
