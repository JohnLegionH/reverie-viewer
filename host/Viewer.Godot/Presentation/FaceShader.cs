// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file llface.cpp
 * @brief LLFace class implementation
 *
 * $LicenseInfo:firstyear=2001&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2010, Linden Research, Inc.
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation;
 * version 2.1 of the License only.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 *
 * Linden Research, Inc., 945 Battery Street, San Francisco, CA  94111  USA
 * $/LicenseInfo$
 */

/**
 * @file pipeline.cpp
 * @brief Rendering pipeline.
 *
 * $LicenseInfo:firstyear=2005&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2010, Linden Research, Inc.
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation;
 * version 2.1 of the License only.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 *
 * Linden Research, Inc., 945 Battery Street, San Francisco, CA  94111  USA
 * $/LicenseInfo$
 */

/**
 * @file class2\windlight\atmosphericsFuncs.glsl
 *
 * $LicenseInfo:firstyear=2022&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2022, Linden Research, Inc.
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation;
 * version 2.1 of the License only.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 *
 * Linden Research, Inc., 945 Battery Street, San Francisco, CA  94111  USA
 * $/LicenseInfo$
 */

/**
 * @file class3/deferred/softenLightF.glsl
 *
 * $LicenseInfo:firstyear=2007&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2007, Linden Research, Inc.
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation;
 * version 2.1 of the License only.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 *
 * Linden Research, Inc., 945 Battery Street, San Francisco, CA  94111  USA
 * $/LicenseInfo$
 */

/**
 * @file class3/deferred/reflectionProbeF.glsl
 *
 * $LicenseInfo:firstyear=2022&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2022, Linden Research, Inc.
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation;
 * version 2.1 of the License only.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 *
 * Linden Research, Inc., 945 Battery Street, San Francisco, CA  94111  USA
 * $/LicenseInfo$
 */

/**
 * @file class1\environment\waterFogF.glsl
 *
 * $LicenseInfo:firstyear=2007&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2007, Linden Research, Inc.
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation;
 * version 2.1 of the License only.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 *
 * Linden Research, Inc., 945 Battery Street, San Francisco, CA  94111  USA
 * $/LicenseInfo$
 */

/* Adapted 2026 by Legion Builds: the LegacyBody shader code is adapted from the planar texture-coordinate generation in llface.cpp and the specular
 * normalisation (exponent 368) in pipeline.cpp, translated to C# and GLSL. The avatar shaders' lighting (ClassicGlobals, ClassicLight) is adapted from
 * calcAtmosphericVars and calcAtmosphericVarsLinear (atmosphericsFuncs.glsl) and the legacy branch of main in softenLightF.glsl (classic lighting), written in GLSL for the engine's light function.
 * The water fog on a blended face seen by a camera under the water (UnderwaterFace) follows applySkyAndWaterFog and getWaterFogViewNoClip of
 * indra/newview/app_settings/shaders/class1/environment/waterFogF.glsl (github.com/secondlife/viewer, commit e34b5c188e758b6d24b768cc44fbee459be2855c), with the
 * 1.1 its lit alpha shaders (alphaF.glsl, materialF.glsl, pbralphaF.glsl) give a classic sky. */

using System.Collections.Generic;
using Godot;

namespace Viewer.Godot.Presentation;

