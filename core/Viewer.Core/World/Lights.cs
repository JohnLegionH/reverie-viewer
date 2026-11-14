// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file llvovolume.cpp
 * @brief LLVOVolume class implementation
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

// Adapted 2026 by Legion Builds. LightRules.SpotLightPriority follows LLVOVolume::updateSpotLightPriority (llvovolume.cpp) and
// LLPipeline::calcPixelArea (pipeline.cpp), translated to C# with the pixel angle left out (only the order matters). The rest of this
// file (the light reach and falloff numbers, the nearest-lights choice, the engine-light fit) is our own code.
// Copyright (c) 2026 Legion Builds

using System.Numerics;

namespace Viewer.Core.World;

/// <summary>A prim's light as the simulator sends it: linear colour (0 to 1 per channel, not gamma encoded), the intensity
/// that scales it, the radius in metres, the falloff (0 to 1, how soon the light fades inside the radius) and the cutoff
/// (carried, unused by the drawing: the official viewer's lighting no longer reads it).</summary>
public readonly record struct PrimLight(Vector3 Color, float Intensity, float Radius, float Falloff, float Cutoff);

/// <summary>A prim's projector: the texture it casts, the vertical field of view in radians, the focus (metres from the
/// prim's face at which the picture starts to blur) and the ambiance (how much of the picture's average colour lights
/// the lit area besides the picture itself). A projector only counts on a prim that also has a <see cref="PrimLight"/>.</summary>
public readonly record struct PrimProjector(Guid Texture, float Fov, float Focus, float Ambiance);

/// <summary>
/// The projector beam in the prim's own axes (SL: the light shines along the prim's -Z): its apex behind the prim's face,
/// the near and far distances measured from the apex, and the aspect of the picture (the prim's X over its Y).
/// </summary>
public readonly record struct Beam(Vector3 Apex, float Near, float Far, float Fov, float Aspect, float Focus, float Ambiance);

/// <summary>What an engine's light needs, worked out from a prim's light (Docs/PROJECTOR-SPEC.md). Colour is gamma encoded for an
/// engine that takes light colours the way a colour picker gives them.</summary>
public readonly record struct EngineLight(Vector3 ColourSrgb, float Energy, float Range, float Attenuation, bool IsSpot, float SpotHalfAngle, Beam? Beam);

/// <summary>The official viewer's local-light and projector rules as plain numbers (Docs/PROJECTOR-SPEC.md).</summary>
public static class LightRules
{
    /// <summary>The viewer draws a light out to 1.5 times its radius.</summary>
    public const float ReachScale = 1.5f;
    /// <summary>The falloff it uses is half the simulator's value.</summary>
    public const float FalloffScale = 0.5f;
    /// <summary>The widest cone half-angle an engine spot light can take, in radians (89 degrees).</summary>
    public const float MaxHalfAngle = 1.5533430f;

    /// <summary>How many local lights are drawn: the official viewer's RenderLocalLightCount setting (default 256, app_settings/settings.xml;
    /// the deferred renderer in pipeline.cpp keeps the lights nearest the camera and draws that many of them).</summary>
    public const int MaxLocalLights = 256;

    /// <summary>The ids of the (at most <paramref name="cap"/>) lights nearest <paramref name="eye"/>; ties go to the lower id, so the choice is stable.</summary>
    public static HashSet<uint> Nearest(IReadOnlyCollection<(uint Id, Vector3 Position)> lights, Vector3 eye, int cap)
    {
        if (cap <= 0) return [];
        if (lights.Count <= cap) return lights.Select(l => l.Id).ToHashSet();
        return lights.OrderBy(l => Vector3.DistanceSquared(l.Position, eye)).ThenBy(l => l.Id).Take(cap).Select(l => l.Id).ToHashSet();
    }

    /// <summary>How many projectors cast shadows at once: the official viewer keeps two spot-light shadow maps (pipeline.cpp, allocated when
    /// RenderShadowDetail is above 1, which is its default of 2).</summary>
    public const int MaxShadowedProjectors = 2;

    /// <summary>The official viewer's ranking of a projector for a shadow map (LLVOVolume::updateSpotLightPriority): the on-screen area of a sphere of
    /// the light's radius centred on the point <paramref name="radius"/> along the shining direction from the light and <paramref name="radius"/> back
    /// along the camera's view direction. A distance under 16 m is shrunk (distance squared over 16), so near lights rank high. Only the order matters,
    /// so the screen's pixel angle is left out.</summary>
    public static float SpotLightPriority(Vector3 position, Vector3 shine, float radius, Vector3 eye, Vector3 cameraAt)
    {
        var centre = position + shine * radius - cameraAt * radius;
        float dist = (centre - eye).Length();
        if (dist < 16f) dist = dist / 16f * dist;
        float angle = MathF.Atan(new Vector3(radius).Length() / MathF.Max(dist, 1e-4f));
        return angle * angle;
    }

