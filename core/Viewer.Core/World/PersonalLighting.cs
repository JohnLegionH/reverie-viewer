// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.World;

/// <summary>
/// The controls of the Personal Lighting window, named as the official window's (floater_adjust_environment.xml): five colour swatches and
/// the sliders for haze, clouds, scene gamma and the glow.
/// </summary>
public enum PersonalField { Ambient, BlueHorizon, BlueDensity, SunColor, CloudColor, HazeHorizon, HazeDensity, CloudCoverage, CloudScale, SceneGamma, GlowSize, GlowFocus }

/// <summary>
/// The Personal Lighting window's model. Behaviour reference: the official viewer's Personal Lighting window and its code
/// (floater_adjust_environment.xml and llfloaterenvironmentadjust.cpp: the controls, their ranges, the scale between a swatch and the
/// setting it edits, the glow's two sliders). What is set here is laid over the sky frame in force and is this viewer's own; <see cref="Reset"/> puts
/// the region's environment back. Main thread only.
/// </summary>
public sealed class PersonalLighting
{
    private readonly Dictionary<PersonalField, double> _values = [];
    private readonly Dictionary<PersonalField, Vector3> _colours = [];

    /// <summary>Raised after any change, including a reset that had something to put back.</summary>
    public event Action? Changed;

    /// <summary>Whether anything is set (the window changes the frame).</summary>
    public bool IsActive => _values.Count > 0 || _colours.Count > 0;

    /// <summary>The scene gamma in force (1 when none is set; the official legacy correction is <see cref="GammaCurve"/>).</summary>
    public float Gamma => _values.TryGetValue(PersonalField.SceneGamma, out var g) ? (float)g : 1f;

    /// <summary>The official slider range of a control: minimum, maximum and step (a colour swatch has 0 to 1 in 0.01).</summary>
    public static (double Min, double Max, double Step) Range(PersonalField field) => field switch
    {
        PersonalField.HazeHorizon or PersonalField.HazeDensity => (0, 5, 0.01),
        PersonalField.CloudCoverage => (0, 1, 0.01),
        PersonalField.CloudScale => (0.01, 3, 0.01),
        PersonalField.SceneGamma => (0, 20, 0.01),
        PersonalField.GlowSize => (0, 1.99, 0.01),
        PersonalField.GlowFocus => (-2, 2, 0.01),
        _ => (0, 1, 0.01),
    };

    /// <summary>How much a swatch's colour is scaled up into the setting it edits: ambient and sun colour 3, blue horizon and density 2, cloud colour 1.</summary>
    public static float ColourScale(PersonalField field) => field switch
    {
        PersonalField.Ambient or PersonalField.SunColor => 3f,
        PersonalField.BlueHorizon or PersonalField.BlueDensity => 2f,
        PersonalField.CloudColor => 1f,
        _ => 0f,
    };

    /// <summary>Sets a slider (kept inside its range); a value that is not a number changes nothing.</summary>
    public void SetValue(PersonalField field, double value)
    {
        if (!double.IsFinite(value)) return;
        var (min, max, _) = Range(field);
        _values[field] = Math.Clamp(value, min, max);
        Changed?.Invoke();
    }

    /// <summary>Sets a colour swatch (each channel 0 to 1); the setting is the swatch times <see cref="ColourScale"/>.</summary>
    public void SetColour(PersonalField field, Vector3 swatch)
    {
        _colours[field] = Vector3.Clamp(swatch, Vector3.Zero, Vector3.One) * ColourScale(field);
        Changed?.Invoke();
    }

    /// <summary>The frame with what is set laid over it.</summary>
    public SkyFrame Apply(SkyFrame frame)
    {
        if (!IsActive) return frame;
        var f = frame;
        if (_colours.TryGetValue(PersonalField.Ambient, out var a)) f = f with { Ambient = a };
        if (_colours.TryGetValue(PersonalField.SunColor, out var s)) f = f with { SunColor = s };
        if (_colours.TryGetValue(PersonalField.BlueHorizon, out var bh)) f = f with { BlueHorizon = bh };
        if (_colours.TryGetValue(PersonalField.BlueDensity, out var bd)) f = f with { BlueDensity = bd };
        if (_colours.TryGetValue(PersonalField.CloudColor, out var cc)) f = f with { Clouds = f.Cloud with { Color = cc } };
        if (_values.TryGetValue(PersonalField.HazeHorizon, out var hh)) f = f with { HazeHorizon = (float)hh };
        if (_values.TryGetValue(PersonalField.HazeDensity, out var hd)) f = f with { HazeDensity = (float)hd };
        if (_values.TryGetValue(PersonalField.CloudCoverage, out var cs)) f = f with { CloudShadow = (float)cs };
        if (_values.TryGetValue(PersonalField.CloudScale, out var sc)) f = f with { Clouds = f.Cloud with { Scale = (float)sc } };
        if (_values.ContainsKey(PersonalField.GlowSize) || _values.ContainsKey(PersonalField.GlowFocus))
        {
            // the official window: glow = ((2 - size) * 20, 0, focus * -5); a slider not touched keeps what the frame has
            double size = _values.TryGetValue(PersonalField.GlowSize, out var gs) ? gs : 2.0 - f.Glow.X / 20.0;
            double focus = _values.TryGetValue(PersonalField.GlowFocus, out var gf) ? gf : f.Glow.Z / -5.0;
            f = f with { GlowParameters = new Vector3((float)((2.0 - size) * 20.0), 0f, (float)(focus * -5.0)) };
        }
        return f;
    }

    /// <summary>What a slider shows: what is set, else what the frame in force has.</summary>
    public double Value(PersonalField field, SkyFrame frame)
    {
        if (_values.TryGetValue(field, out var v)) return v;
        return field switch
        {
            PersonalField.HazeHorizon => frame.HazeHorizon,
            PersonalField.HazeDensity => frame.HazeDensity,
            PersonalField.CloudCoverage => frame.CloudShadow,
            PersonalField.CloudScale => frame.Cloud.Scale,
            PersonalField.SceneGamma => 1.0,
            PersonalField.GlowSize => 2.0 - frame.Glow.X / 20.0,
            PersonalField.GlowFocus => frame.Glow.Z / -5.0,
            _ => 0.0,
        };
    }

    /// <summary>What a colour swatch shows: the setting divided by its scale.</summary>
    public Vector3 Colour(PersonalField field, SkyFrame frame)
    {
        float scale = ColourScale(field);
        if (scale <= 0f) return default;
        var setting = _colours.TryGetValue(field, out var c) ? c : field switch
        {
            PersonalField.Ambient => frame.Ambient,
            PersonalField.SunColor => frame.SunColor,
            PersonalField.BlueHorizon => frame.BlueHorizon,
            PersonalField.BlueDensity => frame.BlueDensity,
            _ => frame.Cloud.Color,
        };
        return setting / scale;
    }

    /// <summary>Puts the region's environment back.</summary>
    public void Reset()
    {
        if (!IsActive) return;
        _values.Clear(); _colours.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// The official legacy gamma correction of a displayed channel value (0 to 1): <c>1 - (1 - x)^gamma</c>
    /// (postDeferredGammaCorrect.glsl, with the gamma the sky settings carry); 1 changes nothing, more is brighter.
    /// </summary>
    public static float GammaCurve(float gamma, float x) => 1f - MathF.Pow(1f - Math.Clamp(x, 0f, 1f), gamma);
}
