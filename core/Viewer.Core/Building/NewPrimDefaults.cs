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
    public NewPrimDefaults Clamped(EditLimits limits)
    {
        static float C(float v, float min, float max, float fallback) => float.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;
        return this with
        {
            Size = new(C(Size.X, limits.MinScale, limits.MaxScale, 0.5f), C(Size.Y, limits.MinScale, limits.MaxScale, 0.5f), C(Size.Z, limits.MinScale, limits.MaxScale, 0.5f)),
            Material = Material is >= 0 and <= 6 ? Material : DefaultBox.MaterialWood,
            Color = new(C(Color.X, 0f, 1f, 1f), C(Color.Y, 0f, 1f, 1f), C(Color.Z, 0f, 1f, 1f)),
            Transparency = C(Transparency, 0f, 90f, 0f),
            Glow = C(Glow, 0f, 1f, 0f),
            Texture = Texture == Guid.Empty ? Plywood : Texture,
        };
    }

    /// <summary>The edits to send to a prim just made with these settings: only those that differ from what the region gives a new prim
    /// (its faces, its flags, its next-owner permissions), so the untouched defaults send nothing.</summary>
    public IReadOnlyList<Viewer.Core.Actions.ActionIntent> EditsFor(uint localId, int faceCount)
    {
        var d = Default;
        var edits = new List<Viewer.Core.Actions.ActionIntent>();
        if (Texture != d.Texture || Color != d.Color || Transparency != d.Transparency || Glow != d.Glow || FullBright != d.FullBright)
        {
            // every face as a new box has it (repeats 1, no offset or turn, the default mapping), with these settings on top
            var face = new FaceSettings(Texture, new Viewer.Core.World.Rgba(Color.X, Color.Y, Color.Z, 1f - Transparency / 100f), 1f, 1f, 0f, 0f, 0f,
                default, FullBright, Glow);
            edits.Add(new Viewer.Core.Actions.SetObjectFacesIntent(localId, [.. Enumerable.Range(0, faceCount).Select(f => new FaceEdit(f, face))]));
        }
        if (Physical != d.Physical || Temporary != d.Temporary || Phantom != d.Phantom)
            edits.Add(new Viewer.Core.Actions.SetObjectFlagsIntent(localId, Physical, Temporary, Phantom));
        // the next owner's permissions, one change each, as ObjectPermissions on the next owner's field
        foreach (var (mask, value, was) in new[] { (PermCopy, NextOwnerCopy, d.NextOwnerCopy), (PermModify, NextOwnerModify, d.NextOwnerModify),
                     (PermTransfer, NextOwnerTransfer, d.NextOwnerTransfer) })
            if (value != was)
                edits.Add(new Viewer.Core.Actions.SetObjectPermissionsIntent([localId], FieldNextOwner, value, mask));
        return edits;
    }

    /// <summary>The protocol's permission bits and ObjectPermissions' next-owner field.</summary>
    private const uint PermTransfer = 1u << 13, PermModify = 1u << 14, PermCopy = 1u << 15;
    private const byte FieldNextOwner = 0x10;
}
