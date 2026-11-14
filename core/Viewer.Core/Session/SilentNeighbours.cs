// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Session;

/// <summary>A transport that can prod a child circuit that has delivered nothing (it sends that circuit an agent update again).</summary>
public interface IChildCircuitNudger
{
    void Nudge(SimRef sim);
}

/// <summary>
/// Which neighbours have delivered nothing for too long, and when each is asked again. A neighbour counts as silent while its world
/// holds no terrain and no objects; it is prodded after <see cref="First"/> and again at doubling intervals, at most <see cref="MaxNudges"/>
/// times, then left alone (and logged by name each time so a run shows what happened). Pure: the caller gives the time.
/// </summary>
public sealed class SilentNeighbourWatch
{
    public static readonly TimeSpan First = TimeSpan.FromSeconds(10);
    public const int MaxNudges = 3;

    private sealed class Entry
    {
        public int Nudges;
        public DateTime Next;
    }

    private readonly Dictionary<SimRef, Entry> _watched = new();

    /// <summary>Starts watching <paramref name="sim"/> from <paramref name="now"/>.</summary>
    public void Added(SimRef sim, DateTime now)
    {
        lock (_watched) _watched[sim] = new Entry { Next = now + First };
    }

    /// <summary>The neighbour has delivered something (or is gone): it is not watched any more.</summary>
    public void Heard(SimRef sim)
    {
        lock (_watched) _watched.Remove(sim);
    }

    /// <summary>Neighbours due a prod at <paramref name="now"/>, with how many prods each has had (counting this one).</summary>
    public IReadOnlyList<(SimRef Sim, int Nudge)> Due(DateTime now)
    {
        var due = new List<(SimRef, int)>();
        lock (_watched)
        {
            foreach (var (sim, e) in _watched.ToArray())
            {
                if (e.Nudges >= MaxNudges || now < e.Next)
                    continue;
                e.Nudges++;
                e.Next = now + First * Math.Pow(2, e.Nudges);
                due.Add((sim, e.Nudges));
            }
        }
        return due;
    }
}