/// <summary>
/// The face shaders: the legacy SL face shader, generated in four variants (lit or fullbright, opaque or alpha-blended), all with
/// back faces culled as SL draws prims and meshes. Texture coordinates follow the official viewer (llface.cpp):
/// planar texgen = planarProjection on the object-SCALED position with the binormal chosen from the
/// normal; then xform: about the face centre, rotate, scale by the repeats, add the offset. SL's V runs up the image, so
/// sampling flips it. The face UVs are SL's (Docs/TEXTURE-ORIENTATION-SPEC.md) and the geometry is
/// uploaded in Godot's winding (MeshSurfaceData.Encode reverses every triangle; earlier, PrimLayer.EngineWinding did), verified end to end by the colour chart (render case colour_*:
/// the top face shows the chart upright seen from above, north up). Mesh vertices arrive in engine axes (x, z, -y) and are turned back to SL axes
/// for planar. And the PBR face shader (<see cref="CreatePbr"/>), single- or double-sided.
/// </summary>
public static class FaceShader
{
    private const string Body = @"
// Texture and tint are read as their raw sRGB values (no source_color hint) and multiplied in sRGB space, as
// SL composes them; the product is then converted to linear for Godot's lighting and output (checked by the colour chart).
uniform sampler2D tex : filter_linear_mipmap, repeat_enable;
uniform vec4 tint = vec4(1.0);
uniform vec2 rep = vec2(1.0);
uniform vec2 off = vec2(0.0);
uniform float rot = 0.0;
uniform bool planar = false;
uniform float glow_w = 0.0;
instance uniform vec3 obj_scale = vec3(1.0);
varying vec2 sl_uv;

void vertex() {
    vec2 tc = UV;
    if (planar) {
        vec3 p = vec3(VERTEX.x, -VERTEX.z, VERTEX.y) * obj_scale;
        vec3 n = normalize(vec3(NORMAL.x, -NORMAL.z, NORMAL.y));
        vec3 b;
        float d = n.x;
        if (d >= 0.5 || d <= -0.5) { b = d < 0.0 ? vec3(0.0, -1.0, 0.0) : vec3(0.0, 1.0, 0.0); }
        else { b = n.y > 0.0 ? vec3(-1.0, 0.0, 0.0) : vec3(1.0, 0.0, 0.0); }
        vec3 t = cross(b, n);
        tc = vec2(1.0 + (dot(b, p) * 2.0 - 0.5), -(dot(t, p) * 2.0 - 0.5));
    }
    float s = tc.x - 0.5;
    float u = tc.y - 0.5;
    float c = cos(rot);
    float sn = sin(rot);
    float ns = s * c + u * sn;
    float nt = -s * sn + u * c;
    sl_uv = vec2(ns * rep.x + off.x + 0.5, nt * rep.y + off.y + 0.5);
}

vec3 srgb_to_linear(vec3 c) {
    return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

void fragment() {
    vec4 texel = texture(tex, vec2(sl_uv.x, 1.0 - sl_uv.y));
    vec4 col = texel * tint;
    ALBEDO = srgb_to_linear(col.rgb);
    ROUGHNESS = 0.9;
    SPECULAR = 0.0;
    %ALPHA%
    if (" + GlowCamera + @") {
        // the glow picture (GlowPass): the face's lit colour times its glow weight and its texture's alpha, blended faces
        // as their blend would show (the share of the face's colour a blend adds is its alpha, so divide it out)
        ALBEDO *= glow_k(ALBEDO, glow_w * texel.a) / %DIV%;
    }
}
";


    // The official classic lighting for avatars (a sky without a reflection-probe ambiance: classic_mode in softenLightF.glsl): per pixel, the face's colour
    // times 1.1 * linear(ambient * ease(angle) + encode(angle^1.2) * sun), the sum taken in display encoding, instead of the engine's straight-line
    // fit of that response. The sky's constants (SkyLighting.Classic) arrive as global shader parameters: classic_amb (the ambient term), classic_sun (the
    // sun's term), classic_sun_lin (the sun colour as linear light, for the specular), classic_l0 (what the engine adds as ambient, taken off again so the
    // sum is the official one) and classic_on (0 until a sky is applied: the engine's own Lambert response; 1 the classic response; 2 the response of a sky that takes the tone-mapped path, where classic_amb is the ambient before its easing and classic_sun the sun as linear light).
    internal const string ClassicGlobals = @"
global uniform vec3 classic_amb;
global uniform vec3 classic_sun;
global uniform vec3 classic_sun_lin;
global uniform vec3 classic_l0;
global uniform float classic_on;
global uniform sampler2D probe_irr;
global uniform float probe_ambiance;
global uniform vec3 probe_sun;
varying float pr_capf;
varying vec3 pr_c0;
varying vec3 pr_c1;
varying vec3 pr_c2;
varying vec3 pr_cam;
varying vec3 pr_pos;

vec3 cl_enc(vec3 c) { return mix(c * 12.92, 1.055 * pow(max(c, vec3(0.0)), vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c)); }
vec3 cl_dec(vec3 c) { return mix(c / 12.92, pow((max(c, vec3(0.0)) + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c)); }

// the official diffuse factor of a face whose normal has dot product nl with the light, less the ambient the engine already added
vec3 cl_diffuse_extra(float nl, float shadow, vec3 wpos, vec3 wn, vec3 cam) {
    float ease = 1.0 - pow(min(abs(nl), 1.0) * 0.5, 2.0);
    if (classic_on > 1.5) {
        // the sky probe's capture (a view that sees only the capture layer): the sun alone, as the official irradiance pass lights the world; that view has no engine ambient to take off
        if (pr_capf > 0.5) return min(clamp(nl, 0.0, 1.0), shadow) * probe_sun;
        // a sky that takes the tone-mapped path (classic_mode 0): the ambient eased by the angle, in linear light and reduced to its luminance, plus the sun times the angle
        float grey = dot(cl_dec(classic_amb * ease), vec3(0.2126, 0.7152, 0.0722));
        vec3 amb = vec3(grey);
        if (probe_ambiance > 0.0) {
            // the sky probe's irradiance (sampleProbeAmbient / tapIrradianceMap in reflectionProbeF.glsl): read in the direction from the probe's centre (the camera, 64 m up)
            // to where the surface's normal meets a 4096 m sphere around it, mixed with the atmospheric ambient by the sky's ambiance
            vec3 centre = cam + vec3(0.0, 64.0, 0.0);
            vec3 l = centre - wpos;
            float tca = dot(l, wn);
            float d2 = dot(l, l) - tca * tca;
            vec3 v = wpos + wn * (tca + sqrt(max(4096.0 * 4096.0 - d2, 0.0))) - centre;
            // the map is one picture: six faces of 16 texels with a one-texel border each (SkyProbe.Atlas); pick the cube face of the direction, then the position on it
            vec3 av = abs(v);
            float face; float sc; float tc; float ma;
            if (av.x >= av.y && av.x >= av.z) { ma = av.x; if (v.x > 0.0) { face = 0.0; sc = -v.z; tc = -v.y; } else { face = 1.0; sc = v.z; tc = -v.y; } }
            else if (av.y >= av.z) { ma = av.y; if (v.y > 0.0) { face = 2.0; sc = v.x; tc = v.z; } else { face = 3.0; sc = v.x; tc = -v.z; } }
            else { ma = av.z; if (v.z > 0.0) { face = 4.0; sc = v.x; tc = -v.y; } else { face = 5.0; sc = -v.x; tc = -v.y; } }
            vec2 st = vec2(sc / ma, tc / ma) * 0.5 + 0.5;
            vec2 auv = vec2((face * 18.0 + 1.0 + st.x * 16.0) / 108.0, (1.0 + st.y * 16.0) / 18.0);
            vec3 irr = textureLod(probe_irr, auv, 0.0).rgb * probe_ambiance;
            amb = mix(amb, irr, min(probe_ambiance, 1.0));
        }
        return amb + min(clamp(nl, 0.0, 1.0), shadow) * classic_sun - classic_l0;
    }
    float d = min(pow(clamp(nl, 0.0, 1.0), 1.2), shadow);
    return 1.1 * cl_dec(classic_amb * ease + cl_enc(vec3(d)) * classic_sun) - classic_l0;
}
";

    /// <summary>The view-to-world basis and the camera's position, handed from fragment() to light(), where the engine's matrices are not visible (the sky probe's lookup needs the world position and normal).</summary>
    internal static string ProbeView(string body) => body.Replace("void fragment() {", "void fragment() {" + "\n" + "    pr_capf = (CAMERA_VISIBLE_LAYERS & 0x100000u) != 0u ? 1.0 : 0.0; pr_c0 = INV_VIEW_MATRIX[0].xyz; pr_c1 = INV_VIEW_MATRIX[1].xyz; pr_c2 = INV_VIEW_MATRIX[2].xyz; pr_cam = INV_VIEW_MATRIX[3].xyz; pr_pos = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;");

    internal const string ClassicLight = @"
void light() {
    float nl = dot(NORMAL, LIGHT);
    if (classic_on > 0.5 && LIGHT_IS_DIRECTIONAL) {
        DIFFUSE_LIGHT += cl_diffuse_extra(nl, ATTENUATION, pr_pos, normalize(mat3(pr_c0, pr_c1, pr_c2) * NORMAL), pr_cam);
    } else {
        DIFFUSE_LIGHT += clamp(nl, 0.0, 1.0) * ATTENUATION * LIGHT_COLOR / PI;
    }
}
";

    /// <summary>The test that tells the glow camera (GlowPass) from the view's: the glow camera sees only layer 1 and layer 20,
    /// the view's sees every layer.</summary>
    internal const string GlowCamera = "(CAMERA_VISIBLE_LAYERS & 0x7FFFEu) == 0u";

    /// <summary>
    /// The shader function that gives, per channel, the factor to apply to a colour (linear) so that its display encoding
    /// is the glow weight times the colour's own encoding (<see cref="Viewer.Core.World.FaceGlow.Scale"/>): the glow picture
    /// holds each glowing face's displayed colour times its glow value, as the official viewer's does, not that value
    /// in linear light.
    /// </summary>
    internal const string GlowFunctions = @"
vec3 glow_enc(vec3 c) { return mix(c * 12.92, 1.055 * pow(max(c, vec3(0.0)), vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c)); }
vec3 glow_dec(vec3 c) { return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c)); }
vec3 glow_k(vec3 c, float g) { vec3 cc = max(c, vec3(0.0001)); return glow_dec(g * glow_enc(cc)) / cc; }
";
    private static string WithGlowFunctions(string body) => body.Replace("void fragment() {", GlowFunctions + "void fragment() {");

