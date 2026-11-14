// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Frozen;

namespace Viewer.Core.Avatars;

/// <summary>
/// The special texture ids a face names to show one of its wearer's baked textures instead of a texture of its own (bake on mesh).
/// The ids are the protocol's constants (the Second Life viewer's IMG_USE_BAKED_* values, indra_constants.cpp:80-90, read for the
/// values only), listed in bake-type order so each id's position is its bake type (<see cref="Viewer.Core.World.AppearanceSlots"/>).
/// What a face naming one of them draws is <see cref="BakeOnMesh"/>.
/// </summary>
public static class BakeChannels
{
    private static readonly System.Collections.Immutable.ImmutableArray<Guid> ByType =
    [
        Guid.Parse("5a9f4a74-30f2-821c-b88d-70499d3e7183"), Guid.Parse("ae2de45c-d252-50b8-5c6e-19f39ce79317"), Guid.Parse("24daea5f-0539-cfcf-047f-fbc40b2786ba"),
        Guid.Parse("52cc6bb6-2ee5-e632-d3ad-50197b1dcb8a"), Guid.Parse("43529ce8-7faa-ad92-165a-bc4078371687"), Guid.Parse("09aac1fb-6bce-0bee-7d44-caac6dbb6c63"),
        Guid.Parse("ff62763f-d60a-9855-890b-0c96f8f8cd98"), Guid.Parse("8e915e25-31d1-cc95-ae08-d58a47488251"), Guid.Parse("9742065b-19b5-297c-858a-29711d539043"),
        Guid.Parse("03642e83-2bd1-4eb9-34b4-4c47ed586d2d"), Guid.Parse("edd51b77-fc10-ce7a-4b3d-011dfc349e4f"),
    ];

    private static readonly FrozenDictionary<Guid, string> Slots = ByType
        .Select((id, type) => (id, slot: Viewer.Core.World.AppearanceSlots.SlotOf(type)!))
        .ToFrozenDictionary(x => x.id, x => x.slot);

    private static readonly FrozenDictionary<string, Guid> Ids = Slots.ToFrozenDictionary(kv => kv.Value, kv => kv.Key);

    public static bool IsBakeId(Guid texture) => Slots.ContainsKey(texture);

    /// <summary>The bake slot name a placeholder id stands for ("head", "upper", ... "aux3"), or null for any other texture.</summary>
    public static string? SlotOf(Guid texture) => Slots.GetValueOrDefault(texture);

    /// <summary>The placeholder id of a bake slot name; throws for a name that is not one of the eleven.</summary>
    public static Guid IdOf(string slot) => Ids.TryGetValue(slot, out var id) ? id : throw new ArgumentException($"'{slot}' is not a bake slot", nameof(slot));
}
