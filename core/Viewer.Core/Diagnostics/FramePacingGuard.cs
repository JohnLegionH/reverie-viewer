// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;

namespace Viewer.Core.Diagnostics;

/// <summary>What a run's engine says it presents with, read back after the run's own settings were applied.</summary>
/// <param name="VsyncMode">The engine's vertical-sync mode as read back ("Disabled", "Enabled", "Adaptive", "Mailbox"), or null when not known.</param>
/// <param name="MaxFps">The engine's frame-rate limit (0 = none), or -1 when not known.</param>
/// <param name="PowerThrottling">Whether Windows may throttle the process: "opted-out", "system-decides", "forced-on" or "unknown".</param>
public sealed record PresentSettings(string? VsyncMode, int MaxFps, string PowerThrottling)
{
    public static readonly PresentSettings Unknown = new(null, -1, "unknown");
}

/// <summary>What kind of pacing the guard found.</summary>
public enum PacingKind
{
    /// <summary>No pacing found.</summary>
    None,
    /// <summary>The engine itself holds frames: vertical sync is on or a frame-rate limit is set.</summary>
    EngineLimit,
    /// <summary>Most frames sit at one to four refresh intervals.</summary>
    RefreshLocked,
    /// <summary>Most frames are short, and the slow ones recur once per refresh interval: presents wait for the display's refresh.</summary>
    RefreshStalls,
    /// <summary>The refresh rate is unknown, and a minority of frames pile up at one slow length that holds the 95th percentile.</summary>
    SlowPile,
}

/// <summary>The guard's reading of one run's frame times: whether they are presentation-paced, why, and the figures behind it.</summary>
/// <param name="Capped">True when the run's frame figures describe the pacing, not the scene.</param>
/// <param name="Message">One line for the run's log and failure list.</param>
/// <param name="NearRefreshFraction">Share of frames within the tolerance of one to four refresh intervals (0 when the refresh rate is unknown).</param>
/// <param name="ModeMs">Centre of the densest cluster of frames of 3 ms or more (0 when there is none).</param>
/// <param name="ModeFraction">Share of all frames in that cluster.</param>
/// <param name="Kind">Which reading found the pacing.</param>
/// <param name="StallsPerSecond">Slow frames a second (frames of 3 ms or more and at least three times the median).</param>
/// <param name="StallRegularity">Share of the gaps between slow frames that last one to four refresh intervals, within a quarter (0 when the refresh rate is unknown).</param>
public sealed record FramePacingVerdict(bool Capped, string Message, double NearRefreshFraction, double ModeMs, double ModeFraction,
    PacingKind Kind = PacingKind.None, double StallsPerSecond = 0, double StallRegularity = 0);

/// <summary>
/// Tells a measuring run whose frame times describe the scene from one whose presents were held by something outside it (the
/// display's vertical sync, the compositor's refresh, a driver frame-rate limit): such a run reports the pacing in its slow
/// frames and hides any cost below it. A steady frame time on its own is not pacing: a heavy scene is steady too. The readings,
/// any of which fails the run:
/// <list type="bullet">
/// <item>The engine's own settings: vertical sync is on, or a frame-rate limit is set.</item>
/// <item>Refresh clustering: at least <see cref="RefreshShare"/> of the frames lie within <see cref="RefreshTolerance"/> of one
/// to four refresh intervals. A run bound to the refresh has nearly all of them there.</item>
/// <item>Refresh stalls: most frames are short, and the slow ones (at least <see cref="MinStallMs"/> and <see cref="StallFactor"/>
/// times the median) come about once per refresh interval (or every second, third or fourth), at that rhythm. A swap chain that lets frames queue up and then
/// waits for the next refresh looks like this: a burst of fast frames, one long wait, again.</item>
/// <item>With the refresh rate unknown only: a minority of the frames pile up at one slow length, the 95th percentile is in the
/// pile, and the median is far below it. Without the median condition this reading also fails a heavy scene whose frames are all
/// about the same length, which is not pacing.</item>
/// </list>
/// </summary>
public static class FramePacingGuard
{
    public const int MinFrames = 200;
    public const double RefreshTolerance = 0.03;
    public const double Tolerance = 0.06;
    public const double RefreshShare = 0.4;
    public const double MinModeMs = 3.0;
    public const double ModeShare = 0.05;
    public const double ModeShareOfSlow = 0.4;
    public const double MinStallMs = 3.0;
    public const double StallFactor = 3.0;
    public const int MinStalls = 20;
    public const double StallRateMatch = 0.10;
    public const double GapTolerance = 0.25;
    public const double StallRegularityShare = 0.6;
    public const double StallRegularityAlone = 0.85;
    public const double PileMedianFactor = 3.0;

    public static FramePacingVerdict Evaluate(IReadOnlyList<double> frameMs, double refreshHz) =>
        Evaluate(frameMs, refreshHz, PresentSettings.Unknown);

