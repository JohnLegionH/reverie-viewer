// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using Godot;
using Viewer.Core.World;
using Viewer.Godot.Presentation;

namespace Viewer.Godot;

public partial class TestRender
{
    /// <summary>The interaction scenes (--tr-interact=select|text, with "-hidden" on the end for the negative control): a grey box standing on the
    /// terrain fixture, either wrapped in the selection highlight (select) or carrying floating text (text). The hidden form leaves the highlight or
    /// the text out, so the picture must differ from the golden.</summary>
    private string? _interact;

    private void AddInteractionScene(Rig rig, WorldState world)
    {
        if (_interact is null)
            return;
        bool hidden = _interact.EndsWith("-hidden", StringComparison.Ordinal);
        string kind = hidden ? _interact[..^"-hidden".Length] : _interact;
        const float x = 128f, y = 128f;
        float ground = TerrainHeight(world, x, y);
        var size = new System.Numerics.Vector3(2f, 2f, 2f);
        var centre = new System.Numerics.Vector3(x, y, ground + 1.2f);
        var box = new MeshInstance3D
        {
            Name = "interact-box", Mesh = new BoxMesh { Size = new Vector3(size.X, size.Z, size.Y) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.55f, 0.58f) },
            Position = new Vector3(centre.X, centre.Z, -centre.Y),
        };
        rig.Root.AddChild(box);
        // the picture frames itself on the box, whatever the terrain's height: 7 m away, a little above, 60 degrees across
        var eye = centre + new System.Numerics.Vector3(4.5f, -5.5f, 2.5f);
        rig.Camera.Fov = 60f; rig.Camera.Near = 0.2f; rig.Camera.Far = 1000f;
        rig.Camera.Position = new Vector3(eye.X, eye.Z, -eye.Y);
        rig.Camera.LookAt(new Vector3(centre.X, centre.Z + 0.4f, -centre.Y), Vector3.Up);
        var prim = new ObjectState(new ObjectId(1, 1), Guid.Parse("00000001-0000-4000-8000-000000000001"), 0, null, null, centre, System.Numerics.Quaternion.Identity, size,
            default, default, default,
            Interaction: new PrimInteraction(0, 0, "Floating text\nsecond line", Viewer.Core.Interaction.HoverTextRules.Pack(255, 140, 0, 255)));
        int pieces = 0, shown = 0;
        if (kind == "select" && !hidden)
        {
            var outline = new SelectionOutline { Name = "interact-outline" };
            rig.Root.AddChild(outline);
            outline.Update([new OutlinedPrim(prim, centre, System.Numerics.Quaternion.Identity, true)], rig.Camera.Position, rig.Camera.Fov);
            pieces = outline.Pieces;
        }
        else if (kind == "text" && !hidden)
        {
            var text = new HoverTextLayer { Name = "interact-text" };
            rig.Root.AddChild(text);
            text.Update([new FloatingText(1, Viewer.Core.Interaction.HoverTextRules.Anchor(centre, size.Z), prim.Interaction!)], rig.Camera.Position, -rig.Camera.GlobalTransform.Basis.Z);
            shown = text.Shown;
        }
        _result["interact"] = new { kind = _interact, pieces, shown, ground, camera = new { rig.Camera.Position.X, rig.Camera.Position.Y, rig.Camera.Position.Z } };
        if (!hidden && ((kind == "select" && pieces == 0) || (kind == "text" && shown == 0)))
            _failures.Add("the interaction layer drew nothing");
    }
}
