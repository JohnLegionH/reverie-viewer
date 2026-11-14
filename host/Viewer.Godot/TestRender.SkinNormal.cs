// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Meshing;
using Viewer.Godot.Presentation;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// --tr-skinnormal [--tr-alter=unscaled]: how the engine carries a skinned vertex's normal through a joint that a shape scales unevenly. The official avatar
/// shaders (avatarV.glsl and the deferred avatarV.glsl, with avatarSkinV.glsl's matrix blend) turn the normal by the blended joint matrix itself and then
/// normalise it; they do not use the matrix's inverse transpose. The body's own path is used: a quad made by <see cref="AvatarSurfaceData.Encode(MeshFace, int[], int[], float[], int, string)"/>,
/// handed over by <see cref="AvatarLayer.HandOver"/>, on a skeleton of one bone whose transform is set with SkeletonBoneSetTransform as the avatar layer sets its
/// palette. The quad's stored normal is (1, 1, 0) / sqrt 2 in the engine's axes and the bone scales y by 2.5, so the official rule gives a drawn normal whose y over
/// x is 2.5; an inverse transpose would give 0.4 and an unturned normal 1. An unshaded shader writes which of the three the drawn normal is (red, green, blue), and
/// the run FAILS unless the middle pixel is red. The negative control (--tr-alter=unscaled: the bone not scaled) must fail, which proves the reading can tell them apart.
/// </summary>
public partial class TestRender
{
    private const float SkinNormalScale = 2.5f;

    private const string SkinNormalShader = @"shader_type spatial;
render_mode unshaded, cull_disabled;
uniform int mode = 0;
void fragment() {
    // the camera has no turn, so the view's axes are the world's
    vec3 n = normalize(NORMAL);
    float a = atan(abs(n.y), abs(n.x)) / 1.5707963;   // 0 along x, 1 along y; y over x = 2.5 gives 0.758, 0.4 gives 0.242, 1 gives 0.5
    if (mode == 1) { ALBEDO = vec3(a); }
    else { ALBEDO = vec3(a > 0.625 ? 1.0 : 0.0, a < 0.375 ? 1.0 : 0.0, (a >= 0.375 && a <= 0.625) ? 1.0 : 0.0); }
}
";

    private async Task<int> SkinNormalRun(string? alter)
    {
        const int size = 64;
        var rig = MakeRig(new Vector2I(size, size));
        rig.Camera.Transform = Transform3D.Identity;   // at the origin, looking down -z
        rig.Camera.Fov = 60f;

        // the quad at engine z = -2, x and y in [-0.5, 0.5], given in SL axes (SL = (x, -z, y) of the engine's) as the body's parts are
        float s2 = MathF.Sqrt(0.5f);
        float[] positions = [-0.5f, 2f, -0.5f, 0.5f, 2f, -0.5f, 0.5f, 2f, 0.5f, -0.5f, 2f, 0.5f];
        float[] normals = [s2, 0f, s2, s2, 0f, s2, s2, 0f, s2, s2, 0f, s2];
        float[] uvs = [0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f];
        var face = new MeshFace(positions, normals, uvs, [0, 1, 2, 0, 2, 3]);
        var prepared = AvatarSurfaceData.Encode(face, [0, 0, 0, 0], [0, 0, 0, 0], [0f, 0f, 0f, 0f], 1, "skin normal quad");

        Rid mesh = Rids.MeshCreate("skin normal mesh");
        AvatarLayer.HandOver(mesh, prepared);
        Rid skeleton = Rids.SkeletonCreate("skin normal skeleton");
        RenderingServer.SkeletonAllocateData(skeleton, 1);
        float scale = alter == "unscaled" ? 1f : SkinNormalScale;
        RenderingServer.SkeletonBoneSetTransform(skeleton, 0, new Transform3D(Basis.FromScale(new Vector3(1f, scale, 1f)), Vector3.Zero));
        Rid instance = Rids.InstanceCreate2(mesh, rig.Scenario, "skin normal instance");
        RenderingServer.InstanceAttachSkeleton(instance, skeleton);
        RenderingServer.InstanceSetCustomAabb(instance, new Aabb(new Vector3(-4, -4, -4), new Vector3(8, 8, 8)));
        var material = new ShaderMaterial { Shader = new Shader { Code = SkinNormalShader } };
        RenderingServer.InstanceGeometrySetMaterialOverride(instance, material.GetRid());

        await Frames(4);
        var img = rig.Texture.GetImage();
        img.Convert(Image.Format.Rgba8);
        var px = img.GetPixel(size / 2, size / 2);
        string reading = px.R > 0.5f && px.G < 0.5f && px.B < 0.5f ? "forward" : px.G > 0.5f && px.R < 0.5f && px.B < 0.5f ? "inverse-transpose" : px.B > 0.5f && px.R < 0.5f && px.G < 0.5f ? "unturned" : "none";

        material.SetShaderParameter("mode", 1);
        await Frames(3);
        var grey = rig.Texture.GetImage();
        grey.Convert(Image.Format.Rgba8);
        int level = (int)MathF.Round(grey.GetPixel(size / 2, size / 2).R * 255f);
        double a = Decode8(level);   // the shader's value, as linear, encoded for the 8-bit picture
        double ratio = Math.Tan(a * Math.PI / 2);

        _result["skin_normal"] = new Dictionary<string, object?>
        {
            ["bone_scale_y"] = scale,
            ["reading"] = reading,
            ["drawn_normal_y_over_x"] = Math.Round(ratio, 3),
            ["official_y_over_x"] = scale,
            ["inverse_transpose_y_over_x"] = Math.Round(1.0 / scale, 3),
            ["pixel"] = new[] { (int)MathF.Round(px.R * 255f), (int)MathF.Round(px.G * 255f), (int)MathF.Round(px.B * 255f) },
        };
        if (reading != "forward")
            _failures.Add($"skin normal: the drawn normal reads '{reading}' (y over x about {ratio:F2}); the official rule, the joint matrix itself, gives {SkinNormalScale}");

        Rids.Free(instance);
        Rids.Free(skeleton);
        Rids.Free(mesh);
        rig.Viewport.QueueFree();
        return _failures.Count == 0 ? 0 : 1;
    }
}
