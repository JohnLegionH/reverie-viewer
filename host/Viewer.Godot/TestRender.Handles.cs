// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Viewer.Godot.Presentation;
using Viewer.Shell.Build;
using NVector3 = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;

namespace Viewer.Godot;

/// <summary>
/// --tr-handles=move|rotate|scale|grid|snap: the edit tools' handles round a grey 1 x 2 x 0.5 m box, drawn by <see cref="HandleOverlay"/> from
/// <see cref="Manipulator"/>'s lines as the viewer draws them: Move (arrows and plane handles, the +x arrow lit as under the pointer), Rotate (the
/// three rings, the z ring lit), Stretch (the box with its corner and face boxes, the top north-east corner lit) and the grid (the Move handles
/// while the +x arrow is dragged, with the 0.5 m grid 2 m each way) and snap (the Move handles while the +x arrow is dragged flush against a second
/// box, with the guide <see cref="ObjectSnap"/> gives). Checks: the lit handle shows (yellow pixels), the +x arrow's tip lies where its
/// 50-pixel rule puts it on screen, and with the camera twice as far the arrow is still that many pixels long (a fixed size on screen); against the
/// golden when one is given. --tr-alter=nohandles draws the box alone: the negative control, which must fail.
/// </summary>
public partial class TestRender
{
    private async Task<int> HandlesRun(string mode, string? golden, string? alter)
    {
        var size = new Vector2I(640, 480);
        var rig = MakeRig(size);
        rig.Camera.Fov = 60f;
        var centre = new NVector3(10, 10, 30);
        var box = new NVector3(1, 2, 0.5f);
        var mesh = new MeshInstance3D
        {
            Name = "box",
            Mesh = new BoxMesh { Size = new Vector3(box.X, box.Z, box.Y) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.55f, 0.55f) },
            Position = HandleOverlay.Engine(centre),
        };
        rig.Root.AddChild(mesh);
        var overlay = new HandleOverlay();
        rig.Root.AddChild(overlay);

        var (manip, lit) = mode switch
        {
            "rotate" => (ManipMode.Rotate, new Handle(HandleKind.Ring, 2)),
            "scale" => (ManipMode.Scale, new Handle(HandleKind.Corner, Corner: 7)),
            "move" or "grid" or "snap" => (ManipMode.Move, new Handle(HandleKind.Axis, 0, 1)),
            _ => throw new ArgumentException("--tr-handles must be move, rotate, scale, grid or snap"),
        };

        // snap: a second box, and the guide of a drag that began 1 m short and has come to within 5 cm of flush
        var guides = new List<HandleLine>();
        if (mode == "snap")
        {
            var otherCentre = centre + new NVector3(1f, 0.3f, 0.25f);
            var other = new NVector3(1, 1, 1);
            rig.Root.AddChild(new MeshInstance3D
            {
                Name = "other",
                Mesh = new BoxMesh { Size = new Vector3(other.X, other.Z, other.Y) },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.45f, 0.6f) },
                Position = HandleOverlay.Engine(otherCentre),
            });
            var from = centre - NVector3.UnitX;
            var caught = ObjectSnap.Snap(new SnapBox(from - box / 2, from + box / 2), new NVector3(0.95f, 0, 0),
                [new SnapBox(otherCentre - other / 2, otherCentre + other / 2)], ObjectSnap.MaxDistance, letGo: false);
            if (caught.Guides.Count != 1 || Math.Abs(caught.Move.X - 1f) > 0.001f)
                _failures.Add($"the drag did not catch flush: move {caught.Move.X:F4} m with {caught.Guides.Count} guides");
            foreach (var g in caught.Guides)
            {
                // the dragged box where the catch leaves it; the margin is the viewer's, at the picture's distance
                var at = from + caught.Move;
                float margin = Manipulator.Metres(Manipulator.SnapGuideMarginPixels, centre,
                    new HandleView(centre + NVector3.Normalize(new NVector3(-0.6f, -0.8f, 0.45f)) * 8f, Mathf.DegToRad(rig.Camera.Fov), size.Y));
                guides.AddRange(Manipulator.SnapGuideDrawing(ObjectSnap.Outline(g, NQuaternion.Identity, new SnapBox(at - box / 2, at + box / 2), margin)));
            }
        }

        // a camera south-west of the box and above it, looking at it
        float PlaceCamera(float distance)
        {
            var dir = NVector3.Normalize(new NVector3(-0.6f, -0.8f, 0.45f));
            var eye = centre + dir * distance;
            rig.Camera.LookAtFromPosition(HandleOverlay.Engine(eye), HandleOverlay.Engine(centre), Vector3.Up);
            var view = new HandleView(eye, Mathf.DegToRad(rig.Camera.Fov), size.Y);
            var lines = new List<HandleLine>();
            if (alter != "nohandles")
            {
                lines.AddRange(Manipulator.Lines(manip, centre, NQuaternion.Identity, box, view, lit));
                if (mode == "grid")
                    lines.AddRange(Manipulator.GridLines(centre, NVector3.Zero, NQuaternion.Identity, 0.5f, 2f, 0.7f));
                lines.AddRange(guides);
            }
            overlay.Show(lines, rig.Camera.GlobalPosition, rig.Camera.Fov, size.Y);
            // the +x arrow's length on screen, from the centre to its tip at 1.2 arrow lengths
            var tip = centre + NVector3.UnitX * Manipulator.Metres(Manipulator.ArrowPixels, centre, view) * Manipulator.HotEnd;
            return rig.Camera.UnprojectPosition(HandleOverlay.Engine(tip)).DistanceTo(rig.Camera.UnprojectPosition(HandleOverlay.Engine(centre)));
        }

        float near = PlaceCamera(8f);
        await Frames(4);
        var img = rig.Texture.GetImage();
        img.Convert(Image.Format.Rgba8);
        int yellow = 0;
        for (int y = 0; y < size.Y; y++)
            for (int x = 0; x < size.X; x++)
            {
                var c = img.GetPixel(x, y);
                if (c.R > 0.85f && c.G > 0.85f && c.B < 0.6f) yellow++;   // the lit handle's yellow (1, 1, 0.2) reads back as about (1, 1, 0.49)
            }
        float far = PlaceCamera(16f);
        await Frames(2);
        // the arrow's on-screen length: the share of the field of view its 50 pixels take (tilted towards the camera it looks shorter, the same at both distances)
        if (yellow < 20) _failures.Add($"the lit handle does not show ({yellow} yellow pixels)");
        if (Math.Abs(near - far) > 1.5f) _failures.Add($"the arrow is {near:F1} px long at 8 m and {far:F1} px at 16 m: not a fixed size on screen");
        if (near > Manipulator.ArrowPixels * Manipulator.HotEnd + 1f || near < 10f) _failures.Add($"the arrow is {near:F1} px long, more than its {Manipulator.ArrowPixels * Manipulator.HotEnd} px");
        _result["handles"] = new Dictionary<string, object?>
        {
            ["mode"] = mode, ["lit_pixels"] = yellow, ["arrow_px_at_8m"] = Math.Round(near, 2), ["arrow_px_at_16m"] = Math.Round(far, 2), ["alter"] = alter,
        };
        // the picture is the one at 8 m
        PlaceCamera(8f);
        await Frames(4);
        SaveAndDiff(rig, size, golden);
        RemoveChild(rig.Viewport);
        rig.Viewport.Free();
        await Frames(1);
        return _failures.Count == 0 ? 0 : 1;
    }
}
