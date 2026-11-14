// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>Which rendering backend is in use and, when the one wanted could not run, which that was and why.</summary>
public sealed record BackendStatus(string InUse, string? Wanted, string? Why)
{
    public bool FellBack => Wanted is not null;

    /// <summary>
    /// <paramref name="requested"/> is the saved choice or the one for this run, <paramref name="running"/> the backend that is running,
    /// <paramref name="tried"/> the backends an earlier start of this run was restarted for, <paramref name="decision"/> what the hardware
    /// probe said (null when it could not be read). A backend counts as one that could not run when the probe put it below the floor and
    /// the other is running, or when this run was restarted onto another backend than the one wanted (the engine refused the first). A
    /// backend started by an explicit engine flag, with no restart and a probe that passes the wanted one, is not a failure.
    /// </summary>
    public static BackendStatus From(string requested, string running, IReadOnlyList<string> tried, BackendDecision? decision)
    {
        string wanted = requested is BackendChoice.Vulkan or BackendChoice.D3D12 ? requested : BackendChoice.Vulkan;   // automatic prefers Vulkan
        if (string.Equals(running, wanted, StringComparison.OrdinalIgnoreCase))
            return new(running, null, null);
        if (decision is { FellBack: true } && string.Equals(decision.Driver, running, StringComparison.OrdinalIgnoreCase))
            return new(running, wanted, decision.WhyNot ?? "it is below the floor");
        if (tried.Count > 0)
            return new(running, wanted, "the graphics engine did not accept it");
        return new(running, null, null);
    }

    private static string Label(StringTable strings, string driver) => strings.Has("prefs.c.graphics_backend." + driver) ? strings.Get("prefs.c.graphics_backend." + driver) : driver;

    /// <summary>"Rendering backend in use: Vulkan".</summary>
    public string InUseText(StringTable strings) => strings.Get("prefs.backend.inuse", Label(strings, InUse));

    /// <summary>The notice for a backend that could not run; null when nothing failed.</summary>
    public string? NoticeText(StringTable strings) =>
        FellBack ? strings.Get("backend.notice.fellback", Label(strings, Wanted!), Label(strings, InUse), Why ?? "") : null;
}
