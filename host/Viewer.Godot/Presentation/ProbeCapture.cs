// The capture follows the behaviour of the official viewer's default reflection probe (LLReflectionMapManager::updateProbeFace and LLViewerWindow::cubeSnapshot in the 26.4
// source): a 90 degree view from the probe's centre in each of the six cube directions, one face per frame, drawing only the terrain and the water. Nothing of that code is copied.
// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using Godot;
using Viewer.Core.World;

namespace Viewer.Godot.Presentation;

/// <summary>
/// The sky probe's capture of the world: six 128 x 128 views (a 90 degree view down each cube axis) from the probe's centre, which draw only what the capture layer holds (the terrain and
/// the water), one face per frame. The pictures are read back and kept as a <see cref="SkyProbe.WorldSample"/>: the colour the engine drew along a direction, or nothing where the sky
/// shows (the view's background is transparent). The view has no ambient light and its tone mapping is linear at a quarter of the exposure, so a value up to 4 fits in eight
/// bits; the face shaders see the capture layer in their camera's mask and light the view with the sun alone, as the official irradiance pass does.
/// </summary>
public sealed class ProbeCapture : IDisposable
{
    /// <summary>The render layer the capture's camera sees; the terrain and the water carry it besides their own layers, and the view's camera does not see it.</summary>
    public const uint Layer = 1u << 20;
    /// <summary>A layer nothing is drawn on, in the capture camera's mask besides <see cref="Layer"/>: the face shaders take a camera that sees none of layers 2 to 19 for the glow picture's camera (<see cref="FaceShader.GlowCamera"/>), which this one is not.</summary>
    private const uint NotGlow = 1u << 17;
    /// <summary>The exposure of the capture's linear tone mapping (a value up to 1 / exposure fits in eight bits).</summary>
    public const float Exposure = 1f;
    private const int Size = SkyProbe.Resolution;

    private readonly Node _parent;
    private readonly SubViewport _viewport;
    private readonly ViewportTexture _texture;
    private readonly Camera3D _camera;
    private readonly global::Godot.Environment _env;
    private readonly Compositor _compositor = new();   // none of the world's: its tone mapping post pass would change the values the capture reads back
    private readonly byte[][] _faces = new byte[6][];   // eight-bit pictures (display-encoded)
    private readonly float[][] _linear = new float[6][];   // or, when the engine gives a floating-point picture, its linear values
    private bool _isLinear;
    private string _format = "";
    private readonly Basis[] _bases = new Basis[6];
    private int _face = 6;   // the face being drawn; 6: none
    private Vector3 _origin;
    private bool _have;

    public ProbeCapture(Node parent, World3D world)
    {
        _parent = parent;
        _env = new global::Godot.Environment
        {
            BackgroundMode = global::Godot.Environment.BGMode.ClearColor,
            AmbientLightSource = global::Godot.Environment.AmbientSource.Disabled, ReflectedLightSource = global::Godot.Environment.ReflectionSource.Disabled,
            TonemapMode = global::Godot.Environment.ToneMapper.Linear, TonemapExposure = Exposure, FogSkyAffect = 0f,
        };
        _viewport = new SubViewport
        {
            Name = "sky_probe_capture", Size = new Vector2I(Size, Size), RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            Msaa3D = Viewport.Msaa.Disabled, ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled, UseTaa = false, World3D = world, TransparentBg = true, UseHdr2D = true,
        };
        _texture = _viewport.GetTexture();
        _camera = new Camera3D { Current = true, Fov = 90f, Near = 0.1f, Far = 512f, CullMask = Layer | NotGlow, Environment = _env, Compositor = _compositor };
        _viewport.AddChild(_camera);
        parent.AddChild(_viewport);
        // the cube's faces in the order of SkyProbe.ToFace: +X, -X, +Y, -Y, +Z, -Z
        Vector3[] looks = [Vector3.Right, Vector3.Left, Vector3.Up, Vector3.Down, Vector3.Back, Vector3.Forward];
        Vector3[] ups = [Vector3.Up, Vector3.Up, Vector3.Forward, Vector3.Back, Vector3.Up, Vector3.Up];
        for (int i = 0; i < 6; i++) _bases[i] = Basis.LookingAt(looks[i], ups[i]);
    }

    /// <summary>Whether the six faces are being drawn.</summary>
    public bool Busy => _face < 6;

    /// <summary>Whether a capture has been completed.</summary>
    public bool HasCapture => _have;

