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
}
