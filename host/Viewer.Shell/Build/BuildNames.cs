// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Build;

/// <summary>
/// People named in the Build window (the selection card's owner and creator, an item's creator and owner): by name through the session's
/// name cache, the lookup chat, instant messages and the block list use, and "Loading..." until the grid has answered, as the official
/// panels show while LLAvatarNameCache fetches a name (indra/newview/llpanelpermissions.cpp). Never the id.
/// </summary>
public static class BuildNames
{
    public static string Person(Func<Guid, string?>? lookup, Guid id, StringTable strings) =>
        id == Guid.Empty ? "" : lookup?.Invoke(id) is { Length: > 0 } name ? name : strings.Get("edit.name.loading");

    /// <summary>A texture as the Look section shows it: the inventory's name for it, else the name of one of the viewer's standard textures,
    /// else the first part of its id (the whole id is in the tooltip), never a 36-character id that no row can hold.</summary>
    public static string Texture(Guid id, string? inventoryName, StringTable strings)
    {
        if (inventoryName is { Length: > 0 })
            return inventoryName;
        if (id == Plywood) return strings.Get("edit.tex.name.plywood");
        if (id == Blank) return strings.Get("edit.tex.name.blank");
        if (id == Transparent) return strings.Get("edit.tex.name.transparent");
        return id.ToString("D")[..8] + "…";
    }

    // the standard textures every grid knows (the default plywood, the blank white one and the fully transparent one)
    private static readonly Guid Plywood = new("89556747-24cb-43ed-920b-47caed15465f"),
        Blank = new("5748decc-f629-461c-9a36-a35a221fe21f"), Transparent = new("8dcd4a48-2d37-4909-9f78-f7a9eb4ef903");

    /// <summary>An owner: a group-owned object (its owner is its group) or item says so instead of asking the avatar names for a group.</summary>
    public static string Owner(Func<Guid, string?>? lookup, Guid owner, Guid group, bool groupOwned, StringTable strings) =>
        groupOwned || (owner != Guid.Empty && owner == group) ? strings.Get("edit.name.group") : Person(lookup, owner, strings);
}
