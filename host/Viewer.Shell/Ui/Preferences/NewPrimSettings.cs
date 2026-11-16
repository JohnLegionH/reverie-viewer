// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Building;

namespace Viewer.Shell;

/// <summary>
/// Preferences, Building: what Create makes (size, material, colour, transparency, glow, full bright, texture, physical, temporary, phantom,
/// and what the next owner may do). Firestorm keeps the same settings as its build preferences (FSBuildPrefs_Xsize and the rest, with
/// ObjectsNextOwnerCopy, Modify and Transfer; read as behaviour). The defaults are the box the create tool always made.
/// </summary>
public static class NewPrimSettings
{
    private static float Real(float v, float min, float max, float fallback) => float.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;

    public static readonly Setting<float> SizeX = new("new_prim_size_x", 0.5f, v => Real(v, 0.01f, 64f, 0.5f));
    public static readonly Setting<float> SizeY = new("new_prim_size_y", 0.5f, v => Real(v, 0.01f, 64f, 0.5f));
    public static readonly Setting<float> SizeZ = new("new_prim_size_z", 0.5f, v => Real(v, 0.01f, 64f, 0.5f));
    public const string MaterialKey = "new_prim_material";
    public static readonly Setting<float> Red = new("new_prim_red", 1f, v => Real(v, 0f, 1f, 1f));
    public static readonly Setting<float> Green = new("new_prim_green", 1f, v => Real(v, 0f, 1f, 1f));
    public static readonly Setting<float> Blue = new("new_prim_blue", 1f, v => Real(v, 0f, 1f, 1f));
    public static readonly Setting<float> Transparency = new("new_prim_transparency", 0f, v => Real(v, 0f, 90f, 0f));
    public static readonly Setting<float> Glow = new("new_prim_glow", 0f, v => Real(v, 0f, 1f, 0f));
    public static readonly Setting<bool> FullBright = new("new_prim_full_bright", false, v => v);
    public const string TextureKey = "new_prim_texture";
    public static readonly Setting<bool> Physical = new("new_prim_physical", false, v => v);
    public static readonly Setting<bool> Temporary = new("new_prim_temporary", false, v => v);
    public static readonly Setting<bool> Phantom = new("new_prim_phantom", false, v => v);
    public static readonly Setting<bool> NextOwnerCopy = new("new_prim_next_owner_copy", false, v => v);
    public static readonly Setting<bool> NextOwnerModify = new("new_prim_next_owner_modify", false, v => v);
    public static readonly Setting<bool> NextOwnerTransfer = new("new_prim_next_owner_transfer", true, v => v);

    /// <summary>The settings as Create uses them.</summary>
    public static NewPrimDefaults Read(SettingsService settings) => NewPrimDefaults.Default;
}