    /// <summary>The ids of the (at most <paramref name="cap"/>) projectors that cast shadows: the ones with the highest <see cref="SpotLightPriority"/>;
    /// ties go to the lower id, so the choice is stable.</summary>
    public static HashSet<uint> ShadowCasters(IReadOnlyCollection<(uint Id, Vector3 Position, Vector3 Shine, float Radius)> projectors, Vector3 eye, Vector3 cameraAt, int cap)
    {
        if (cap <= 0) return [];
        if (projectors.Count <= cap) return projectors.Select(p => p.Id).ToHashSet();
        return projectors.OrderByDescending(p => SpotLightPriority(p.Position, p.Shine, p.Radius, eye, cameraAt)).ThenBy(p => p.Id)
            .Take(cap).Select(p => p.Id).ToHashSet();
    }

    public static float Reach(PrimLight l) => l.Radius * ReachScale;

    public static Vector3 LinearColour(PrimLight l) => l.Color * l.Intensity;

    /// <summary>Distance attenuation at <paramref name="distance"/> metres from the prim's centre: 0 at the reach and beyond.
    /// With d the distance over the reach and f the falloff in use, it is twice the square of (1 - (d + f) / (1 + f)); for d up
    /// to 1 that is 2 / (1 + f)^2 times (1 - d)^2.</summary>
    public static float Attenuation(PrimLight l, float distance)
    {
        float reach = Reach(l);
        if (reach <= 0f || distance >= reach) return 0f;
        float f = l.Falloff * FalloffScale;
        float t = 1f - Math.Clamp((distance / reach + f) / (1f + f), 0f, 1f);
        return 2f * t * t;
    }

    /// <summary>The projector's beam for a prim of <paramref name="scale"/>; null when the light has no projector texture.
    /// The prim's face (its -Z side) is the picture's window: its Y extent is the window's height at the field of view,
    /// which puts the apex that far behind the face.</summary>
    public static Beam? BeamOf(PrimLight l, PrimProjector? p, Vector3 scale)
    {
        if (p is not { } pr || pr.Texture == Guid.Empty) return null;
        float fov = Math.Clamp(pr.Fov, 0.01f, 3.1f);
        float near = scale.Y * 0.5f / MathF.Tan(fov * 0.5f);
        var apex = new Vector3(0f, 0f, -scale.Z * 0.5f + near);
        float far = Reach(l) + near - scale.Z * 0.5f;
        float aspect = scale.Y > 0f ? scale.X / scale.Y : 1f;
        return new Beam(apex, near, far, fov, aspect, pr.Focus, pr.Ambiance);
    }

    /// <summary>The engine-light numbers for a prim of <paramref name="scale"/>; null when the light cannot be seen (no colour or no reach).
    /// A point light is an omni light of the reach, the engine curve fitted to the viewer's (see <see cref="FitExponent"/>) and the colour's brightest
    /// channel times 2 / (1 + f)^2 (scaled by the fit) as its energy. A projector is a spot light from the beam's apex: its range ends where the
    /// light's reach does, measured from the apex as the engine does, and its
    /// cone holds the picture's corners (the engine's cone is round, the picture a rectangle).</summary>
    public static EngineLight? ForEngine(PrimLight l, PrimProjector? p, Vector3 scale)
    {
        var linear = LinearColour(l);
        float peak = MathF.Max(linear.X, MathF.Max(linear.Y, linear.Z));
        float reach = Reach(l);
        if (peak <= 0.001f || reach <= 0.001f) return null;
        float f = l.Falloff * FalloffScale;
        float factor = peak * 2f / ((1f + f) * (1f + f));
        var colour = new Vector3(LinearToSrgb(linear.X / peak), LinearToSrgb(linear.Y / peak), LinearToSrgb(linear.Z / peak));
        if (BeamOf(l, p, scale) is not { } beam)
            return new EngineLight(colour, EngineEnergy(factor, reach), FitRange * reach, FitExponent, false, 0f, null);
        float far = beam.Far;   // the engine measures from the apex; the light is dark at the reach from the prim's centre
        float corner = MathF.Tan(beam.Fov * 0.5f) * MathF.Sqrt(1f + beam.Aspect * beam.Aspect);
        float half = MathF.Min(MathF.Atan(corner), MaxHalfAngle);
        return new EngineLight(colour, EngineEnergy(factor, far), FitRange * far, FitExponent, true, half, beam);
    }

    // The engine's distance curve is a window, (1 - (d / range)^4) squared, over d to an exponent. The viewer's is
    // (1 - d / reach) squared. These three numbers fit one to the other over the reach (found by a least-squares search on
    // the log of the ratio at d = 0.05 to 0.95 of the reach: within about a quarter of the viewer's value throughout).
    public const float FitExponent = 0.8f, FitRange = 0.98f, FitScale = 0.144f;

    /// <summary>The engine energy that gives the viewer's brightness factor at the reach's scale: the curve's exponent works on metres.</summary>
    private static float EngineEnergy(float factor, float reach) => factor * FitScale * MathF.Pow(reach, FitExponent);

    /// <summary>The standard linear to sRGB curve, for engines that read a light colour as a colour-picker value.</summary>
    public static float LinearToSrgb(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        return v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
    }
}
