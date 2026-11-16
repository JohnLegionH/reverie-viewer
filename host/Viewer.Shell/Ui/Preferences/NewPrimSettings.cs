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
    private static double Real(double v, double min, double max, double fallback) => double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;

    public static readonly Setting<double> SizeX = new("new_prim_size_x", 0.5, v => Real(v, 0.01, 64, 0.5));
    public static readonly Setting<double> SizeY = new("new_prim_size_y", 0.5, v => Real(v, 0.01, 64, 0.5));
    public static readonly Setting<double> SizeZ = new("new_prim_size_z", 0.5, v => Real(v, 0.01, 64, 0.5));
    public const string MaterialKey = "new_prim_material";
    public static readonly Setting<double> Red = new("new_prim_red", 1.0, v => Real(v, 0, 1, 1));
    public static readonly Setting<double> Green = new("new_prim_green", 1.0, v => Real(v, 0, 1, 1));
    public static readonly Setting<double> Blue = new("new_prim_blue", 1.0, v => Real(v, 0, 1, 1));
    public static readonly Setting<double> Transparency = new("new_prim_transparency", 0.0, v => Real(v, 0, 90, 0));
    public static readonly Setting<double> Glow = new("new_prim_glow", 0.0, v => Real(v, 0, 1, 0));
    public static readonly Setting<bool> FullBright = new("new_prim_full_bright", false, v => v);
    public const string TextureKey = "new_prim_texture";
    public static readonly Setting<bool> Physical = new("new_prim_physical", false, v => v);
    public static readonly Setting<bool> Temporary = new("new_prim_temporary", false, v => v);
    public static readonly Setting<bool> Phantom = new("new_prim_phantom", false, v => v);
    public static readonly Setting<bool> NextOwnerCopy = new("new_prim_next_owner_copy", false, v => v);
    public static readonly Setting<bool> NextOwnerModify = new("new_prim_next_owner_modify", false, v => v);
    public static readonly Setting<bool> NextOwnerTransfer = new("new_prim_next_owner_transfer", true, v => v);

    /// <summary>The settings as Create uses them.</summary>
    public static NewPrimDefaults Read(SettingsService settings)
    {
        int material = settings.GetText(MaterialKey) is { } m ? Array.IndexOf(Materials, m) : -1;
        var texture = settings.GetText(TextureKey) is { } t && Guid.TryParse(t, out var id) ? id : NewPrimDefaults.Plywood;
        return new NewPrimDefaults(new Vector3((float)settings.Get(SizeX), (float)settings.Get(SizeY), (float)settings.Get(SizeZ)), material >= 0 ? material : DefaultBox.MaterialWood,
            new Vector3((float)settings.Get(Red), (float)settings.Get(Green), (float)settings.Get(Blue)), (float)settings.Get(Transparency), (float)settings.Get(Glow), settings.Get(FullBright), texture,
            settings.Get(Physical), settings.Get(Temporary), settings.Get(Phantom), settings.Get(NextOwnerCopy), settings.Get(NextOwnerModify), settings.Get(NextOwnerTransfer));
    }

    /// <summary>The material list's ids, in the protocol's numbering (Stone 0 to Rubber 6).</summary>
    public static readonly string[] Materials = ["stone", "metal", "glass", "wood", "flesh", "plastic", "rubber"];
}
