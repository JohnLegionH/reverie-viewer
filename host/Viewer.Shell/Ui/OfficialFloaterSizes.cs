// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Generic;

namespace Viewer.Shell;

/// <summary>The default sizes of the official viewer's windows that ours stand for (stub).</summary>
public static class OfficialFloaterSizes
{
    public static UiSize? Of(string id) => null;

    public static IReadOnlyDictionary<string, (string File, UiSize? Size)> Table { get; } = new Dictionary<string, (string, UiSize?)>();
}
