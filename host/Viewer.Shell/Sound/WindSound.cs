// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Shell.Sound;

/// <summary>
/// The wind sound as the official viewer makes it (behaviour taken from llvieweraudio.cpp audio_update_wind, llaudioengine.cpp
/// mapWindVecToGain, mapWindVecToPitch and mapWindVecToPan, llaudioengine_openal.cpp updateWind; written here from that behaviour,
/// no code copied):
/// <list type="bullet">
/// <item>the wind felt is the region's wind at the agent scaled by the wind level (0.5), less the agent's own velocity, turned into
/// the agent's frame (x forward, y left, z up);</item>
/// <item>its gain is its speed over 20 m/s, at most 1; its pan is the angle between it and the listener's right, over pi; its pitch
/// is that angle folded to 0..0.5; the filter's centre frequency is 80 Hz times (1 + pitch) to the power 2.5 x (gain + 1);</item>
/// <item>the loudest it may be is master x ambient; it fades up towards that while the agent flies and down to silence when it does not,
/// a full swing taking 2 seconds;</item>
/// <item>the sound itself is white noise, pinked, through a two-pole resonant filter 50 Hz wide at the centre frequency, its gain,
/// pan and frequency eased towards their targets sample by sample (<see cref="WindGenerator"/>).</item>
/// </list>
/// </summary>
public static class WindSound
{
    /// <summary>How much of the region's wind is heard (the official AudioLevelWind default).</summary>
    public const float WindLevel = 0.5f;

    /// <summary>The wind speed at which the gain reaches 1.</summary>
    public const float FullGainSpeed = 20f;

    /// <summary>The seconds a full fade between silence and the loudest takes.</summary>
    public const float FadeSeconds = 2f;

    /// <summary>The wind the agent feels, in its own frame (x forward, y left, z up).</summary>
    public static Vector3 Relative(Vector3 regionWind, Vector3 agentVelocity, Quaternion agentRotation) =>
        Vector3.Transform(regionWind * WindLevel - agentVelocity, Quaternion.Conjugate(Quaternion.Normalize(agentRotation)));

    /// <summary>The gain of the relative wind, 0..1.</summary>
    public static float Gain(Vector3 relative) => float.IsFinite(relative.Length()) ? MathF.Min(relative.Length(), FullGainSpeed) / FullGainSpeed : 0f;

    /// <summary>The angle between the relative wind and the listener's right, over pi (0..1; 0.5 when there is no wind).</summary>
    public static float Pan(Vector3 relative)
    {
        // the listener's right is -y in the agent's frame (x forward, y left); no wind is a right angle to it
        float length = relative.Length();
        float cos = length > 1e-6f && float.IsFinite(length) ? Math.Clamp(-relative.Y / length, -1f, 1f) : 0f;
        return MathF.Acos(cos) / MathF.PI;
    }

    /// <summary>The pan folded to 0..0.5.</summary>
    public static float Pitch(Vector3 relative)
    {
        float pan = Pan(relative);
        return Math.Max(0f, pan > 0.5f ? 1f - pan : pan);
    }

    /// <summary>The filter's centre frequency in Hz.</summary>
    public static float CentreFrequency(Vector3 relative) => 80f * MathF.Pow(1f + Pitch(relative), 2.5f * (Gain(relative) + 1f));

    /// <summary>The loudest the wind may be after one frame of <paramref name="seconds"/>: towards master x ambient while flying, towards 0
    /// otherwise, by that frame's share of a two-second fade.</summary>
    public static float StepMaxGain(float current, bool flying, float master, float ambient, float seconds)
    {
        float most = Math.Clamp(master, 0f, 1f) * Math.Clamp(ambient, 0f, 1f);
        float delta = Math.Max(0f, seconds) / FadeSeconds * most;
        return flying ? Math.Min(current + delta, most) : Math.Max(Math.Min(current, most) - delta, 0f);
    }
}

/// <summary>
/// The wind's noise, stereo, at <see cref="SampleRate"/>: see <see cref="WindSound"/>. Each synthesized sample is spread over
/// <see cref="SubSamples"/> output frames. Mono puts the same sample on both sides. Not thread-safe; one owner.
/// </summary>
public sealed class WindGenerator
{
    public const int SampleRate = 44100;
    public const int SubSamples = 2;
    public const float BandWidth = 50f;

    /// <summary>The gain, centre frequency and right-side share the sound eases towards.</summary>
    public float TargetGain { get; set; }
    public float TargetFrequency { get; set; } = 100f;
    public float TargetPanRight { get; set; } = 0.5f;

    /// <summary>Where the easing has got to.</summary>
    public float Gain { get; private set; }
    public float Frequency { get; private set; } = 100f;
    public float PanRight { get; private set; } = 0.5f;

    /// <summary>The filter's coefficients at <paramref name="frequency"/>: (a0, b1, b2).</summary>
    public static (float A0, float B1, float B2) Coefficients(float frequency)
    {
        float period = (float)SubSamples / SampleRate;
        float b2 = MathF.Exp(-2f * MathF.PI * BandWidth * period);
        float b1 = -4f * b2 / (1f + b2) * MathF.Cos(2f * MathF.PI * frequency * period);
        float a0 = (1f - b2) * MathF.Sqrt(1f - b1 * b1 / (4f * b2));
        return (a0, b1, b2);
    }

    private readonly Random _noise = new();
    private float _pink0, _pink1, _pink2, _y0, _y1, _last;
    private int _sub;
    private float _step;

    /// <summary>Fills <paramref name="left"/> and <paramref name="right"/> (the same length) with the next frames.</summary>
    public void Fill(Span<float> left, Span<float> right, bool mono)
    {
        int n = Math.Min(left.Length, right.Length);
        // a frequency close to the target is taken at once; a far one is eased towards it sample by sample
        bool ease = MathF.Abs(TargetFrequency - Frequency) >= Frequency * 0.112f;
        if (!ease)
            Frequency = TargetFrequency;
        var (a0, b1, b2) = Coefficients(Frequency);
        for (int i = 0; i < n; i++)
        {
            if (_sub == 0)
            {
                // white noise, pinked (Paul Kellet's economy pinking filter), then the resonant filter
                float white = (float)_noise.NextDouble() - 0.5f;
                _pink0 = _pink0 * 0.99765f + white * 0.0990460f;
                _pink1 = _pink1 * 0.96300f + white * 0.2965164f;
                _pink2 = _pink2 * 0.57000f + white * 1.0526913f;
                float pink = _pink0 + _pink1 + _pink2 + white * 0.1848f;
                if (ease)
                {
                    Frequency = 0.999f * Frequency + 0.001f * TargetFrequency;
                    (a0, b1, b2) = Coefficients(Frequency);
                }
                float filtered = a0 * pink - b1 * _y0 - b2 * _y1;
                _y1 = _y0;
                _y0 = filtered;
                Gain = 0.999f * Gain + 0.001f * TargetGain;
                PanRight = 0.999f * PanRight + 0.001f * TargetPanRight;
                // the synthesized sample is spread over the sub-samples by a straight line from the last one
                _step = (filtered * Gain - _last) / SubSamples;
                _sub = SubSamples;
            }
            _last += _step;
            _sub--;
            if (mono)
            {
                left[i] = _last * 0.5f;
                right[i] = _last * 0.5f;
            }
            else
            {
                right[i] = _last * PanRight;
                left[i] = _last - right[i];
            }
        }
    }
}
