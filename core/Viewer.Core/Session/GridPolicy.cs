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

    /// <summary>The viewer statistics report posted to the region's ViewerStats capability (llviewerstats.cpp send_viewer_stats). The
    /// Third-Party Viewer Policy 2.h asks that it is not left out on Second Life; elsewhere it is not sent.</summary>
    ViewerStatistics = 6,

    /// <summary>This viewer's own interface sounds, made by its generator. The official viewer plays the grid's sound assets by their
    /// ids (its UISnd* settings).</summary>
    OwnInterfaceSounds = 7,
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

    /// <summary>On Second Life only; off everywhere else.</summary>
    SecondLifeOnly,
}

/// <summary>
/// The per-grid switch. Every feature beyond the official viewer declares here where it may run; a feature with no declaration is
/// off on Second Life and keeps its behaviour elsewhere. The table is fixed in the build: nothing at run time (no setting, no
/// add-on) can add to it or change it.
/// </summary>
public static class GridPolicy
{
    // The declarations. Backup and restore follow the Third-Party Viewer Policy 2.b: content is exported only when every part's
    // creator is the person. The automation interface (local, and off unless the viewer is started with --automation) is not allowed
    // on Second Life, whose bot rules (Third-Party Viewer Policy 4.a.iii) cover scripted agents; the add-on host and the restriction
    // set act only as the person could by hand. The viewer statistics report is sent where the policy asks for it (2.h) and nowhere
    // else. This viewer's own interface sounds are not played on Second Life, where the official viewer plays the grid's sounds by
    // their ids and this viewer does the same; elsewhere they are the default. Each is a choice recorded here, in one place.
    private static readonly IReadOnlyDictionary<ViewerFeature, FeatureReach> Declared = new Dictionary<ViewerFeature, FeatureReach>
    {
        [ViewerFeature.Backup] = FeatureReach.OwnCreationsOnSecondLife,
        [ViewerFeature.Restore] = FeatureReach.OwnCreationsOnSecondLife,
        [ViewerFeature.Automation] = FeatureReach.OpenSimOnly,
        [ViewerFeature.Addons] = FeatureReach.Everywhere,
        [ViewerFeature.Restrictions] = FeatureReach.Everywhere,
        [ViewerFeature.ViewerStatistics] = FeatureReach.SecondLifeOnly,
        [ViewerFeature.OwnInterfaceSounds] = FeatureReach.OpenSimOnly,
    };

    /// <summary>The kind of grid a login address belongs to: Second Life when its host is one of Linden Lab's.</summary>
    public static GridKind KindOf(string? loginUri)
    {
        if (string.IsNullOrWhiteSpace(loginUri) || !Uri.TryCreate(loginUri.Trim(), UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            return GridKind.OpenSim;
        string host = uri.Host.TrimEnd('.').ToLowerInvariant();
        // Linden Lab's own domains: every Second Life login service (the main grid and the beta grid) is under one of them
        return Under(host, "lindenlab.com") || Under(host, "secondlife.com") || Under(host, "secondlife.io") ? GridKind.SecondLife : GridKind.OpenSim;
    }

    private static bool Under(string host, string domain) =>
        host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

    /// <summary>The declared place of a feature, or null when it has none.</summary>
    public static FeatureReach? ReachOf(ViewerFeature feature) => Declared.TryGetValue(feature, out var reach) ? reach : null;

    /// <summary>Whether a feature may run on this kind of grid at all. An undeclared feature is off on Second Life and unchanged
    /// elsewhere.</summary>
    public static bool Allows(ViewerFeature feature, GridKind grid) => ReachOf(feature) switch
    {
        null => grid != GridKind.SecondLife,
        FeatureReach.OpenSimOnly => grid != GridKind.SecondLife,
        FeatureReach.SecondLifeOnly => grid == GridKind.SecondLife,
        _ => true,
    };

    /// <summary>Whether objects whose parts were made by <paramref name="partCreators"/> may be saved to a file by
    /// <paramref name="self"/> on this kind of grid. On Second Life every part must be the person's own creation, and an object
    /// with no parts listed is refused; elsewhere this rule does not limit it.</summary>
    public static bool MayBackUp(GridKind grid, Guid self, IReadOnlyCollection<Guid> partCreators)
    {
        if (grid != GridKind.SecondLife)
            return Allows(ViewerFeature.Backup, grid);
        return Allows(ViewerFeature.Backup, grid) && OwnCreations(self, partCreators);
    }

    /// <summary>Whether objects from a file whose parts were made by <paramref name="partCreators"/> may be rezzed again by
    /// <paramref name="self"/> on this kind of grid: the same rule as <see cref="MayBackUp"/>.</summary>
    public static bool MayRestore(GridKind grid, Guid self, IReadOnlyCollection<Guid> partCreators) =>
        Allows(ViewerFeature.Restore, grid) && (grid != GridKind.SecondLife || OwnCreations(self, partCreators));

    private static bool OwnCreations(Guid self, IReadOnlyCollection<Guid> partCreators) =>
        self != Guid.Empty && partCreators.Count > 0 && partCreators.All(c => c == self);
}
