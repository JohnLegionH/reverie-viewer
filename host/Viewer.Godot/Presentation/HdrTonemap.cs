// SPDX-License-Identifier: LGPL-2.1-only AND MIT
/**
 * @file postDeferredTonemap.glsl
 *
 * $LicenseInfo:firstyear=2024&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2024, Linden Research, Inc.
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
 * @file exposureF.glsl
 *
 * $LicenseInfo:firstyear=2023&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2023, Linden Research, Inc.
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
 * @file luminanceF.glsl
 *
 * $LicenseInfo:firstyear=2023&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2023, Linden Research, Inc.
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

/*
 * Baking Lab, BakingLab/ACES.hlsl (https://github.com/TheRealMJP/BakingLab), whose fitted curve and two matrices the Hill fit below uses:
 *
 *  Baking Lab
 *  by MJP and David Neubelt
 *  http://mynameismjp.wordpress.com/
 *
 *  All code licensed under the MIT license
 *
 * The code in this file was originally written by Stephen Hill (@self_shadow), who deserves all
 * credit for coming up with this fit and implementing it. Buy him a beer next time you see him. :)
 *
 * Copyright (c) 2016 MJP
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */

// Adapted 2026 by Legion Builds. The compute shader (Glsl) follows the official post pass (toneMap's exposure, mix and clamp, called from postDeferredTonemap.glsl),
// written as a compute shader that reads and writes the scene's colour image. Its Hill fit uses Stephen Hill's fit as Baking Lab publishes it (MIT, notice above); its
// PBR Neutral mapper is written from the equations of the Khronos PBR Neutral specification text (github.com/KhronosGroup/ToneMapping, PBR_Neutral), not from any
// implementation of it; both are the same maths as Viewer.Core.World.ToneMap. The luminance sample and the exposure step follow luminanceF.glsl and exposureF.glsl,
// written as compute passes over the colour image. The rest of this file is our own code.
// Copyright (c) 2026 Legion Builds

using System;
using Godot;

namespace Viewer.Godot.Presentation;

