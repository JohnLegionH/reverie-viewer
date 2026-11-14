// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// What a unit of presentation work costs for its size, learned from the units already done: the milliseconds a unit takes per unit of
/// size (bytes handed to the engine, say), kept for the last <c>window</c> units, and a high quantile of them as the estimate so that
/// a frame leaves room for the dearer units, not the average. Main thread only.
/// </summary>
public sealed class UnitCostModel
{
    /// <summary>Fewer measurements than this and the estimate is 0 (unknown: the unit is allowed to start).</summary>
    public const int MinSamples = 4;

    private readonly double[] _ratios;
    private readonly double[] _sorted;
    private readonly double _quantile;
    private int _count, _next;

    /// <param name="window">How many of the latest units are kept.</param>
    /// <param name="quantile">Which share of the kept costs the estimate covers (0.9: all but the dearest tenth).</param>
    public UnitCostModel(int window = 32, double quantile = 0.9)
    {
        if (window < MinSamples) throw new ArgumentOutOfRangeException(nameof(window));
        _ratios = new double[window];
        _sorted = new double[window];
        _quantile = Math.Clamp(quantile, 0, 1);
    }

    /// <summary>How many units are kept.</summary>
    public int Samples => _count;

    /// <summary>One finished unit: its size (any positive measure, as long as the same one is used for the estimate) and how long it took.</summary>
    public void Record(double size, double ms)
    {
        if (size <= 0 || ms < 0) return;
        _ratios[_next] = ms / size;
        _next = (_next + 1) % _ratios.Length;
        if (_count < _ratios.Length) _count++;
    }

    /// <summary>About how long a unit of this size takes; 0 until <see cref="MinSamples"/> units have been measured.</summary>
    public double EstimateMs(double size)
    {
        if (_count < MinSamples || size <= 0) return 0;
        Array.Copy(_ratios, _sorted, _count);
        Array.Sort(_sorted, 0, _count);
        int rank = Math.Max(1, (int)Math.Ceiling(_quantile * _count));   // nearest rank
        return _sorted[rank - 1] * size;
    }
}