    /// <summary>A new shader for the variant; the owner (PrimLayer) caches it per layer and frees it with the region.</summary>
    public static Shader Create(bool fullbright, bool blend) => new() { Code = FaceCode(fullbright, blend, classic: true) };

    /// <summary>The face shader drawing a face as a cut-out (<see cref="Viewer.Core.World.AutoMask"/>): solid where the texture's alpha reaches the official cutoff, nothing elsewhere; opaque otherwise.</summary>
    public static Shader CreateMasked(bool fullbright) => new() { Code = FaceCode(fullbright, false, mask: true, classic: true) };

    /// <summary>The system body's face shader: the lit face shader that throws away a pixel whose texture alpha is below
    /// <see cref="Viewer.Core.Avatars.BakeAlpha.Minimum"/>, as the official viewer's avatar shader does (so a worn alpha
    /// layer's holes, and a fully transparent bake, show nothing of the body there).</summary>
    public static Shader CreateBody(bool blend) => new() { Code = FaceCode(false, blend, bodyCut: true, classic: true) };

    /// <summary>The opaque face shader reading the tint from the vertex colour (times the uniform tint, left at white), so faces that differ only in colour
    /// can be merged into one surface (<see cref="Viewer.Shell.CellMerge"/>). Not for planar mapping: the merged vertices are not in the object's space.</summary>
    public static Shader CreateTinted(bool fullbright) => new() { Code = TintedCode(fullbright) };

    internal static string TintedCode(bool fullbright) => FaceCode(fullbright, false, classic: true)
        .Replace("varying vec2 sl_uv;", "varying vec2 sl_uv;\nvarying vec4 sl_vc;")
        .Replace("void vertex() {", "void vertex() {\n    sl_vc = COLOR;")
        .Replace("vec4 col = texel * tint;", "vec4 col = texel * tint * sl_vc;");

