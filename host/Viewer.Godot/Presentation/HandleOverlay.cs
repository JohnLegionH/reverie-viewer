// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Generic;
using Godot;
using Viewer.Shell.Build;

namespace Viewer.Godot.Presentation;

/// <summary>
/// Draws the edit tools' handles and grid: each <see cref="HandleLine"/> (region metres) becomes a thin band that faces the camera, a fixed
/// number of pixels wide, drawn over the scene (no depth test) so the handles show through the object they move, as the official viewer's do.
/// One mesh, rebuilt when the lines change. Main thread only.
/// </summary>
public partial class HandleOverlay : MeshInstance3D
{
    /// <summary>A line's width on screen, in pixels.</summary>
    public const float LinePixels = 2.5f;

    private readonly ImmediateMesh _mesh = new();

    public HandleOverlay()
    {
        Name = "edit-handles";
        Mesh = _mesh;
        CastShadow = ShadowCastingSetting.Off;
        MaterialOverride = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            NoDepthTest = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            RenderPriority = (int)Material.RenderPriorityMax,
        };
        // the handles are always on screen while shown: never culled by a bounding box that the mesh's own size would give
        ExtraCullMargin = 16384f;
    }

    /// <summary>Draws <paramref name="lines"/> as seen from <paramref name="camera"/> (engine coordinates), for a view <paramref name="viewHeight"/>
    /// pixels high with a vertical field of view of <paramref name="fovYDegrees"/>; no lines hides the overlay.</summary>
    public void Show(IReadOnlyList<HandleLine> lines, Vector3 camera, float fovYDegrees, float viewHeight)
    {
        _mesh.ClearSurfaces();
        Visible = lines.Count > 0;
        if (lines.Count == 0)
            return;
        float pixelAngle = Mathf.DegToRad(fovYDegrees) / Mathf.Max(1f, viewHeight);
        var quads = new List<(Vector3 V, Color C)>(lines.Count * 6);
        foreach (var l in lines)
        {
            var a = Engine(l.From);
            var b = Engine(l.To);
            var mid = (a + b) / 2;
            var along = b - a;
            if (along.LengthSquared() < 1e-12f)
                continue;
            var toCamera = camera - mid;
            var side = along.Cross(toCamera);
            if (side.LengthSquared() < 1e-12f)
                continue;
            float half = camera.DistanceTo(mid) * Mathf.Tan(pixelAngle * LinePixels) / 2f;
            side = side.Normalized() * half;
            var c = new Color(l.Colour.X, l.Colour.Y, l.Colour.Z, l.Colour.W);
            foreach (var v in new[] { a - side, a + side, b + side, a - side, b + side, b - side })
                quads.Add((v, c));
        }
        if (quads.Count == 0)
        {
            Visible = false;
            return;
        }
        _mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        foreach (var (v, c) in quads)
        {
            _mesh.SurfaceSetColor(c);
            _mesh.SurfaceAddVertex(v);
        }
        _mesh.SurfaceEnd();
    }

    /// <summary>A region point in the engine's axes (x east, y up, z south).</summary>
    public static Vector3 Engine(System.Numerics.Vector3 p) => new(p.X, p.Z, -p.Y);
}
