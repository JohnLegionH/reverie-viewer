// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Godot;
using Viewer.Core.Identity;

namespace Viewer.Godot;

/// <summary>The identity the screens show: the build's own, except in a test-picture or start-up check run, which shows the
/// version without its build number so the pictures do not change with every commit. What is sent to a grid is always
/// <see cref="ViewerIdentity.Current"/>.</summary>
internal static class ShownIdentity
{
    private static readonly bool ForPictures =
        OS.GetCmdlineUserArgs().Any(a => a == "--test-render" || a.StartsWith("--s1-", StringComparison.Ordinal));

    public static ViewerIdentity Current => ForPictures ? ViewerIdentity.Current.WithoutBuildNumber() : ViewerIdentity.Current;
}
