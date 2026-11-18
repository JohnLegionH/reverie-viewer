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

    /// <summary>An owner: a group-owned object (its owner is its group) or item says so instead of asking the avatar names for a group.</summary>
    public static string Owner(Func<Guid, string?>? lookup, Guid owner, Guid group, bool groupOwned, StringTable strings) =>
        groupOwned || (owner != Guid.Empty && owner == group) ? strings.Get("edit.name.group") : Person(lookup, owner, strings);
}