    private static string FaceCode(bool fullbright, bool blend, bool bodyCut = false, bool mask = false, bool classic = false)
    {
        string mode = "render_mode cull_back" + (blend ? ", blend_mix, depth_draw_opaque" : "") + (fullbright ? ", unshaded" : "") + ";";
        string cut = bodyCut ? "if (texel.a < " + Viewer.Core.Avatars.BakeAlpha.Minimum.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture) + ") { discard; }\n    " : "";
        // as the official viewer: half an 8-bit step below the cutoff, so a texel exactly at it stays
        if (mask) cut = "if (col.a < " + (Viewer.Core.World.AutoMask.Cutoff - 0.001953125f).ToString("0.0######", System.Globalization.CultureInfo.InvariantCulture) + ") { discard; }\n    ";
        string body = WithGlowFunctions(Body).Replace("%ALPHA%", cut + (blend ? "ALPHA = col.a;" : "")).Replace("%DIV%", blend ? "max(col.a, 0.0001)" : "1.0");
        if (classic && !fullbright) body = ClassicGlobals + ProbeView(body) + ClassicLight;
        return "shader_type spatial;\n" + mode + (blend ? WaterCut(UnderwaterFace(body, fullbright)) : body);
    }

    /// <summary>
    /// A blended face under the water seen by a camera under the water: its colour times what survives the way to it, plus the water fog's light
    /// (<see cref="Viewer.Core.World.UnderwaterView.Fog"/>), as the official viewer's alpha shaders apply it. The haze pass
    /// (<see cref="EnvironmentLayer.WaterHazeCode"/>) is drawn before see-through things, so each does this itself. water_under_level is the water's
    /// height while the camera is under it and far below everything otherwise. A lit face under a classic sky scales the fog's light by 1.1 too.
    /// The glow picture is left as it is.
    /// </summary>
    internal static string UnderwaterFace(string body, bool unshaded)
    {
        string classicDeclared = unshaded || body.Contains("global uniform float classic_on;", StringComparison.Ordinal) ? "" : "global uniform float classic_on;\n";
        string declarations = "global uniform float water_under_level;\nglobal uniform vec3 water_under_color;\nglobal uniform float water_under_density;\nglobal uniform float water_under_slant;\n"
            + classicDeclared + EnvironmentLayer.FogCode;
        string apply = unshaded
            ? "ALBEDO = ALBEDO * uw.y + water_under_color * uw.x;"
            : "ALBEDO *= uw.y;\n            EMISSION = EMISSION * uw.y + water_under_color * uw.x * (classic_on > 0.5 && classic_on < 1.5 ? 1.1 : 1.0);\n            SPECULAR *= uw.y;"
              + (body.Contains("varying vec3 spec_lin;", StringComparison.Ordinal) ? "\n            spec_lin *= uw.y;" : "");
        string block = "    if (CAMERA_POSITION_WORLD.y < water_under_level && !(" + GlowCamera + ")) {\n"
            + "        vec3 uw_at = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;\n"
            + "        if (uw_at.y <= water_under_level) {\n"
            + "            vec3 uw_way = uw_at - CAMERA_POSITION_WORLD;\n"
            + "            vec2 uw = under_fog(water_under_density, water_under_slant, water_under_level - CAMERA_POSITION_WORLD.y, uw_way.y / max(length(uw_way), 0.0001), length(uw_way));\n"
            + "            " + apply + "\n        }\n    }\n";
        int glow = body.IndexOf("    if (" + GlowCamera + ") {", StringComparison.Ordinal);
        if (glow < 0) throw new InvalidOperationException("a face shader without its glow branch");
        return body.Insert(glow, block).Replace("varying vec2 sl_uv;", "varying vec2 sl_uv;\n" + declarations);
    }

    /// <summary>
    /// A blended face's water cut: the instance parameter water_clip (side, height) keeps only the part of the face
    /// above the water (side +1) or below it (-1); 0, the default, keeps all of it. A blended face crossing the water is
    /// drawn twice, once before the water cut to the side beyond it and once after it cut to the camera's side, as the
    /// official viewer draws it (<see cref="Viewer.Core.World.BlendOrder"/>).
    /// </summary>
    internal static string WaterCut(string body) => body
        .Replace("varying vec2 sl_uv;", "varying vec2 sl_uv;\ninstance uniform vec2 water_clip = vec2(0.0);\nvarying float water_y;")
        .Replace("void vertex() {", "void vertex() {\n    water_y = (MODEL_MATRIX * vec4(VERTEX, 1.0)).y;")
        .Replace("void fragment() {", "void fragment() {\n    if (water_clip.x != 0.0 && (water_y - water_clip.y) * water_clip.x < 0.0) { discard; }");

    // Skinning for a worn rigged mesh, added in front of a face shader's vertex function. The mesh's vertices carry four joint indices
    // (CUSTOM0) and four weights (CUSTOM1). Two textures, both of RGBA float texels with three texels (the first three rows of a
    // matrix, translation in the fourth channel) a matrix: skin_bones, one row a worn-on avatar, a matrix for each node of its
    // skeleton in the pose it has now; skin_atlas, a pair of rows a mesh, the first the matrices of its joints (bind shape matrix
    // times inverse bind matrix), the second the skeleton node each joint is (SkinTables). The two instance parameters pick the rows. A vertex
    // is moved by the weighted sum of (node matrix times joint matrix), and its normal turned by the same sum. Instance parameters
    // are used for the rows, so every worn mesh with one look shares one material.
    private const string SkinDeclarations = @"
uniform sampler2D skin_bones : filter_nearest, repeat_disable;
uniform sampler2D skin_atlas : filter_nearest, repeat_disable;
instance uniform int avatar_row = 0;
instance uniform int skin_row = 0;

mat4 skin_fetch(sampler2D t, int index, int y) {
    vec4 r0 = texelFetch(t, ivec2(index * 3, y), 0);
    vec4 r1 = texelFetch(t, ivec2(index * 3 + 1, y), 0);
    vec4 r2 = texelFetch(t, ivec2(index * 3 + 2, y), 0);
    return mat4(vec4(r0.x, r1.x, r2.x, 0.0), vec4(r0.y, r1.y, r2.y, 0.0), vec4(r0.z, r1.z, r2.z, 0.0), vec4(r0.w, r1.w, r2.w, 1.0));
}

";

    private const string SkinBlock = @"
    {
        vec4 skin_w = CUSTOM1;
        mat4 skin_m = mat4(0.0);
        for (int i = 0; i < 4; i++) {
            if (skin_w[i] > 0.0) {
                int j = int(CUSTOM0[i] + 0.5);
                int node = int(texelFetch(skin_atlas, ivec2(j, skin_row * 2 + 1), 0).r + 0.5);
                skin_m += skin_w[i] * (skin_fetch(skin_bones, node, avatar_row) * skin_fetch(skin_atlas, j, skin_row * 2));
            }
        }
        VERTEX = (skin_m * vec4(VERTEX, 1.0)).xyz;
        NORMAL = normalize((skin_m * vec4(NORMAL, 0.0)).xyz);
    }
