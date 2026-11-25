// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-only

using System.Numerics;

namespace Viewer.Core.World;

/// <summary>Stub.</summary>
public static class UnderwaterView
{
    public static bool EyeBelow(float eyeZ, float? waterHeight) => false;
    public static float HeightAt(float x, float y, float homeHeight, IReadOnlyList<WaterRect> others) => 0f;
    public static float FogDensity(float density, float fogMod, bool underwater) => 0f;
    public static (Vector3 Light, float Survives) Fog(Vector3 fogColour, float density, float slant, float eyeDepth, float rayUp, float distance) => (default, 0f);
    public static Vector3 Haze(Vector3 behind, Vector3 light, float survives) => default;
    public static Vector3 SurfaceFromBelow(Vector3 above, Vector3 light, float survives) => default;
    public static Vector2 Distort(Vector2 screen, Vector3 waveSum, float scaleBelow) => default;
}
