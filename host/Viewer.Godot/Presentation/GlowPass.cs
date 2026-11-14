// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file glowF.glsl
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

// Adapted 2026 by Legion Builds. The blur shader (BlurCode: eight taps with the weights 0.25 0.5 0.8 1 1 0.8 0.5 0.25, summed and
// multiplied by a strength) follows glowF.glsl, translated from GLSL to Godot's shading language. The rest of this file is our own code.
// Copyright (c) 2026 Legion Builds

using System.Collections.Generic;
using Godot;
using Viewer.Core.World;

namespace Viewer.Godot.Presentation;

/// <summary>
/// The glow of faces whose glow value is above 0 (Docs/GLOW-SPEC.md). While the prim layer holds a glowing face, each
/// frame draws:
/// <list type="number">
/// <item>the glow picture: a second camera, with the view's position, lens and world, draws the scene into a viewport of
/// its own at twice the glow picture's width, seeing only <see cref="InGlowPicture"/>. The face shaders and the terrain shader tell
/// that camera by its cull mask (<see cref="FaceShader.GlowCamera"/>) and draw every face black but the glowing ones,
/// which draw their lit colour times their glow weight, so what the picture holds is hidden by whatever is in front of it
/// (the objects that can be in front of a glowing one, <see cref="PrimLayer.UpdateGlowPicture"/>, and the terrain);
/// water and avatars sit on layer 2 and are not in it;</item>
/// <item>four blurs of it at <see cref="GlowBlur.Resolution"/> pixels square, horizontal, vertical, horizontal, vertical,
/// each a viewport of its own drawing a fullscreen rectangle (<see cref="GlowBlur.Kernel"/>, <see cref="GlowBlur.Delta"/>,
/// <see cref="GlowBlur.Strength"/>); each viewport is a child of the one that uses its picture, so the engine draws them in
/// order within the frame;</item>
/// <item>the last blur added over the view's own picture, after its tone-mapping, by a rectangle with additive blending.</item>
/// </list>
/// With no glowing face nothing is made and nothing is drawn. The pass is attached to the viewport that draws the view
/// (the window's, or a test's).
/// </summary>
public sealed partial class GlowPass : Node
{
    /// <summary>The glow scene's negative control (--tr-alter=noglow): no glow is drawn.</summary>
    public static bool Disabled;

    /// <summary>Measurement: the engine's own timing of each viewport the pass draws (<see cref="PassMs"/>).</summary>
    public static bool MeasureTimes;

    /// <summary>The layer water and avatars are on: the view sees it, the glow camera does not.</summary>
    public const uint NotInGlowLayer = 2u;

    /// <summary>The layer (20) of what the glow picture draws: glowing objects, whatever can hide one from the eye
    /// (<see cref="GlowOccluders"/>) and composed terrain. Nothing else is on it, and the view's own camera sees the same
    /// things through layer 1; the glow camera's mask holding only this layer is what tells it from the view's
    /// (<see cref="FaceShader.GlowCamera"/>).</summary>
    public const uint InGlowPicture = 1u << 19;

    /// <summary>What the glow camera sees.</summary>
    public const uint CameraMask = InGlowPicture;
    public static uint TestCameraBits;

    private const int PictureWidth = 2 * GlowBlur.Resolution;

    private static readonly StringName Src = "src";
    private static readonly StringName Step = "step_uv";
    private static readonly StringName Strength = "strength";

    private const string BlurCode = @"
shader_type canvas_item;
render_mode blend_disabled;
uniform sampler2D src : filter_linear, repeat_disable;
uniform vec2 step_uv = vec2(0.0);
uniform float strength = 0.325;
void fragment() {
    float k[8] = float[8](0.25, 0.5, 0.8, 1.0, 1.0, 0.8, 0.5, 0.25);
    vec3 acc = vec3(0.0);
    for (int i = 0; i < 8; i++) { acc += k[i] * texture(src, UV + step_uv * (float(i) - 3.5)).rgb; }
    COLOR = vec4(clamp(acc * strength, 0.0, 1.0), 1.0);
}
";