";

    /// <summary>A face shader's code with the skinning of a worn rigged mesh in front of its vertex function (so its planar texture
    /// coordinates and water cut read the skinned vertex). Apply it last, after the water cut. The declarations go right after the render
    /// mode, ahead of every other instance uniform: the engine numbers an instance's uniforms in declaration order and reads each name at
    /// the index it has in the first material that declares it, so a variant that declares another instance uniform first (the blended
    /// variant's water cut, the face shaders' object scale) would otherwise leave the avatar and skin rows at the wrong index when one
    /// instance draws more than one variant.</summary>
    internal static string Skin(string code)
    {
        int mode = code.IndexOf("render_mode", StringComparison.Ordinal);
        int at = code.IndexOf(';', Math.Max(mode, 0)) + 1;
        return code.Insert(at, "\n" + SkinDeclarations).Replace("void vertex() {", "void vertex() {" + SkinBlock);
    }

    /// <summary>The legacy face shader (<see cref="Create"/>) skinning a worn rigged mesh; kept per process by <see cref="KeptShaders"/>.</summary>
    public static Shader CreateSkinned(bool fullbright, bool blend) => new() { Code = Skin(FaceCode(fullbright, blend, classic: true)) };

    /// <summary>The legacy material shader (<see cref="CreateLegacy"/>) skinning a worn rigged mesh.</summary>
    public static Shader CreateLegacySkinned(bool fullbright, Viewer.Core.World.FaceAlpha alpha) => new() { Code = Skin(LegacyCode(fullbright, alpha, classic: true)) };

    /// <summary>The skip shader: a surface given it draws nothing (every vertex placed outside the view, so nothing is
    /// rasterised in any pass). An object whose blended faces are drawn by face instances of their own
    /// (<see cref="BlendedFaces"/>) gives it to those surfaces, and each face instance to every surface but its own.</summary>
    internal const string SkipCode = "shader_type spatial;\nrender_mode unshaded, cull_disabled;\nvoid vertex() { POSITION = vec4(2.0, 2.0, 2.0, 1.0); }\nvoid fragment() { ALBEDO = vec3(0.0); }\n";

    // The PBR face shader: a glTF 2.0 metallic-roughness material. The face's own texture coordinates (planar
    // texgen as above) carry each slot's KHR_texture_transform: in glTF's space (V down the image) scale, then rotate, then
    // offset, the official viewer's order (textureUtilV.glsl); the legacy texture entry's repeats, offset and rotation do
    // not apply. Colour textures are read raw and converted to linear here, as the legacy shader does, then multiplied by
    // the linear factors. The metallic-roughness texture is an ORM texture: R occlusion, G roughness, B metallic. The normal
    // map (glTF's convention: +X right, +Y up the image) is applied in a tangent frame built from the screen derivatives
    // of the position and the slot's coordinates, so the mesh needs no tangents. A slot without a texture samples white
    // (factors alone); the emissive slot is black while its texture loads.
    private const string PbrBody = @"
uniform sampler2D base_tex : hint_default_white, filter_linear_mipmap, repeat_enable;
uniform sampler2D nrm_tex : hint_default_white, filter_linear_mipmap, repeat_enable;
uniform sampler2D orm_tex : hint_default_white, filter_linear_mipmap, repeat_enable;
uniform sampler2D emis_tex : hint_default_white, filter_linear_mipmap, repeat_enable;
uniform vec4 base_factor = vec4(1.0);
uniform vec3 emis_factor = vec3(0.0);
uniform float metallic = 1.0;
uniform float roughness = 1.0;
uniform bool use_normal = false;
uniform float glow_w = 0.0;
uniform vec4 xf_base = vec4(1.0, 1.0, 0.0, 0.0);
uniform vec4 xf_nrm = vec4(1.0, 1.0, 0.0, 0.0);
uniform vec4 xf_orm = vec4(1.0, 1.0, 0.0, 0.0);
uniform vec4 xf_emis = vec4(1.0, 1.0, 0.0, 0.0);
uniform vec2 off_base = vec2(0.0);
uniform vec2 off_nrm = vec2(0.0);
uniform vec2 off_orm = vec2(0.0);
uniform vec2 off_emis = vec2(0.0);
uniform bool planar = false;
instance uniform vec3 obj_scale = vec3(1.0);
varying vec2 sl_uv;

void vertex() {
    vec2 tc = UV;
    if (planar) {
        vec3 p = vec3(VERTEX.x, -VERTEX.z, VERTEX.y) * obj_scale;
        vec3 n = normalize(vec3(NORMAL.x, -NORMAL.z, NORMAL.y));
        vec3 b;
        float d = n.x;
        if (d >= 0.5 || d <= -0.5) { b = d < 0.0 ? vec3(0.0, -1.0, 0.0) : vec3(0.0, 1.0, 0.0); }
        else { b = n.y > 0.0 ? vec3(-1.0, 0.0, 0.0) : vec3(1.0, 0.0, 0.0); }
        vec3 t = cross(b, n);
        tc = vec2(1.0 + (dot(b, p) * 2.0 - 0.5), -(dot(t, p) * 2.0 - 0.5));
    }
    sl_uv = tc;
}

// xf: scale (xy) and rotation (z); off: offset. The result is sampled directly (glTF's V down the image).
vec2 khr(vec2 uv, vec4 xf, vec2 off) {
    vec2 t = vec2(uv.x, 1.0 - uv.y) * xf.xy;
    float c = cos(xf.z);
    float s = sin(xf.z);
    return vec2(c * t.x + s * t.y, -s * t.x + c * t.y) + off;
}

