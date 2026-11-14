// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text;

namespace Viewer.Shell;

/// <summary>
/// The engine resources (meshes, instances, skeletons, textures) the viewer has created and not yet freed, by id.
/// The engine corrupts its heap when it quits while resources we made are still live, so a test or S-1 run lists
/// what is left just before the quit and fails with that list instead of ending in a crash. Plain .NET (the id is
/// the engine resource's number), thread-safe.
/// </summary>
public sealed class LiveRidLedger
{
    public sealed record Entry(ulong Id, string Owner);

    private readonly object _gate = new();
    private readonly Dictionary<ulong, string> _live = new();
    private int _unknownFrees;

    public int Count { get { lock (_gate) return _live.Count; } }

    /// <summary>Frees of an id the ledger never saw or had already seen freed: ids the engine made itself, or a double free.</summary>
    public int UnknownFrees { get { lock (_gate) return _unknownFrees; } }

    public void Created(ulong id, string owner)
    {
        lock (_gate) _live[id] = owner;
    }

    public void Freed(ulong id)
    {
        lock (_gate)
        {
            if (!_live.Remove(id)) _unknownFrees++;
        }
    }

    public IReadOnlyList<Entry> Live()
    {
        lock (_gate) return _live.Select(kv => new Entry(kv.Key, kv.Value)).OrderBy(e => e.Id).ToList();
    }

    /// <summary>"3 live: terrain chunk x2, water mesh x1", or "none live".</summary>
    public string Summary()
    {
        lock (_gate)
        {
            if (_live.Count == 0) return "none live";
            var sb = new StringBuilder().Append(_live.Count).Append(" live: ");
            sb.AppendJoin(", ", _live.Values.GroupBy(o => o).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} x{g.Count()}"));
            return sb.ToString();
        }
    }
}

/// <summary>
/// A teardown that runs every step even when an earlier one throws. A step that threw used to skip all the steps
/// after it, and a skipped free leaves engine resources live at quit. The first exception no longer hides the rest:
/// each failure goes to the report sink as it happens, and all of them are raised together at the end.
/// </summary>
public static class TeardownSteps
{
    /// <summary>A step that takes at least this long is reported to the slow-step sink.</summary>
    public const int SlowStepMilliseconds = 50;

    public static void RunAll(params (string Name, Action Step)[] steps) => RunAll(_ => { }, steps);

    public static void RunAll(Action<string> report, params (string Name, Action Step)[] steps) => RunAll(report, (_, _) => { }, steps);

    /// <summary>As above, and each step that takes <see cref="SlowStepMilliseconds"/> or more is also reported to <paramref name="slow"/> with its name and its time in milliseconds.</summary>
    public static void RunAll(Action<string> report, Action<string, double> slow, params (string Name, Action Step)[] steps)
    {
        List<Exception>? failures = null;
        foreach (var (name, step) in steps)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try { step(); }
            catch (Exception e)
            {
                (failures ??= new()).Add(new InvalidOperationException($"{name}: {e.GetType().Name}: {e.Message}", e));
                try { report($"teardown step '{name}' failed: {e.GetType().Name}: {e.Message}"); } catch { /* the sink must not stop the remaining steps */ }
            }
            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (ms >= SlowStepMilliseconds)
                try { slow(name, ms); } catch { /* the sink must not stop the remaining steps */ }
        }
        if (failures is not null)
            throw new AggregateException("teardown: " + string.Join("; ", failures.Select(f => f.Message)), failures);
    }
}