    private const string AddCode = @"
shader_type canvas_item;
render_mode blend_add;
uniform sampler2D src : filter_linear, repeat_disable;
void fragment() { COLOR = vec4(texture(src, UV).rgb, 1.0); }
";

    private static Shader? s_blur, s_add;   // made once, kept for the process

    private readonly PrimLayer? _prims;
    // the view's world and the node holding its environment, handed in by whoever makes the view: the pass reads neither back
    // from the viewport (a read-back hands out a fresh managed wrapper each time)
    private readonly World3D _world;
    private readonly WorldEnvironment _viewEnvironment;
    private readonly ViewportTexture?[] _textures = new ViewportTexture?[GlowBlur.Passes + 1];   // the picture's, then each blur's, read once when made
    private int _pictureWidth = PictureWidth;   // smaller in the warm-up: the pipelines do not depend on the size
    private bool _forced;   // the pipeline warm-up draws the pass whatever the scene holds
    private SubViewport? _picture;
    private readonly SubViewport?[] _blurs = new SubViewport?[GlowBlur.Passes];
    private Camera3D? _camera;
    private global::Godot.Environment? _env;
    private CanvasLayer? _layer;
    private ColorRect? _out;
    private readonly List<ShaderMaterial> _materials = new();
    private bool _active;

    /// <summary>The prim layers of the other regions in view (neighbours): their glowing faces glow too, and what is in front of them hides the glow.</summary>
    public IReadOnlyList<PrimLayer> OtherPrims { get; set; } = [];
    private readonly List<Viewer.Core.Residency.Bounds> _otherTargets = new();

    private bool AnyGlowObjects()
    {
        if (_prims is not null && _prims.GlowObjects > 0) return true;
        foreach (var p in OtherPrims) if (p.GlowObjects > 0) return true;
        return false;
    }

    public GlowPass(PrimLayer? prims, World3D world, WorldEnvironment viewEnvironment)
    {
        _prims = prims;
        _world = world;
        _viewEnvironment = viewEnvironment;
        ProcessPriority = 1000;   // after whatever moves the view's camera this frame
    }

    /// <summary>Whether the pass is drawing this frame.</summary>
    public bool Active => _active;

    /// <summary>The draw calls the frame's glow viewports made, all together (zero while the pass is not drawing).</summary>
    public double PassDrawCalls()
    {
        if (_picture is null || !_active) return 0;
        double n = 0;
        foreach (var vp in _blurs.Prepend(_picture))
            n += RenderingServer.ViewportGetRenderInfo(vp!.GetViewportRid(), RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.DrawCallsInFrame);
        return n;
    }