vec3 srgb_to_linear(vec3 c) {
    return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

void fragment() {
    vec4 bc = texture(base_tex, khr(sl_uv, xf_base, off_base));
    vec3 orm = texture(orm_tex, khr(sl_uv, xf_orm, off_orm)).rgb;
    ALBEDO = srgb_to_linear(bc.rgb) * base_factor.rgb;
    METALLIC = clamp(metallic * orm.b, 0.0, 1.0);
    ROUGHNESS = clamp(roughness * orm.g, 0.0, 1.0);
    AO = orm.r;
    EMISSION = srgb_to_linear(texture(emis_tex, khr(sl_uv, xf_emis, off_emis)).rgb) * emis_factor;
    if (use_normal) {
        vec2 uvn = khr(sl_uv, xf_nrm, off_nrm);
        vec3 nt = texture(nrm_tex, uvn).rgb * 2.0 - 1.0;
        vec3 n = normalize(NORMAL);
        vec3 dp1 = dFdx(VERTEX);
        vec3 dp2 = dFdy(VERTEX);
        vec2 duv1 = dFdx(uvn);
        vec2 duv2 = dFdy(uvn);
        vec3 dp2perp = cross(dp2, n);
        vec3 dp1perp = cross(n, dp1);
        vec3 t = dp2perp * duv1.x + dp1perp * duv2.x;   // along +U
        vec3 b = dp2perp * duv1.y + dp1perp * duv2.y;   // along +V, down the image
        float inv = inversesqrt(max(max(dot(t, t), dot(b, b)), 1e-20));
        NORMAL = normalize(t * inv * nt.x - b * inv * nt.y + n * nt.z);
    }
    if (" + GlowCamera + @") {
        // the glow picture: the lit colour times the glow weight and the brightest channel of the emissive light; a face in
        // mask alpha mode has none; a blended face as its blend would show
        float lum = max(EMISSION.r, max(EMISSION.g, EMISSION.b));
        vec3 d = glow_k(ALBEDO + EMISSION, %GLOWK%) / %DIV%;
        ALBEDO *= d;
        EMISSION *= d;
        METALLIC = 0.0;
        SPECULAR = 0.0;
    }
}
";

    /// <summary>A new PBR shader, single-sided (back faces culled) or double-sided, with its alpha mode (opaque, blend or
    /// mask; <see cref="Viewer.Core.World.FaceLooks.Pbr"/>); kept per process by <see cref="KeptShaders"/>.</summary>
    public static Shader CreatePbr(bool doubleSided, Viewer.Core.World.FaceAlpha alpha = Viewer.Core.World.FaceAlpha.Opaque)
    {
        string mode = "render_mode " + (doubleSided ? "cull_disabled" : "cull_back") + (alpha == Viewer.Core.World.FaceAlpha.Blend ? ", blend_mix, depth_draw_opaque" : "") + ";";
        // glTF: blend uses the base colour's alpha times the factor's; mask draws only where that reaches the cutoff
        string a = alpha switch
        {
            Viewer.Core.World.FaceAlpha.Blend => "\n    ALPHA = bc.a * base_factor.a;",
            Viewer.Core.World.FaceAlpha.Mask => "\n    if (bc.a * base_factor.a < alpha_cutoff) { discard; }",
            _ => "",
        };
        string pbr = WithGlowFunctions(PbrBody).Replace("%GLOWK%", alpha == Viewer.Core.World.FaceAlpha.Mask ? "0.0" : "glow_w * lum")
            .Replace("%DIV%", alpha == Viewer.Core.World.FaceAlpha.Blend ? "max(bc.a * base_factor.a, 0.0001)" : "1.0");
        string body = alpha == Viewer.Core.World.FaceAlpha.Opaque ? pbr
            : pbr.Replace("uniform bool use_normal = false;", "uniform bool use_normal = false;\nuniform float alpha_cutoff = 0.5;")
                .Replace("    ALBEDO = srgb_to_linear(bc.rgb) * base_factor.rgb;", "    ALBEDO = srgb_to_linear(bc.rgb) * base_factor.rgb;" + a);
        if (alpha == Viewer.Core.World.FaceAlpha.Blend) body = WaterCut(UnderwaterFace(body, unshaded: false));
        return new() { Code = "shader_type spatial;\n" + mode + body };
    }

    // The legacy material face shader: the legacy face shader's texture, tint and texture coordinates, plus the region's
    // legacy material (FaceLooks.Legacy): a normal map and a specular map, each with its own repeats, offset and rotation
    // about the face centre (as the face texture's); the diffuse alpha mode as its variant (opaque, blend, mask at the
    // cutoff, emissive: alpha is the share of the colour shown unlit). Lighting, written from the official viewer's
    // documented model (materialF.glsl, pipeline.cpp's specular table) in our own terms: the diffuse term is the engine's
    // default (Burley at the legacy faces' roughness 0.9, so a face looks the same with or without a material until its maps
    // show); the specular term is a normalised Blinn-Phong lobe with exponent glossiness^2 * 368, a Schlick-style Fresnel
    // (0.5 to 0.9), the Cook-Torrance geometry term and a soft terminator, coloured by the specular map times the specular
    // colour. The normal map's alpha scales the glossiness. A normal map is applied in a tangent frame from the screen
    // derivatives (as the PBR shader), +Y up the image. The specular a face without a specular map has is its shiny level (FaceLooks.Legacy). The environment reflection is not drawn (a recorded gap).
    private const string LegacyBody = @"
uniform sampler2D tex : filter_linear_mipmap, repeat_enable;
uniform sampler2D nrm_tex : hint_default_white, filter_linear_mipmap, repeat_enable;
uniform sampler2D spec_tex : hint_default_white, filter_linear_mipmap, repeat_enable;
uniform vec4 tint = vec4(1.0);
uniform vec2 rep = vec2(1.0);
uniform vec2 off = vec2(0.0);
uniform float rot = 0.0;
uniform vec4 nrm_xf = vec4(1.0, 1.0, 0.0, 0.0);
uniform vec4 spec_xf = vec4(1.0, 1.0, 0.0, 0.0);
uniform float nrm_rot = 0.0;
uniform float spec_rot = 0.0;
uniform bool use_normal = false;
uniform bool use_spec = false;
uniform vec3 spec_col = vec3(1.0);
uniform float gloss = 0.5;
uniform float alpha_cutoff = 0.0;
uniform bool planar = false;
uniform float glow_w = 0.0;
instance uniform vec3 obj_scale = vec3(1.0);
varying vec2 sl_uv;
varying vec2 nrm_uv;
varying vec2 spec_uv;
varying vec3 spec_lin;
varying float gloss_f;

// SL's face texture transform: about the face centre, rotate, scale by the repeats, add the offset (llface.cpp)
vec2 place(vec2 tc, vec2 r, vec2 o, float a) {
    float s = tc.x - 0.5;
    float u = tc.y - 0.5;
    float c = cos(a);
    float sn = sin(a);
    return vec2((s * c + u * sn) * r.x + o.x + 0.5, (-s * sn + u * c) * r.y + o.y + 0.5);
}

void vertex() {
    vec2 tc = UV;
    if (planar) {
        vec3 p = vec3(VERTEX.x, -VERTEX.z, VERTEX.y) * obj_scale;
        vec3 n = normalize(vec3(NORMAL.x, -NORMAL.z, NORMAL.y));
        vec3 b;
        float d = n.x;
        if (d >= 0.5 || d <= -0.5) { b = d < 0.0 ? vec3(0.0, -1.0, 0.0) : vec3(0.0, 1.0, 0.0); }
        else { b = n.y > 0.0 ? vec3(-1.0, 0.0, 0.0) : vec3(1.0, 0.0, 0.0); }
        vec3 t = cross(b, n);
        tc = vec2(1.0 + (dot(b, p) * 2.0 - 0.5), -(dot(t, p) * 2.0 - 0.5));
    }
    sl_uv = place(tc, rep, off, rot);
    nrm_uv = place(tc, nrm_xf.xy, nrm_xf.zw, nrm_rot);
    spec_uv = place(tc, spec_xf.xy, spec_xf.zw, spec_rot);
}

vec3 srgb_to_linear(vec3 c) {
    return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

void fragment() {
    vec4 texel = texture(tex, vec2(sl_uv.x, 1.0 - sl_uv.y));
    vec4 col = texel * tint;%ALPHA%
    ALBEDO = srgb_to_linear(col.rgb)%ALBEDO%;
    ROUGHNESS = 0.9;
    SPECULAR = 0.0;
    float g = gloss;
    if (use_normal) {
        vec2 uvn = vec2(nrm_uv.x, 1.0 - nrm_uv.y);
        vec4 ns = texture(nrm_tex, uvn);
        vec3 nt = ns.rgb * 2.0 - 1.0;
        g *= ns.a;
        vec3 n = normalize(NORMAL);
        vec3 dp1 = dFdx(VERTEX);
        vec3 dp2 = dFdy(VERTEX);
        vec2 duv1 = dFdx(uvn);
        vec2 duv2 = dFdy(uvn);
        vec3 dp2perp = cross(dp2, n);
        vec3 dp1perp = cross(n, dp1);
        vec3 t = dp2perp * duv1.x + dp1perp * duv2.x;
        vec3 b = dp2perp * duv1.y + dp1perp * duv2.y;
        float inv = inversesqrt(max(max(dot(t, t), dot(b, b)), 1e-20));
        NORMAL = normalize(t * inv * nt.x - b * inv * nt.y + n * nt.z);
    }
    vec3 s = spec_col;   // the specular colour and map multiply as stored (sRGB), then convert, as the official deferred pass does
    if (use_spec) { s *= texture(spec_tex, vec2(spec_uv.x, 1.0 - spec_uv.y)).rgb; }
    spec_lin = srgb_to_linear(s)%SPECSCALE%;
    gloss_f = g;
    if (" + GlowCamera + @") {
        vec3 d = glow_k(ALBEDO + EMISSION, glow_w * texel.a) / %DIV%;
        ALBEDO *= d;
        EMISSION *= d;
        spec_lin *= d;
    }
}

float schlick(float u) {
    float m = clamp(1.0 - u, 0.0, 1.0);
    float m2 = m * m;
    return m2 * m2 * m;
}

void light() {
    float nl = dot(NORMAL, LIGHT);
    float cnl = clamp(nl, 0.0, 1.0);
    float cnv = clamp(dot(NORMAL, VIEW), 0.0, 1.0);
    vec3 h = normalize(LIGHT + VIEW);
    float clh = clamp(dot(LIGHT, h), 0.0, 1.0);
    float fd90 = 2.0 * clh * clh * 0.9 - 0.5;
    DIFFUSE_LIGHT += (1.0 / PI) * (1.0 + fd90 * schlick(cnv)) * (1.0 + fd90 * schlick(cnl)) * cnl * ATTENUATION * LIGHT_COLOR;
    float nh = dot(NORMAL, h);
    if (gloss_f > 0.0 && nl > 0.0 && nh > 0.0) {
        float vh = max(dot(VIEW, h), 1e-4);
        float e = gloss_f * gloss_f * 368.0;
        float lobe = pow(nh, e) * ((e + 2.0) * (e + 4.0)) / (8.0 * PI * (pow(2.0, -e / 2.0) + e));
        float fres = pow(1.0 - vh, 5.0) * 0.4 + 0.5;
        float gt = max(0.0, min(2.0 * nh * cnv / vh, 2.0 * nh * nl / vh));
        float lit = min(nl * 6.0, 1.0);
        SPECULAR_LIGHT += clamp(lit * fres * lobe * gt / (nh * nl) * ATTENUATION * LIGHT_COLOR / PI * spec_lin, vec3(0.0), vec3(11.2));
    }
}
";

    // the legacy material's light function under the official classic response: the diffuse as ClassicLight, and the specular of softenLightF.glsl
    // (Fresnel, geometry term, normalised lobe, lit with the sun's colour as linear light, times the final 1.1) in place of the engine-light version
    private static string ClassicLegacyLight(string body)
    {
        int at = body.IndexOf("void light() {", StringComparison.Ordinal);
        const string tail = @"
void light() {
    float nl = dot(NORMAL, LIGHT);
    float cnl = clamp(nl, 0.0, 1.0);
    float cnv = clamp(dot(NORMAL, VIEW), 0.0, 1.0);
    vec3 h = normalize(LIGHT + VIEW);
    float nh = dot(NORMAL, h);
    if (classic_on > 0.5 && LIGHT_IS_DIRECTIONAL) {
        DIFFUSE_LIGHT += cl_diffuse_extra(nl, ATTENUATION, pr_pos, normalize(mat3(pr_c0, pr_c1, pr_c2) * NORMAL), pr_cam);
        if (gloss_f > 0.0 && nl > 0.0 && nh > 0.0) {
            float vh = max(dot(VIEW, h), 1e-4);
            float e = gloss_f * gloss_f * 368.0;
            float lobe = pow(nh, e) * ((e + 2.0) * (e + 4.0)) / (8.0 * PI * (pow(2.0, -e / 2.0) + e));
            float fres = pow(1.0 - vh, 5.0) * 0.4 + 0.5;
            float gt = max(0.0, min(2.0 * nh * cnv / vh, 2.0 * nh * nl / vh));
            float lit = min(nl * 6.0, 1.0);
            SPECULAR_LIGHT += clamp((classic_on > 1.5 ? 1.0 : 1.1) * lit * ATTENUATION * fres * lobe * gt / (nh * nl) * classic_sun_lin * spec_lin, vec3(0.0), vec3(11.2));
        }
    } else {
    DIFFUSE_LIGHT += cnl * ATTENUATION * LIGHT_COLOR / PI;
    if (gloss_f > 0.0 && nl > 0.0 && nh > 0.0) {
        float vh = max(dot(VIEW, h), 1e-4);
        float e = gloss_f * gloss_f * 368.0;
        float lobe = pow(nh, e) * ((e + 2.0) * (e + 4.0)) / (8.0 * PI * (pow(2.0, -e / 2.0) + e));
        float fres = pow(1.0 - vh, 5.0) * 0.4 + 0.5;
        float gt = max(0.0, min(2.0 * nh * cnv / vh, 2.0 * nh * nl / vh));
        float lit = min(nl * 6.0, 1.0);
        SPECULAR_LIGHT += clamp(lit * fres * lobe * gt / (nh * nl) * ATTENUATION * LIGHT_COLOR / PI * spec_lin, vec3(0.0), vec3(11.2));
    }
    }
}
";
        return body[..at] + tail.TrimStart('\n');
    }

    /// <summary>The legacy material shader under the official classic lighting, not skinned: what <see cref="CreateLegacySkinned"/> draws, for a render case that measures it on a flat patch.</summary>
    internal static Shader CreateLegacyClassicForTest() => new() { Code = LegacyCode(false, Viewer.Core.World.FaceAlpha.Opaque, classic: true) };

    /// <summary>A new legacy material shader for the variant (<see cref="Viewer.Core.World.FaceLooks.Legacy"/>); kept per
    /// process by <see cref="KeptShaders"/>. Fullbright draws unlit (the maps and the emissive mask then change nothing).</summary>
    public static Shader CreateLegacy(bool fullbright, Viewer.Core.World.FaceAlpha alpha) => new() { Code = LegacyCode(fullbright, alpha, classic: true) };

    private static string LegacyCode(bool fullbright, Viewer.Core.World.FaceAlpha alpha, bool classic = false)
    {
        bool blend = alpha == Viewer.Core.World.FaceAlpha.Blend;
        string mode = "render_mode cull_back" + (blend ? ", blend_mix, depth_draw_opaque" : "") + (fullbright ? ", unshaded" : "") + ";";
        string a = alpha switch
        {
            Viewer.Core.World.FaceAlpha.Blend => "\n    ALPHA = col.a;",
            // as the official viewer: half an 8-bit step below the cutoff, so a texel exactly at it stays
            Viewer.Core.World.FaceAlpha.Mask => "\n    if (col.a < alpha_cutoff - 0.001953125) { discard; }",
            _ => "",
        };
        bool emissive = alpha == Viewer.Core.World.FaceAlpha.Emissive && !fullbright;
        string body = WithGlowFunctions(LegacyBody).Replace("%ALPHA%", a)
            // emissive: the alpha's share of the colour shown unlit (emission), the rest lit (and its specular with it)
            .Replace("%ALBEDO%", emissive ? " * (1.0 - col.a);\n    EMISSION = srgb_to_linear(col.rgb) * col.a" : "")
            .Replace("%SPECSCALE%", emissive ? " * (1.0 - col.a)" : "")
            .Replace("%DIV%", alpha == Viewer.Core.World.FaceAlpha.Blend ? "max(col.a, 0.0001)" : "1.0");
        if (fullbright) body = body[..body.IndexOf("float schlick(", StringComparison.Ordinal)];
        else if (classic) body = ClassicGlobals + ProbeView(ClassicLegacyLight(body));
        if (blend) body = WaterCut(UnderwaterFace(body, fullbright));
        return "shader_type spatial;\n" + mode + body;
    }
}
