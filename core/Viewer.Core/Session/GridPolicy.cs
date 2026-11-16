// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Session;

/// <summary>Which kind of grid a session is on. Second Life is told apart by its login host, never by the name a grid list
/// gives it.</summary>
public enum GridKind
{
    /// <summary>Any grid that is not Second Life (OpenSim and its relatives).</summary>
    OpenSim,

    /// <summary>Linden Lab's Second Life, where the Third-Party Viewer Policy applies.</summary>
    SecondLife,
}

/// <summary>A feature this viewer has that the official Second Life viewer does not, and so needs a declared place.</summary>
public enum ViewerFeature
{
    /// <summary>Saving objects to a file on the person's computer.</summary>
    Backup = 1,

    /// <summary>Rezzing objects again from such a file.</summary>
    Restore = 2,

    /// <summary>The local automation interface (started only with --automation).</summary>
    Automation = 3,

    /// <summary>The add-on host: add-ons that chat, play animations or filter actions for the person.</summary>
    Addons = 4,

    /// <summary>The restrained-life restriction set (RLV), which scripted objects the person wears drive.</summary>
    Restrictions = 5,
}

/// <summary>Where a feature may run.</summary>
public enum FeatureReach
{
    /// <summary>On every grid.</summary>
    Everywhere,

    /// <summary>Off on Second Life, on everywhere else.</summary>
    OpenSimOnly,

    /// <summary>On Second Life only for objects every part of which the person created; on everywhere else as before.</summary>
    OwnCreationsOnSecondLife,
}

/// <summary>
/// The per-grid switch. Every feature beyond the official viewer declares here where it may run; a feature with no declaration is
/// off on Second Life and keeps its behaviour elsewhere. The table is fixed in the build: nothing at run time (no setting, no
/// add-on) can add to it or change it.
/// </summary>
public static class GridPolicy
{
    private static readonly IReadOnlyDictionary<ViewerFeature, FeatureReach> Declared = new Dictionary<ViewerFeature, FeatureReach>();

    /// <summary>The kind of grid a login address belongs to: Second Life when its host is one of Linden Lab's.</summary>
    public static GridKind KindOf(string? loginUri) => GridKind.OpenSim;

    /// <summary>The declared place of a feature, or null when it has none.</summary>
    public static FeatureReach? ReachOf(ViewerFeature feature) => null;

    /// <summary>Whether a feature may run on this kind of grid at all.</summary>
    public static bool Allows(ViewerFeature feature, GridKind grid) => false;

    /// <summary>Whether objects whose parts were made by <paramref name="partCreators"/> may be saved to a file by
    /// <paramref name="self"/> on this kind of grid.</summary>
    public static bool MayBackUp(GridKind grid, Guid self, IReadOnlyCollection<Guid> partCreators) => false;
}