    /// <summary>The engine's measured time for the frame's glow viewports together, in milliseconds on the CPU and on the GPU
    /// (zero while the pass is not built or not timed).</summary>
    public (double Cpu, double Gpu) PassMs()
    {
        double cpu = 0, gpu = 0;
        if (_picture is null || !_active) return (0, 0);
        foreach (var vp in _blurs.Prepend(_picture))
        {
            var rid = vp!.GetViewportRid();
            cpu += RenderingServer.ViewportGetMeasuredRenderTimeCpu(rid);
            gpu += RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid);
        }
        return (cpu, gpu);
    }

    /// <summary>The glow picture before the blur (the test render saves it for review), or null while the pass has not been built.</summary>
    public Image? PictureImage() => _textures[0]?.GetImage();

    /// <summary>The last blur, as it is added over the view, or null while the pass has not been built.</summary>
    public Image? BlurredImage() => _textures[^1]?.GetImage();

    public override void _Process(double delta)
    {
        var main = GetViewport().GetCamera3D();
        bool want = _forced ? main is not null : !Disabled && _prims is not null && AnyGlowObjects() && main is not null && GlowInView(main);
        if (want && _picture is null) Build();
        if (want != _active) Set(want);
        if (!_active) return;
        Follow(GetViewport().GetCamera3D()!);
    }

    /// <summary>Whether a glowing object can show in the view (its halo reaches a little beyond it, which the test allows for).</summary>
    private bool GlowInView(Camera3D main)
    {
        if (main.Projection != Camera3D.ProjectionType.Perspective) return true;
        var size = GetViewport().GetVisibleRect().Size;
        float aspect = size.X / System.Math.Max(1f, size.Y);
        float t = System.MathF.Tan(main.Fov * System.MathF.PI / 360f);
        float tanX = main.KeepAspect == Camera3D.KeepAspectEnum.Height ? t * aspect : t, tanY = main.KeepAspect == Camera3D.KeepAspectEnum.Height ? t : t / aspect;
        var xf = main.GlobalTransform;
        System.Numerics.Vector3 N(Vector3 v) => new(v.X, v.Y, v.Z);
        var eye = N(xf.Origin);
        var forward = N(-xf.Basis.Z);
        var right = N(xf.Basis.X);
        var up = N(xf.Basis.Y);
        foreach (var s in _prims!.GlowSpheres)
            if (ViewFrustum.SphereVisible(eye, forward, right, up, tanX, tanY, main.Near, main.Far, N(s.Centre), s.Radius))
                return true;
        foreach (var p in OtherPrims)
            foreach (var s in p.GlowSpheres)
                if (ViewFrustum.SphereVisible(eye, forward, right, up, tanX, tanY, main.Near, main.Far, N(s.Centre), s.Radius))
                    return true;
        return false;
    }

    /// <summary>The pass's two shaders, made once for the process (<see cref="KeptShaders.PrepareAtStartup"/>) and never freed.</summary>
    internal static void PrepareShaders()
    {
        s_blur ??= new Shader { Code = BlurCode };
        s_add ??= new Shader { Code = AddCode };
    }

    /// <summary>Draws the whole pass for a few frames whatever the scene holds, so the engine compiles its pipelines before the
    /// first glow is seen (the pipeline warm-up). The pass must be in the tree.</summary>
    internal void WarmUp()
    {
        _forced = true;
        _pictureWidth = 64;
        if (_picture is null) Build();
        Set(true);
    }

    private void Build()
    {
        PrepareShaders();
        // the chain, innermost first: the picture inside the first blur inside the second ... the last blur is the pass's child
        _picture = new SubViewport
        {
            Name = "glow_picture", Size = new Vector2I(PictureWidth, PictureWidth * 9 / 16), RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            Msaa3D = Viewport.Msaa.Disabled, ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled, UseTaa = false, Scaling3DScale = 1f,
            World3D = _world,
        };
        _textures[0] = _picture.GetTexture();
        _camera = new Camera3D { Current = true, CullMask = CameraMask | TestCameraBits };
        _env = new global::Godot.Environment
        {
            BackgroundMode = global::Godot.Environment.BGMode.Color, BackgroundColor = Colors.Black, FogEnabled = false,
            ReflectedLightSource = global::Godot.Environment.ReflectionSource.Disabled, TonemapMode = global::Godot.Environment.ToneMapper.Linear,
        };
        _camera.Environment = _env;
        _picture.AddChild(_camera);
        Node inner = _picture;
        SubViewport? previous = null;
        for (int i = 0; i < GlowBlur.Passes; i++)
        {
            var vp = new SubViewport
            {
                Name = "glow_blur_" + i, Size = new Vector2I(GlowBlur.Resolution, GlowBlur.Resolution), Disable3D = true,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled, TransparentBg = false,
            };
            var rect = new ColorRect { Color = Colors.Black, Size = vp.Size };
            var m = new ShaderMaterial { Shader = s_blur };
            VariantDisposal.SetShaderParameter(m, Src, _textures[i]!);
            m.SetShaderParameter(Step, i % 2 == 0 ? new Vector2(GlowBlur.Delta, 0f) : new Vector2(0f, GlowBlur.Delta));
            m.SetShaderParameter(Strength, GlowBlur.Strength);
            rect.Material = m;
            _materials.Add(m);
            vp.AddChild(inner);
            vp.AddChild(rect);
            inner = vp;
            previous = vp;
            _blurs[i] = vp;
            _textures[i + 1] = vp.GetTexture();
        }
        if (MeasureTimes)
            foreach (var vp in _blurs.Prepend(_picture))
                RenderingServer.ViewportSetMeasureRenderTime(vp!.GetViewportRid(), true);
        AddChild(inner);   // the outermost blur: its parent viewport is the view's, which draws it before itself
        _layer = new CanvasLayer { Layer = 100, Visible = false };
        _out = new ColorRect { Color = Colors.White, MouseFilter = Control.MouseFilterEnum.Ignore };   // the shader draws it; its size is set to the view's each frame
        var add = new ShaderMaterial { Shader = s_add };
        VariantDisposal.SetShaderParameter(add, Src, _textures[^1]!);
        _out.Material = add;
        _materials.Add(add);
        _layer.AddChild(_out);
        AddChild(_layer);
    }

    private void Set(bool on)
    {
        _active = on;
        var mode = on ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        if (_picture is null) return;
        _picture.RenderTargetUpdateMode = mode;
        foreach (var b in _blurs) b!.RenderTargetUpdateMode = mode;
        _layer!.Visible = on;
    }

    /// <summary>The glow camera takes the view's camera and lighting, and the picture the view's shape.</summary>
    private void Follow(Camera3D main)
    {
        var host = GetViewport();
        var size = host.GetVisibleRect().Size;
        int h = System.Math.Max(2, (int)System.MathF.Round(_pictureWidth * size.Y / System.Math.Max(1f, size.X)));
        if (_picture!.Size != new Vector2I(_pictureWidth, h)) _picture.Size = new Vector2I(_pictureWidth, h);
        _out!.Position = Vector2.Zero;
        _out.Size = size;   // the rectangle covers the view
        var c = _camera!;
        c.GlobalTransform = main.GlobalTransform;
        c.Projection = main.Projection;
        c.Fov = main.Fov;
        c.Size = main.Size;
        c.KeepAspect = main.KeepAspect;
        c.Near = main.Near;
        c.Far = main.Far;
        c.FrustumOffset = main.FrustumOffset;
        c.HOffset = main.HOffset;
        c.VOffset = main.VOffset;
        // before the engine draws the picture this frame: each region's layer chooses its objects, knowing the other regions' glowing ones
        var eyeAt = main.GlobalTransform.Origin;
        if (OtherPrims.Count == 0) _prims?.UpdateGlowPicture(eyeAt);
        else
        {
            if (_prims is not null)
            {
                _otherTargets.Clear();
                foreach (var p in OtherPrims) p.AppendGlowTargets(_otherTargets);
                _prims.UpdateGlowPicture(eyeAt, _otherTargets);
            }
            for (int i = 0; i < OtherPrims.Count; i++)
            {
                _otherTargets.Clear();
                _prims?.AppendGlowTargets(_otherTargets);
                for (int k = 0; k < OtherPrims.Count; k++) if (k != i) OtherPrims[k].AppendGlowTargets(_otherTargets);
                OtherPrims[i].UpdateGlowPicture(eyeAt, _otherTargets);
            }
        }
        // the view's ambient light, so a glowing face is as lit as it shows
        {
            var e = Stage.EnvironmentOf(_viewEnvironment);
            _env!.AmbientLightSource = e.AmbientLightSource;
            _env.AmbientLightColor = e.AmbientLightColor;
            _env.AmbientLightEnergy = e.AmbientLightEnergy;
            if (e.AmbientLightSource == global::Godot.Environment.AmbientSource.Sky) _env.Sky = e.Sky;
            _env.TonemapExposure = e.TonemapExposure;
        }
    }

    public override void _ExitTree()
    {
        _active = false;
        foreach (var m in _materials) m.Dispose();
        _materials.Clear();
        _env?.Dispose();
        _env = null;
    }
}
