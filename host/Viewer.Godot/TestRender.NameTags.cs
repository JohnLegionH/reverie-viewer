// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Godot.Presentation;
using Viewer.Shell;
using Viewer.Shell.NameTags;
using SNumerics = System.Numerics;

namespace Viewer.Godot;

public partial class TestRender
{
    /// <summary>
    /// Name tags on: four stand-in avatars of very different heights (a bar from the soles to the top of each body box, drawn on screen) with
    /// the tags the model plans for them, drawn by the overlay through the rig's camera. No avatar body, texture or character data is used, so the
    /// golden carries nothing of any viewer's content. Pixel checks (four tags drawn; each tag hangs above its stand-in's top, the taller the avatar the higher
    /// the tag) and the golden (--tr-golden=). The other render cases never draw tags.
    /// --tr-alter=notags (negative control): the mode is off, no tag is drawn, and the checks and the golden must fail.
    /// </summary>
    private async Task<int> NameTagRender(string[] args)
    {
        string? alter = Arg(args, "--tr-alter=");
        var rig = MakeRig(new Vector2I(640, 360));
        var cam = rig.Camera;
        cam.Fov = 70f; cam.Near = 0.1f; cam.Far = 200f;
        // the region's axes: X east, Y north, Z up; the engine's: x, z, -y
        var eye = new SNumerics.Vector3(128f, 100f, 4f);
        var target = new SNumerics.Vector3(128f, 110f, 4f);
        cam.Position = new Vector3(eye.X, eye.Z, -eye.Y);
        cam.LookAt(new Vector3(target.X, target.Z, -target.Y), Vector3.Up);

        var overlay = new NameTagOverlay { Name = "name_tags_case" };
        rig.Viewport.AddChild(overlay);
        var strings = StringTable.English();
        var settings = new NameTagSettings(alter == "notags" ? NameTagMode.Off : NameTagMode.Always, ShowSelf: true, GroupTitles: true, DisplayNames: true, UserNames: true, FriendColour: true);
        var model = new NameTagModel(() => settings, () => 0.0, k => strings.Get(k));

        (string Name, string? Display, string Title, float Height, float X, bool Friend, bool Blocked, Guid[] Anims)[] cast =
        [
            ("Short Person", null, "Builders", 1.0f, 119.5f, false, false, []),
            ("Default Resident", "Dizzy", "", 1.8f, 124.5f, true, false, []),
            ("Tall Person", null, "", 3.5f, 129.5f, false, false, [NameTagAnimations.Away, NameTagAnimations.Busy]),
            ("Giant Avatar", null, "Very Large Things", 8.0f, 135.5f, false, true, []),
        ];
        const float Soles = 0.5f, Y = 110f;
        var inputs = cast.Select((c, i) => new NameTagInput(Guid.Parse($"7a6e0001-0000-4000-8000-00000000000{i + 1}"), new AvatarNames(c.Name, c.Display), c.Title,
            new SNumerics.Vector3(c.X, Y, Soles + c.Height / 2), SNumerics.Quaternion.Identity, new SNumerics.Vector3(0.45f, 0.6f, c.Height), false, c.Friend, c.Blocked, c.Anims)).ToArray();
        var snapCam = new NameTagCamera(eye, SNumerics.Vector3.Normalize(target - eye), SNumerics.Vector3.UnitZ, cam.Near);
        var plans = model.Update(inputs, snapCam, false);

        // a stand-in bar for each body box: from the soles to the top, as wide as the box seen from here
        var bars = new List<ColorRect>();
        Vector2 Project(float x, float z) => cam.UnprojectPosition(new Vector3(x, z, -Y));
        foreach (var c in cast)
        {
            var feet = Project(c.X, Soles);
            var top = Project(c.X, Soles + c.Height);
            var side = Project(c.X + 0.225f, Soles);
            float w = Math.Max(4f, 2f * Math.Abs(side.X - feet.X));
            var bar = new ColorRect { Color = new Color(0.55f, 0.45f, 0.40f), Position = new Vector2(feet.X - w / 2f, top.Y), Size = new Vector2(w, Math.Max(2f, feet.Y - top.Y)), MouseFilter = Control.MouseFilterEnum.Ignore };
            rig.Viewport.AddChild(bar);
            bars.Add(bar);
        }
        rig.Viewport.MoveChild(overlay, rig.Viewport.GetChildCount() - 1);   // the tags over the bars
        overlay.Show(plans, cam, new Vector2(640, 360));
        await Frames(6);
        overlay.Show(plans, cam, new Vector2(640, 360));
        await Frames(4);
        var img = rig.Texture.GetImage();
        img.Convert(Image.Format.Rgba8);
        img.SavePng(Path.Combine(_out, _tag + ".png"));

        _result["mode"] = "name-tags";
        var panels = overlay.GetChildren().OfType<PanelContainer>().Where(p => p.Visible).OrderBy(p => p.Position.X).ToList();
        _result["name_tags"] = new
        {
            planned = plans.Count, drawn = overlay.Shown, altered = alter,
            tag_bottoms = panels.Select(p => Math.Round(p.Position.Y + p.Size.Y, 1)).ToArray(),
            body_tops = bars.Select(b => Math.Round(b.Position.Y, 1)).ToArray(),
            anchors_z = plans.Select(p => Math.Round(p.Anchor.Z, 3)).ToArray(),
        };
        if (plans.Count != 4 || overlay.Shown != 4)
            _failures.Add($"expected four tags drawn, planned {plans.Count} drawn {overlay.Shown}");
        else
        {
            for (int i = 0; i < 4; i++)
            {
                float bottom = panels[i].Position.Y + panels[i].Size.Y;
                if (bottom > bars[i].Position.Y + 2f)
                    _failures.Add($"tag {i} hangs at {bottom:F0} px, below the top of its avatar at {bars[i].Position.Y:F0} px");
            }
            for (int i = 1; i < 4; i++)
                if (plans[i].Anchor.Z <= plans[i - 1].Anchor.Z)
                    _failures.Add("a taller avatar's tag is not higher than a shorter one's");
        }
        if (Arg(args, "--tr-golden=") is { } golden) DiffGolden(img, golden);
        overlay.QueueFree();
        foreach (var b in bars) b.QueueFree();
        await Frames(2);
        rig.Viewport.QueueFree();
        return _failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Name tags over avatars the viewer draws itself: the user's own avatar and another, each with a group title, through the world state,
    /// the avatar presenter (no character folder, so no body), the tag model and the overlay. Each avatar must show one tag holding its
    /// title and its name, and nothing else may hang a name over it: no engine label is left in the scene. The picture is for looking at;
    /// the checks are on the tags and the nodes.
    /// </summary>
    private async Task<int> NameTagAvatarsRender(string[] args)
    {
        var rig = MakeRig(new Vector2I(640, 360));
        var view = MakeView("server", rig);
        var world = new Viewer.Core.World.WorldState(new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "test-render"));
        world.BeginRegion(FixtureSim, new Viewer.Core.Session.RegionInfo("tags", 1000, 1000, 256, 256, true));
        var avatars = new AvatarPresenter(rig.Scenario, world, LiveSelf, null, null, null, null, () => 0);
        var presenter = new WorldPresenter(world, view, avatars: avatars);
        var size = new SNumerics.Vector3(0.45f, 0.6f, 1.9f);
        world.ApplyAvatar(FixtureSim, new(800, LiveSelf, "Test User", new(128, 128, 21), SNumerics.Quaternion.Identity, size, Title: "Builders"));
        world.ApplyAvatar(FixtureSim, new(901, LiveOther, "Example Avatar", new(131, 129, 21), SNumerics.Quaternion.Identity, size, Title: "Dancers"));
        var cam = rig.Camera;
        cam.Fov = 60f; cam.Near = 0.1f; cam.Far = 200f;
        cam.Position = new Vector3(124f, 22f, -128.5f);
        cam.LookAt(new Vector3(130f, 21.5f, -128.5f), Vector3.Up);
        presenter.Frame();
        await Frames(8);
        presenter.Frame();

        var overlay = new NameTagOverlay { Name = "name_tags_case" };
        rig.Viewport.AddChild(overlay);
        var strings = StringTable.English();
        var settings = new NameTagSettings(NameTagMode.Always, ShowSelf: true, GroupTitles: true, DisplayNames: true, UserNames: true, FriendColour: false);
        var model = new NameTagModel(() => settings, () => 0.0, k => strings.Get(k));
        var inputs = world.AvatarList().Select(a =>
        {
            var (position, rotation) = NameTagPose.Of(avatars.Scene, a);
            return new NameTagInput(a.AgentId, new AvatarNames(a.Name), a.Title, position, rotation, a.Scale, a.AgentId == LiveSelf, false, false, []);
        }).ToArray();
        var basis = cam.GlobalTransform.Basis;
        SNumerics.Vector3 Region(Vector3 v) => new(v.X, -v.Z, v.Y);
        var plans = model.Update(inputs, new NameTagCamera(Region(cam.GlobalPosition), Region(-basis.Z), Region(basis.Y), cam.Near), false);
        overlay.Show(plans, cam, new Vector2(640, 360));
        await Frames(6);
        overlay.Show(plans, cam, new Vector2(640, 360));
        await Frames(4);
        var img = rig.Texture.GetImage();
        img.Convert(Image.Format.Rgba8);
        img.SavePng(Path.Combine(_out, _tag + ".png"));

        int engineLabels = LabelsUnder(rig.Root);
        var list = world.AvatarList();
        _result["mode"] = "name-tags-avatars";
        _result["name_tags_avatars"] = new { avatars = list.Count, planned = plans.Count, drawn = overlay.Shown, engine_labels = engineLabels, lines = plans.Select(p => p.Lines.Select(l => l.Text).ToArray()).ToArray() };
        if (list.Count != 2) _failures.Add($"expected two avatars, the world holds {list.Count}");
        if (plans.Count != 2 || overlay.Shown != 2) _failures.Add($"expected one tag for each of two avatars, planned {plans.Count} drawn {overlay.Shown}");
        if (engineLabels != 0) _failures.Add($"{engineLabels} engine name label(s) hang over the avatars besides their tags");
        foreach (var p in plans)
        {
            var a = list.First(x => x.AgentId == p.AgentId);
            var texts = p.Lines.Select(l => l.Text).ToArray();
            if (!texts.SequenceEqual([a.Title, a.Name])) _failures.Add($"a tag reads [{string.Join(" | ", texts)}], expected [{a.Title} | {a.Name}]");
        }

        overlay.QueueFree();
        presenter.Dispose();
        view.Clear();
        world.Dispose();
        RemoveChild(rig.Viewport);
        rig.Viewport.Free();
        await Frames(1);
        return _failures.Count == 0 ? 0 : 1;
    }
}
