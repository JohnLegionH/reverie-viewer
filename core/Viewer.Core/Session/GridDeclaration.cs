// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Session;

/// <summary>
/// The grid check for a feature that states its own grids, such as an add-on's "grids" for what it does beyond the official viewer.
/// The viewer's own features are declared in <see cref="GridPolicy"/>'s fixed table, which nothing here adds to.
/// </summary>
public static class GridDeclaration
{
    /// <summary>Whether a feature may run on this kind of grid: exactly the grids it names. One that names none is off on Second
    /// Life and on elsewhere, as an undeclared <see cref="ViewerFeature"/> is.</summary>
    public static bool Allows(IReadOnlyCollection<GridKind>? declared, GridKind grid) =>
        declared is { Count: > 0 } ? declared.Contains(grid) : grid != GridKind.SecondLife;
}