    /// <summary>The haze of the capture's view (the official irradiance pass draws it with grey blue horizon and blue density).</summary>
    public void SetHaze(Color colour, float energy, float density, bool enabled = true)
    {
        _env.FogEnabled = enabled;
        _env.FogLightColor = colour;
        _env.FogLightEnergy = energy;
        _env.FogDensity = density;
    }

    /// <summary>Starts a capture from <paramref name="origin"/>, seeing as far as <paramref name="far"/> metres.</summary>
    public void Begin(Vector3 origin, float far)
    {
        _origin = origin;
        _camera.Far = Math.Max(far, 1f);
        _face = -1;
        Tick();
    }

    /// <summary>Called once a frame: reads back the face drawn in the last frame and starts the next. The seventh call after a start leaves the capture complete.</summary>
    public void Tick()
    {
        if (_face >= 6) return;
        if (_face >= 0)
        {
            var img = _texture.GetImage();
            _format = img.GetFormat().ToString();
            _isLinear = img.GetFormat() != Image.Format.Rgba8;
            if (_isLinear)
            {
                if (img.GetFormat() != Image.Format.Rgbaf) img.Convert(Image.Format.Rgbaf);
                var raw = img.GetData();
                var f = new float[raw.Length / 4];
                Buffer.BlockCopy(raw, 0, f, 0, raw.Length);
                _linear[_face] = f;
            }
            else _faces[_face] = img.GetData();
        }
        _face++;
        if (_face >= 6) { _have = true; return; }
        _camera.Transform = new Transform3D(_bases[_face], _origin);
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
    }

    /// <summary>The engine's colour along a direction of the last complete capture (linear), or null where the sky shows.</summary>
    public SkyProbe.WorldSample Sample()
    {
        bool linear = _isLinear;
        var snapshot = new byte[6][];
        var floats = new float[6][];
        for (int i = 0; i < 6; i++)
        {
            if (linear) floats[i] = (float[])_linear[i].Clone(); else snapshot[i] = (byte[])_faces[i].Clone();
        }
        var bases = (Basis[])_bases.Clone();
        return d =>
        {
            var (face, _, _) = SkyProbe.ToFace(d);
            var b = bases[face];
            var dir = new Vector3(d.X, d.Y, d.Z);
            float along = dir.Dot(-b.Z);
            float u = dir.Dot(b.X) / along, v = dir.Dot(b.Y) / along;
            int px = Math.Clamp((int)MathF.Floor((u + 1f) * 0.5f * Size), 0, Size - 1), py = Math.Clamp((int)MathF.Floor((1f - v) * 0.5f * Size), 0, Size - 1);
            int k = (py * Size + px) * 4;
            if (linear)
            {
                if (floats[face][k + 3] < 0.5f) return null;   // nothing drawn there: the sky shows
                return new System.Numerics.Vector3(floats[face][k], floats[face][k + 1], floats[face][k + 2]) / Exposure;
            }
            if (snapshot[face][k + 3] < 128) return null;
            return new System.Numerics.Vector3(Decode(snapshot[face][k]), Decode(snapshot[face][k + 1]), Decode(snapshot[face][k + 2])) / Exposure;
        };
    }

    /// <summary>What each face holds: how many pixels are the background, how many are not, the mean of those that are not (sRGB bytes) and the most common pixel value (test report).</summary>
    public object Describe()
    {
        var rows = new System.Collections.Generic.List<object>();
        for (int f = 0; f < 6; f++)
        {
            long n = 0, bg = 0;
            double r = 0, g = 0, b = 0;
            for (int k = 0; k < Size * Size; k++)
            {
                double pr, pg, pb, a;
                if (_isLinear) { pr = _linear[f][k * 4]; pg = _linear[f][k * 4 + 1]; pb = _linear[f][k * 4 + 2]; a = _linear[f][k * 4 + 3] * 255.0; }
                else { pr = Decode(_faces[f][k * 4]); pg = Decode(_faces[f][k * 4 + 1]); pb = Decode(_faces[f][k * 4 + 2]); a = _faces[f][k * 4 + 3]; }
                if (a < 128) { bg++; continue; }
                n++; r += pr / Exposure; g += pg / Exposure; b += pb / Exposure;
            }
            rows.Add(new { face = f, format = _format, background = bg, drawn = n, mean_linear = n == 0 ? null : new[] { r / n, g / n, b / n } });
        }
        return rows;
    }

    private static float Decode(byte v)
    {
        float c = v / 255f;
        return c < 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    public void Dispose()
    {
        _parent.RemoveChild(_viewport);
        _viewport.Free();
        _env.Dispose();
        _compositor.Dispose();
    }
}