/// <summary>
/// The tone mapping of a sky that sets a reflection probe ambiance, as a compositor effect: it runs on the scene's colour image after the opaque and transparent
/// passes and before the engine's own tone mapping (which is the linear one), so what reaches the display is what the official post pass makes: the colour
/// scaled by the exposure, mapped, mixed with the exposed colour by the mix, and clamped to 0..1. The parameters are those of <see cref="Viewer.Core.World.ToneMap"/>.
/// It exists only while a sky takes the tone-mapped path (the layer sets the world's compositor then, and takes it away after).
/// </summary>
public partial class HdrTonemapEffect : CompositorEffect
{
    internal const string Glsl = @"
#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(rgba16f, set = 0, binding = 0) uniform image2D color_image;
layout(set = 0, binding = 1) uniform sampler2D depth_tex;
// the haze pass's inputs (SkyLighting.HazeOf, in the engine's axes): the engine's inverse projection, the camera's axes, the haze's constant light and its glow-weighted light,
// the survival exponents (per metre, per channel) with the sky's scale, the light's direction (clamped and as it is) with the glow shape
layout(set = 0, binding = 2, std140) uniform Haze {
    mat4 inv_proj;
    vec4 cam_x; vec4 cam_y; vec4 cam_z;
    vec4 constant_light;   // xyz: the light, w: 1 when the haze pass runs
    vec4 glow_light;       // xyz: the glow-weighted light, w: the glow factor
    vec4 exponent;         // xyz: the survival exponents, w: the sky's scale
    vec4 light_clamped;    // xyz: the light's direction (height held at -0.1), w: the glow shape's x
    vec4 light_raw;        // xyz: the light's direction, w: the glow shape's z
} haze;
// the scene's exposure scale, kept from frame to frame (x: the scale, y: the mean luminance last sampled, z: the scale it is heading for)
layout(set = 0, binding = 3, std430) buffer Exposure { vec4 state; } expo;
// mode 0: the haze only; 1: sample the scene's luminance and step the exposure; 2: the tone mapping with the exposure; 3: the haze and the tone mapping (no exposure change)
// expo_params: x the luminance that counts as fully bright, y the scale's lower end, z its upper end, w the speed of the step (per second)
layout(push_constant, std430) uniform Params { vec2 raster_size; float exposure; float tonemap_mix; int tonemap_type; int mode; float dt; int snap; vec4 expo_params; } params;
shared float partial[64];

// Hill's fit (Baking Lab, notice in this file's header): rows of the matrix into the fit's working space and of the one back out, as in ToneMap.AcesHill
const vec3 hill_in_x = vec3(0.59719, 0.35458, 0.04823), hill_in_y = vec3(0.07600, 0.90834, 0.01566), hill_in_z = vec3(0.02840, 0.13383, 0.83777);
const vec3 hill_out_x = vec3(1.60475, -0.53108, -0.07367), hill_out_y = vec3(-0.10208, 1.10813, -0.00605), hill_out_z = vec3(-0.00327, -0.07276, 1.07602);

// the fitted curve: a ratio of two quadratics in the value
vec3 hill_curve(vec3 v)
{
    vec3 numerator = v * (v + 0.0245786) - 0.000090537;
    vec3 denominator = v * (0.983729 * v + 0.4329510) + 0.238081;
    return numerator / denominator;
}

vec3 tonemap_hill(vec3 c)
{
    vec3 fitted = hill_curve(vec3(dot(hill_in_x, c), dot(hill_in_y, c), dot(hill_in_z, c)));
    return clamp(vec3(dot(hill_out_x, fitted), dot(hill_out_y, fitted), dot(hill_out_z, fitted)), 0.0, 1.0);
}

// PBR Neutral from its specification's equations (F90, Ks, Kd as the specification names them), as in ToneMap.PbrNeutral
const float neutral_f90 = 0.04, neutral_ks = 0.8 - neutral_f90, neutral_kd = 0.15;

vec3 tonemap_neutral(vec3 c)
{
    float smallest = min(c.r, min(c.g, c.b));
    float f = smallest <= 2.0 * neutral_f90 ? smallest - smallest * smallest / (4.0 * neutral_f90) : neutral_f90;
    vec3 offset = c - f;
    float p = max(offset.r, max(offset.g, offset.b));
    if (p <= neutral_ks) return offset;
    float pn = 1.0 - (1.0 - neutral_ks) * (1.0 - neutral_ks) / (p + 1.0 - 2.0 * neutral_ks);
    float g = 1.0 / (neutral_kd * (p - pn) + 1.0);
    return offset * (pn / p * g) + vec3(pn * (1.0 - g));
}

vec3 srgb_to_linear(vec3 c)
{
    return mix(c / 12.92, pow((max(c, vec3(0.0)) + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

float lum(vec3 col)
{
    return dot(vec3(0.2126, 0.7152, 0.0722), col);
}

// the scene's mean luminance over the middle of the view (the official sample reads the middle 60 per cent, nudged down a tenth to favour the ground over the sky), taken on
// a 64 by 64 grid, then the exposure step of exposureF.glsl: the luminance held to the coefficient and squared picks a scale between the range's ends, and the stored scale moves to it
void sample_exposure()
{
    uint t = gl_LocalInvocationIndex;
    ivec2 size = ivec2(params.raster_size);
    float sum = 0.0;
    for (int i = 0; i < 64; i++)
    {
        int idx = int(t) * 64 + i;
        int gx = idx % 64;
        int gy = idx / 64;
        vec2 uv = vec2(0.2 + 0.6 * (float(gx) + 0.5) / 64.0, 0.3 + 0.6 * (float(gy) + 0.5) / 64.0);
        ivec2 q = clamp(ivec2(uv * vec2(size)), ivec2(0), size - ivec2(1));
        vec3 c = imageLoad(color_image, q).rgb;
        if (any(isnan(c)) || any(isinf(c))) c = vec3(0.0);
        sum += min(lum(max(c, vec3(0.0))), 1000.0);
    }
    partial[t] = sum;
    barrier();
    for (uint s = 32u; s > 0u; s >>= 1)
    {
        if (t < s) partial[t] += partial[t + s];
        barrier();
    }
    if (t == 0u)
    {
        float mean_l = partial[0] / 4096.0;
        float l = clamp(mean_l, 0.0, params.expo_params.x) / params.expo_params.x;
        l = l * l;
        float target = mix(params.expo_params.z, params.expo_params.y, l);
        float prev = expo.state.x;
        float s = params.snap != 0 ? target : mix(prev, target, 1.0 - exp(-params.expo_params.w * params.dt));
        expo.state = vec4(s, mean_l, target, 0.0);
    }
}

void main()
{
    if (params.mode == 1) { sample_exposure(); return; }
    ivec2 p = ivec2(gl_GlobalInvocationID.xy);
    ivec2 size = ivec2(params.raster_size);
    if (p.x >= size.x || p.y >= size.y) return;
    vec4 col = imageLoad(color_image, p);
    if (params.mode != 2 && haze.constant_light.w > 0.5)
    {
        // the official haze pass (hazeF.glsl): every pixel that is not sky, seen through the air along its own ray; blended as the official viewer does (the scene times the
        // red channel's survival, plus the haze's light made linear and scaled by the sky's scale)
        float depth = texelFetch(depth_tex, p, 0).r;
        if (depth > 0.000001)
        {
            vec2 uv = (vec2(p) + 0.5) / vec2(size);
            vec4 pv = haze.inv_proj * vec4(uv * 2.0 - 1.0, depth, 1.0);
            pv.xyz /= pv.w;
            float dist = length(pv.xyz);
            vec3 vd = pv.xyz / max(dist, 0.00001);
            vec3 wd = normalize(vd.x * haze.cam_x.xyz + vd.y * haze.cam_y.xyz + vd.z * haze.cam_z.xyz);
            vec3 survival = exp(-haze.exponent.xyz * dist);
            float glow = dot(wd, haze.light_clamped.xyz) * max(0.0, dot(haze.light_raw.xyz, wd));
            glow = max(1.0 - glow, 0.001) * haze.light_clamped.w;
            glow = clamp(pow(glow, haze.light_raw.w), -100000.0, 100000.0) + 0.25;
            glow *= haze.glow_light.w;
            vec3 additive = min((haze.constant_light.xyz + haze.glow_light.xyz * glow) * (vec3(1.0) - survival), vec3(10.0));
            col.rgb = col.rgb * survival.r + srgb_to_linear(additive * 2.0) * haze.exponent.w;
        }
    }
    if (params.mode == 0) { imageStore(color_image, p, col); return; }
    float scale = params.mode == 2 ? expo.state.x : 1.0;
    vec3 exposed = col.rgb * params.exposure * scale;
    vec3 mapped = params.tonemap_type == 1 ? tonemap_hill(exposed) : tonemap_neutral(exposed);
    col.rgb = clamp(mix(exposed, mapped, params.tonemap_mix), vec3(0.0), vec3(1.0));
    imageStore(color_image, p, col);
}
";

    private RenderingDevice? _rd;
    private Rid _shader, _pipeline;
    private float _exposure = Viewer.Core.World.ToneMap.DefaultExposure, _mix = Viewer.Core.World.ToneMap.DefaultMix;
    private int _type = (int)Viewer.Core.World.ToneMap.DefaultType;
    // the haze pass: the frame's terms (null: no haze pass, the colour image is only tone-mapped), the sky's scale, the uniform buffer and the depth sampler
    private Viewer.Core.World.SkyLighting.HazeTerms? _haze;
    private float _hazeScale = 2f;
    private Rid _hazeBuffer, _depthSampler;
    private readonly byte[] _hazeBytes = new byte[208];
    // the automatic exposure: the scale's buffer, whether it runs and its range (a sky on the classic path, or the setting off, leaves it out), and the time of the last frame
    private Rid _exposureBuffer;
    private bool _dynamic;
    private float _expMin = 1f, _expMax = 1f;
    private long _lastTicks;

    /// <summary>A test sets this to have the exposure jump to the scene's value in one frame instead of moving at the official speed.</summary>
    public static bool ExposureInstant { get; set; }
    /// <summary>A test sets this to read the exposure back after every frame (it stalls the frame: never in the live loop).</summary>
    public static bool ReadExposure { get; set; }
    /// <summary>The exposure scale and the mean luminance it was worked out from, as of the last frame read back (see <see cref="ReadExposure"/>).</summary>
    public static (float Scale, float MeanLuminance, float Target) LastExposure { get; private set; } = (1f, 0f, 1f);

    /// <summary>Frames the effect has run on (a test or a probe asks whether the pass ran at all).</summary>
    public static long Ran => System.Threading.Interlocked.Read(ref s_ran);
    private static long s_ran;
    /// <summary>The reason the effect could not be set up on this device (null when it could).</summary>
    public static string? SetupError { get; private set; }

    public HdrTonemapEffect()
    {
        EffectCallbackType = EffectCallbackTypeEnum.PostTransparent;
        RenderingServer.CallOnRenderThread(Callable.From(Init));
    }

    /// <summary>The post pass's parameters, as the official viewer's settings give them.</summary>
    public void Set(float exposure, Viewer.Core.World.ToneMapType type, float mix) { _exposure = exposure; _type = (int)type; _mix = mix; }

    /// <summary>Sets the haze the pass puts over every pixel that is not sky (null: none): the terms of the frame and the sky's scale.</summary>
    public void SetHaze(Viewer.Core.World.SkyLighting.HazeTerms? terms, float skyScale) { _haze = terms; _hazeScale = skyScale; }

    /// <summary>Whether the haze pass runs (it has terms and the pass could be set up).</summary>
    public bool HazeActive => _haze is not null && _hazeBuffer.IsValid;

    /// <summary>Sets the automatic exposure: whether it runs and the range of its scale (<see cref="Viewer.Core.World.AutoExposure.Range"/>).</summary>
    public void SetExposureRange(bool dynamic, float min, float max) { _dynamic = dynamic; _expMin = min; _expMax = max; }

    /// <summary>Whether the exposure step runs on the frames (it is on, the range is wider than one value, and the pass could be set up).</summary>
    public bool ExposureActive => _dynamic && _expMax != _expMin && _exposureBuffer.IsValid;

    private void Init()
    {
        _rd = RenderingServer.GetRenderingDevice();
        if (_rd is null) { SetupError = "no rendering device"; return; }
        var source = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = Glsl };
        var spirv = _rd.ShaderCompileSpirVFromSource(source);
        if (!string.IsNullOrEmpty(spirv.CompileErrorCompute)) { SetupError = spirv.CompileErrorCompute; return; }
        _shader = _rd.ShaderCreateFromSpirV(spirv);
        if (!_shader.IsValid) { SetupError = "the compute shader was not created"; return; }
        _pipeline = _rd.ComputePipelineCreate(_shader);
        _hazeBuffer = _rd.UniformBufferCreate((uint)_hazeBytes.Length);
        _exposureBuffer = _rd.StorageBufferCreate(16, System.Runtime.InteropServices.MemoryMarshal.AsBytes<float>([1f, 0f, 1f, 0f]).ToArray());
        _depthSampler = _rd.SamplerCreate(new RDSamplerState { MinFilter = RenderingDevice.SamplerFilter.Nearest, MagFilter = RenderingDevice.SamplerFilter.Nearest });
    }

    /// <summary>The haze pass's uniform buffer for a frame: the projection's inverse, the camera's axes and the haze terms in the engine's axes (x east, y up, z south).</summary>
    private void FillHaze(Projection projection, Transform3D camera)
    {
        
        int at = 0;
        void F(float v) { System.BitConverter.TryWriteBytes(new System.Span<byte>(_hazeBytes, at, 4), v); at += 4; }
        void V4(float x, float y, float z, float q) { F(x); F(y); F(z); F(q); }
        var inv = projection.Inverse();
        V4(inv.X.X, inv.X.Y, inv.X.Z, inv.X.W); V4(inv.Y.X, inv.Y.Y, inv.Y.Z, inv.Y.W); V4(inv.Z.X, inv.Z.Y, inv.Z.Z, inv.Z.W); V4(inv.W.X, inv.W.Y, inv.W.Z, inv.W.W);
        var b = camera.Basis;
        V4(b.Column0.X, b.Column0.Y, b.Column0.Z, 0f); V4(b.Column1.X, b.Column1.Y, b.Column1.Z, 0f); V4(b.Column2.X, b.Column2.Y, b.Column2.Z, 0f);
        if (_haze is not { } t) { V4(0, 0, 0, 0); V4(0, 0, 0, 0); V4(0, 0, 0, 0); V4(0, 0, 0, 0); V4(0, 0, 0, 0); return; }
        // the viewer's frames have z up and y north; the engine's world has y up and z south
        static Vector3 E(System.Numerics.Vector3 v) => new(v.X, v.Z, -v.Y);
        V4(t.Constant.X, t.Constant.Y, t.Constant.Z, 1f);
        V4(t.GlowWeighted.X, t.GlowWeighted.Y, t.GlowWeighted.Z, t.GlowFactor);
        V4(t.Exponent.X, t.Exponent.Y, t.Exponent.Z, _hazeScale);
        var c = E(t.LightDirection); V4(c.X, c.Y, c.Z, t.GlowShape.X);
        var r = E(t.LightDirectionRaw); V4(r.X, r.Y, r.Z, t.GlowShape.Z);
    }

    public override void _RenderCallback(int effectCallbackType, RenderData renderData)
    {
        if (_rd is null || !_pipeline.IsValid || effectCallbackType != (int)EffectCallbackTypeEnum.PostTransparent) return;
        if (renderData.GetRenderSceneBuffers() is not RenderSceneBuffersRD buffers) return;
        var size = buffers.GetInternalSize();
        if (size.X <= 0 || size.Y <= 0) return;
        uint groupsX = (uint)((size.X + 7) / 8), groupsY = (uint)((size.Y + 7) / 8);
        uint views = buffers.GetViewCount();
        var sceneData = renderData.GetRenderSceneData();
        bool haze = _haze is not null && _hazeBuffer.IsValid && sceneData is not null;
        if (haze) { FillHaze(sceneData!.GetCamProjection(), sceneData.GetCamTransform()); _rd.BufferUpdate(_hazeBuffer, 0, (uint)_hazeBytes.Length, _hazeBytes); }
        else if (_hazeBuffer.IsValid) { System.Array.Clear(_hazeBytes); _rd.BufferUpdate(_hazeBuffer, 0, (uint)_hazeBytes.Length, _hazeBytes); }
        bool dynamic = ExposureActive;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = _lastTicks == 0 ? 0f : MathF.Min((float)((now - _lastTicks) / (double)System.Diagnostics.Stopwatch.Frequency), 0.25f);   // the official frame interval, held to a quarter second
        _lastTicks = now;
        // the official speed: the scale closes (1 - the speed error) of the gap in the speed target time
        float speed = -MathF.Log(Viewer.Core.World.AutoExposure.DefaultSpeedError) / Viewer.Core.World.AutoExposure.DefaultSpeedTarget;
        void Run(Rid set, int mode, uint gx, uint gy)
        {
            var push = new float[12];
            push[0] = size.X; push[1] = size.Y; push[2] = _exposure; push[3] = _mix;
            var bytes = new byte[48];
            System.Buffer.BlockCopy(push, 0, bytes, 0, 48);
            System.BitConverter.TryWriteBytes(new System.Span<byte>(bytes, 16, 4), _type);
            System.BitConverter.TryWriteBytes(new System.Span<byte>(bytes, 20, 4), mode);
            System.BitConverter.TryWriteBytes(new System.Span<byte>(bytes, 24, 4), dt);
            System.BitConverter.TryWriteBytes(new System.Span<byte>(bytes, 28, 4), ExposureInstant ? 1 : 0);
            System.BitConverter.TryWriteBytes(new System.Span<byte>(bytes, 32, 4), Viewer.Core.World.AutoExposure.DefaultCoefficient);
            System.BitConverter.TryWriteBytes(new System.Span<byte>(bytes, 36, 4), _expMin);
            System.BitConverter.TryWriteBytes(new System.Span<byte>(bytes, 40, 4), _expMax);
            System.BitConverter.TryWriteBytes(new System.Span<byte>(bytes, 44, 4), speed);
            long list = _rd!.ComputeListBegin();
            _rd.ComputeListBindComputePipeline(list, _pipeline);
            _rd.ComputeListBindUniformSet(list, set, 0);
            _rd.ComputeListSetPushConstant(list, bytes, (uint)bytes.Length);
            _rd.ComputeListDispatch(list, gx, gy, 1);
            _rd.ComputeListEnd();
        }
        for (uint view = 0; view < views; view++)
        {
            var image = buffers.GetColorLayer(view);
            var uniform = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 };
            uniform.AddId(image);
            var depth = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 1 };
            depth.AddId(_depthSampler);
            depth.AddId(buffers.GetDepthLayer(view));
            var hazeUniform = new RDUniform { UniformType = RenderingDevice.UniformType.UniformBuffer, Binding = 2 };
            hazeUniform.AddId(_hazeBuffer);
            var exposureUniform = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 3 };
            exposureUniform.AddId(_exposureBuffer);
            var set = UniformSetCacheRD.GetCache(_shader, 0, [uniform, depth, hazeUniform, exposureUniform]);
            if (dynamic)
            {
                // the haze first (the official sample is of the hazed scene), then the luminance sample and the exposure step, then the tone mapping with that exposure
                if (haze) Run(set, 0, groupsX, groupsY);
                Run(set, 1, 1, 1);
                Run(set, 2, groupsX, groupsY);
            }
            else Run(set, 3, groupsX, groupsY);
        }
        if (dynamic && ReadExposure && _exposureBuffer.IsValid)
        {
            var data = _rd.BufferGetData(_exposureBuffer, 0, 16);
            LastExposure = (System.BitConverter.ToSingle(data, 0), System.BitConverter.ToSingle(data, 4), System.BitConverter.ToSingle(data, 8));
        }
        System.Threading.Interlocked.Increment(ref s_ran);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _rd is not null)
        {
            if (_pipeline.IsValid) _rd.FreeRid(_pipeline);
            if (_shader.IsValid) _rd.FreeRid(_shader);
            if (_hazeBuffer.IsValid) _rd.FreeRid(_hazeBuffer);
            if (_depthSampler.IsValid) _rd.FreeRid(_depthSampler);
            if (_exposureBuffer.IsValid) _rd.FreeRid(_exposureBuffer);
        }
        base.Dispose(disposing);
    }
}
