// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.Building;

/// <summary>
/// What Create makes: the new prim's size and material, and the settings put on it once it arrives (its faces' texture, colour,
/// transparency, glow and full bright; physical, temporary and phantom; what the next owner may do with it). The defaults are the
/// box the create tool has always made, so a viewer whose settings are untouched sends nothing more than before. Firestorm's build
/// preferences (its FSBuildPrefs settings and FSCommon::applyDefaultBuildPreferences) were followed as behaviour, not copied.
/// </summary>
/// <param name="Transparency">Per cent, 0 (opaque) to 90 (the Texture tab's limit).</param>
public sealed record NewPrimDefaults(Vector3 Size, int Material, Vector3 Color, float Transparency, float Glow, bool FullBright, Guid Texture,
    bool Physical, bool Temporary, bool Phantom, bool NextOwnerCopy, bool NextOwnerModify, bool NextOwnerTransfer)
{
    /// <summary>The plywood texture every grid gives a new prim.</summary>
    public static readonly Guid Plywood = Guid.Parse("89556747-24cb-43ed-920b-47caed15465f");

    /// <summary>The box the create tool makes on its own: half a metre of wood, white plywood, nothing ticked, and the next owner may only
    /// transfer it (the region's own default for a new prim, and Firestorm's).</summary>
    public static readonly NewPrimDefaults Default = new(DefaultBox.Scale, DefaultBox.MaterialWood, Vector3.One, 0f, 0f, false, Plywood,
        false, false, false, false, false, true);

    /// <summary>The settings held to what a prim may have: the size to the region's limits, the material to Stone to Rubber, colour and glow to
    /// 0 to 1, transparency to 0 to 90; an empty texture is plywood.</summary>
    public NewPrimDefaults Clamped(EditLimits limits) => this;

    /// <summary>The edits to send to a prim just made with these settings: only those that differ from what the region gives a new prim
    /// (its faces, its flags, its next-owner permissions), so the untouched defaults send nothing.</summary>
    public IReadOnlyList<Viewer.Core.Actions.ActionIntent> EditsFor(uint localId, int faceCount) => [];
}
