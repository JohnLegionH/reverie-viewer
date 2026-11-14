// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Residency;

/// <summary>
/// The texture budget matches the official viewer's current
/// develop. Budget = the graphics card's video memory / <see cref="Divisor"/>, minus the larger of <see cref="OtherPrograms"/>
/// or a fifth of that share, minus the viewer's own non-texture video memory (mesh buffers, render targets, the engine's
/// other allocations; the live accounting); never below <see cref="Minimum"/>; <see cref="Unreadable"/> when the video
/// memory cannot be read (Mac and Linux until); no fixed ceiling. A configured cap (<c>--texture-budget-mib=</c>)
/// always wins. Test renders pass their own fixed budget (<see cref="TestRenderFixed"/>) and never use this.
/// </summary>
public static class TextureBudget
{
    public const long MiB = 1024 * 1024;
    /// <summary>The share of video memory textures start from (the official viewer's setting, code default 2). A
    /// constant until the preferences work.</summary>
    public const int Divisor = 2;
    /// <summary>Kept back from that share at least (the official viewer's reserve); a fifth of the share when that is larger.</summary>
    public const long OtherPrograms = 512 * MiB;
    /// <summary>The least the budget ever is (the official viewer claims at least this much).</summary>
    public const long Minimum = 768 * MiB;
    /// <summary>The budget when the card's video memory cannot be read.</summary>
    public const long Unreadable = 768 * MiB;
    /// <summary>The viewer's own non-texture video memory assumed until the engine has been read (and the whole reserve
    /// when live accounting is off): the worst figure at 3840x2160 (459 MiB, region A v5 on d3d12) plus a
    /// margin of about two thirds for denser regions (more meshes) than the scenes measured.</summary>
    public const long StaticNonTexture = 768 * MiB;
    /// <summary>The fixed budget of test renders when none is given (unchanged, so goldens never depend on
    /// the machine's GPU).</summary>
    public const long TestRenderFixed = 384 * MiB;

    /// <summary>The budget for this card: <paramref name="configuredBytes"/> if set; otherwise video memory / <see cref="Divisor"/>
    /// minus the larger of <see cref="OtherPrograms"/> or a fifth of that share, minus the viewer's non-texture memory, at
    /// least <see cref="Minimum"/>.</summary>
    public static long For(long? videoMemoryBytes, long nonTextureBytes = StaticNonTexture, long? configuredBytes = null)
    {
        if (configuredBytes is > 0) return configuredBytes.Value;
        if (videoMemoryBytes is not > 0) return Unreadable;
        long share = videoMemoryBytes.Value / Divisor;
        long reserve = Math.Max(OtherPrograms, share / 5);
        return Math.Max(Minimum, share - reserve - Math.Max(0, nonTextureBytes));
    }
}

/// <summary>One move of the live budget (when, from the session's clock; the non-texture figure it came
/// from; the budget before and after).</summary>
public sealed record BudgetChange(TimeSpan At, long NonTexture, long Previous, long Budget);

/// <summary>
///Live accounting (unchanged): the official viewer counts its vertex buffers with its textures; here every residency update
/// hands in the engine's current non-texture video memory (the engine's total minus the residency's resident textures)
/// and the budget follows it. Smoothed so it does not jitter: a change of less than <see cref="Deadband"/> in the
/// non-texture figure does not move it, and it moves at most once per <see cref="Interval"/>. With a configured cap, with
/// unreadable video memory, or with live accounting off, the budget never moves.
/// </summary>
public sealed class LiveTextureBudget
{
    public static readonly long Deadband = 32 * TextureBudget.MiB;
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly long? _videoMemory, _configured;
    private TimeSpan? _lastApplied;

    public LiveTextureBudget(long? videoMemoryBytes, long? configuredBytes = null, bool live = true)
    {
        _videoMemory = videoMemoryBytes is > 0 ? videoMemoryBytes : null;
        _configured = configuredBytes is > 0 ? configuredBytes : null;
        Live = live && _configured is null && _videoMemory is not null;
        NonTextureApplied = TextureBudget.StaticNonTexture;
        Budget = TextureBudget.For(_videoMemory, NonTextureApplied, _configured);
        Initial = Min = Max = Budget;
    }

    /// <summary>Whether the budget follows the engine's non-texture memory (false: fixed for the session).</summary>
    public bool Live { get; }
    public long Budget { get; private set; }
    /// <summary>The non-texture figure the current budget was computed from.</summary>
    public long NonTextureApplied { get; private set; }
    public long Initial { get; }
    public long Min { get; private set; }
    public long Max { get; private set; }
    /// <summary>How many times the budget moved.</summary>
    public int Changes { get; private set; }
    /// <summary>Raised on every move of the budget (the live check logs them).</summary>
    public event Action<BudgetChange>? Changed;
    /// <summary>The first moves of the budget, kept for the live check (at most <see cref="MaxHistory"/>).</summary>
    public IReadOnlyList<BudgetChange> History { get { lock (_history) return _history.ToList(); } }
    public const int MaxHistory = 256;
    private readonly List<BudgetChange> _history = new();

    public string Source => _configured is not null ? "configured" : _videoMemory is null ? "unreadable" : Live ? "video memory, live" : "video memory, static";

    /// <summary>Called on every residency update with the engine's current non-texture video memory; returns the budget.</summary>
    public long Update(long nonTextureBytes, TimeSpan now)
    {
        if (!Live) return Budget;
        if (_lastApplied is { } last && now - last < Interval) return Budget;
        if (Math.Abs(nonTextureBytes - NonTextureApplied) < Deadband) return Budget;
        _lastApplied = now;
        NonTextureApplied = Math.Max(0, nonTextureBytes);
        long next = TextureBudget.For(_videoMemory, NonTextureApplied);
        if (next != Budget)
        {
            var change = new BudgetChange(now, NonTextureApplied, Budget, next);
            lock (_history) if (_history.Count < MaxHistory) _history.Add(change);
            Budget = next;
            Changes++;
            Min = Math.Min(Min, next);
            Max = Math.Max(Max, next);
            Changed?.Invoke(change);
        }
        return Budget;
    }
}