    public static FramePacingVerdict Evaluate(IReadOnlyList<double> frameMs, double refreshHz, PresentSettings settings)
    {
        string refresh = refreshHz > 0 ? $"{refreshHz.ToString("0.##", CultureInfo.InvariantCulture)} Hz" : "unknown";

        // the engine says it holds frames: no need to read the frames to know the run is paced
        bool vsyncOn = settings.VsyncMode is { } v && !string.Equals(v, "Disabled", StringComparison.OrdinalIgnoreCase);
        if (vsyncOn || settings.MaxFps > 0)
        {
            string what = vsyncOn ? $"vertical sync is {settings.VsyncMode}" : $"the frame-rate limit is {settings.MaxFps}";
            return new(true, $"frame times are capped: the engine reports {what} (refresh rate {refresh}); the figures describe the display's pacing, not the scene",
                0, 0, 0, PacingKind.EngineLimit);
        }

        if (frameMs.Count < MinFrames)
            return new(false, $"frame pacing not judged: too few frames ({frameMs.Count}, need {MinFrames})", 0, 0, 0);

        var sorted = frameMs.OrderBy(x => x).ToArray();
        int n = sorted.Length;
        double median = sorted[n / 2];

        double near = 0;
        double interval = refreshHz > 0 ? 1000.0 / refreshHz : 0;
        if (refreshHz > 0)
        {
            int inside = 0;
            foreach (double f in sorted)
                for (int k = 1; k <= 4; k++)
                    if (Math.Abs(f - k * interval) <= k * interval * RefreshTolerance) { inside++; break; }
            near = (double)inside / n;
        }

        // densest window [m, m * (1 + 2 * Tolerance)] among frames at or over MinModeMs, by two pointers on the sorted times
        int first = Array.FindIndex(sorted, f => f >= MinModeMs);
        int best = 0, bestStart = 0;
        if (first >= 0)
        {
            int hi = first;
            for (int lo = first; lo < n; lo++)
            {
                double limit = sorted[lo] * (1 + 2 * Tolerance);
                while (hi < n && sorted[hi] <= limit) hi++;
                if (hi - lo > best) { best = hi - lo; bestStart = lo; }
            }
        }
        double modeLow = best > 0 ? sorted[bestStart] : 0;
        double modeHigh = best > 0 ? modeLow * (1 + 2 * Tolerance) : 0;
        double modeMs = best > 0 ? (modeLow + modeHigh) / 2 : 0;
        double modeFraction = (double)best / n;
        int slow = first >= 0 ? n - first : 0;
        double ofSlow = slow > 0 ? (double)best / slow : 0;
        double p95 = sorted[Math.Clamp((int)Math.Ceiling(0.95 * n) - 1, 0, n - 1)];

        // slow frames and the rhythm they come at, from the frames in the order they ran
        double stallLimit = Math.Max(MinStallMs, StallFactor * median);
        double totalMs = 0;
        var starts = new List<double>();
        foreach (double f in frameMs)
        {
            if (f >= stallLimit) starts.Add(totalMs);
            totalMs += f;
        }
        double stallsPerSecond = totalMs > 0 ? starts.Count * 1000.0 / totalMs : 0;
        double regularity = 0;
        bool rateMatches = false;
        if (refreshHz > 0 && starts.Count >= MinStalls)
        {
            int inRhythm = 0;
            for (int i = 1; i < starts.Count; i++)
            {
                double gap = starts[i] - starts[i - 1];
                for (int k = 1; k <= 4; k++)
                    if (Math.Abs(gap - k * interval) <= k * interval * GapTolerance) { inRhythm++; break; }
            }
            regularity = (double)inRhythm / (starts.Count - 1);
            for (int k = 1; k <= 4; k++)
                if (Math.Abs(stallsPerSecond - refreshHz / k) <= refreshHz / k * StallRateMatch) { rateMatches = true; break; }
        }

        FramePacingVerdict Make(bool capped, string message, PacingKind kind) =>
            new(capped, message, near, modeMs, modeFraction, kind, stallsPerSecond, regularity);

        if (near >= RefreshShare)
            return Make(true, $"frame times are capped: {near:P0} of the frames sit at one to four refresh intervals (refresh rate {refresh}); the figures describe the display's pacing, not the scene", PacingKind.RefreshLocked);
        // slow frames spaced like the refresh are the rhythm; a run held for only part of its length (or on every second refresh) has fewer of them,
        // so a very regular rhythm is enough without the rate, as long as no more come than the display refreshes
        bool rhythm = starts.Count >= MinStalls && stallsPerSecond <= refreshHz * (1 + StallRateMatch)
            && (regularity >= StallRegularityAlone || (rateMatches && regularity >= StallRegularityShare));
        if (rhythm)
            return Make(true, $"frame times are capped: {starts.Count} slow frames, {stallsPerSecond:0.#} a second against a refresh rate of {refresh}, {regularity:P0} of them one refresh interval apart; presents wait for the display's refresh and the slow frames carry the wait, not the scene", PacingKind.RefreshStalls);
        if (refreshHz <= 0 && modeFraction >= ModeShare && ofSlow >= ModeShareOfSlow && p95 >= modeLow && p95 <= modeHigh && median * PileMedianFactor <= modeLow)
            return Make(true, $"frame times are capped: {modeFraction:P0} of the frames pile up at about {modeMs:0.##} ms, the 95th percentile ({p95:0.##} ms) is in that pile and the median is {median:0.##} ms (refresh rate unknown); the figures describe a presentation limit, not the scene", PacingKind.SlowPile);
        return Make(false, $"frame pacing free (refresh rate {refresh}; {near:P0} near a refresh interval; {stallsPerSecond:0.#} slow frames a second, {regularity:P0} in a refresh rhythm; densest pile {modeFraction:P1} at {modeMs:0.##} ms)", PacingKind.None);
    }
}
