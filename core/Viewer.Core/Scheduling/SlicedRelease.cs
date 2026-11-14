// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Scheduling;

/// <summary>
/// A list of things to let go of, released a slice at a time so no single frame pays for all of them. Each call to <see cref="Step"/>
/// releases items in order until the time given has been used (at least one item, so the work always advances). Plain .NET; the
/// clock is passed in, so a test can count a made-up cost per item instead of waiting.
/// </summary>
public sealed class SlicedRelease<T>
{
    private readonly IReadOnlyList<T> _items;
    private readonly Action<T> _release;
    private int _next;

    public SlicedRelease(IReadOnlyList<T> items, Action<T> release)
    {
        _items = items;
        _release = release;
    }

    /// <summary>Items not yet released.</summary>
    public int Remaining => _items.Count - _next;

    /// <summary>Items whose release threw (the rest were still released).</summary>
    public int Failed { get; private set; }

    public bool Done => _next >= _items.Count;

    /// <summary>Releases items in order until <paramref name="budgetMs"/> has passed on <paramref name="nowMs"/>; returns how many.</summary>
    public int Step(double budgetMs, Func<double> nowMs)
    {
        double start = nowMs();
        int n = 0;
        while (!Done)
        {
            ReleaseNext();
            n++;
            if (nowMs() - start >= budgetMs) break;
        }
        return n;
    }

    /// <summary>Releases everything left now (shutdown, where there is no next frame); returns how many.</summary>
    public int ReleaseAll()
    {
        int n = 0;
        while (!Done) { ReleaseNext(); n++; }
        return n;
    }

    private void ReleaseNext()
    {
        var item = _items[_next++];
        try { _release(item); }
        catch (Exception) { Failed++; }
    }
}
