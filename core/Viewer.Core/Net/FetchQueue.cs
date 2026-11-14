// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Concurrent;
using System.Diagnostics;

namespace Viewer.Core.Net;

/// <summary>What a texture request is for, in the order the fetch slots serve the classes. Base colour: a face's own
/// (legacy) texture, what a face without a PBR material looks like. PBR base colour: a PBR material's base colour.
/// Detail: a PBR material's normal, metallic-roughness (ORM) or emissive map, which only refines a face already drawn
/// in its base colour.</summary>
public enum TextureFetchClass { BaseColour = 0, PbrBaseColour = 1, Detail = 2 }

/// <summary>
/// The texture fetch slots: at most <see cref="Slots"/> requests hold a slot at once and the rest wait here. A freed slot
/// goes to the first-arrived waiting request of the earliest class (<see cref="TextureFetchClass"/>: faces' own textures,
/// then PBR base colours, then detail maps), unless the request that has waited longest has waited at least
/// <see cref="Aging"/>: then it goes first, whatever its class. So a face's own texture is not held up behind PBR
/// textures, a PBR base colour is not held up behind detail maps, a request takes a free slot whenever no request of an
/// earlier class waits, and every wait is bounded: once a request has aged, no request that arrived after it goes first,
/// so it waits at most the aging time plus the time the requests queued ahead of it take. Within a class, first come
/// first served (the order the residency manager asked in, highest priority first).
/// </summary>
public sealed class FetchQueue
{
    /// <summary>How long a request waits behind requests of an earlier class before it is served in arrival order with them.</summary>
    public static readonly TimeSpan DefaultAging = TimeSpan.FromSeconds(2);

    private sealed class Waiter
    {
        public required TextureFetchClass Class;
        public required TimeSpan Arrived;
        public required long Seq;
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node;
        public CancellationTokenRegistration Cancel;
    }

    private readonly object _lock = new();
    private readonly LinkedList<Waiter> _waiting = new();
    private readonly Func<TimeSpan> _clock;
    private readonly ClassStats[] _stats = { new(), new(), new() };
    private int _free;
    private long _seq;

    /// <param name="slots">Requests that may hold a slot at once.</param>
    /// <param name="aging">See <see cref="DefaultAging"/>.</param>
    /// <param name="clock">A monotonic clock (tests pass their own); the process's stopwatch otherwise.</param>
    public FetchQueue(int slots, TimeSpan? aging = null, Func<TimeSpan>? clock = null)
    {
        if (slots < 1) throw new ArgumentOutOfRangeException(nameof(slots));
        Slots = _free = slots;
        Aging = aging ?? DefaultAging;
        long start = Stopwatch.GetTimestamp();
        _clock = clock ?? (() => Stopwatch.GetElapsedTime(start));
    }

    public int Slots { get; }
    public TimeSpan Aging { get; }

    /// <summary>Requests waiting for a slot now.</summary>
    public int Waiting { get { lock (_lock) return _waiting.Count; } }

    /// <summary>Waits for a slot (the caller releases it with <see cref="Release"/>). Cancelling the token while it waits
    /// removes the request and throws <see cref="OperationCanceledException"/>; a slot already granted stays the caller's.</summary>
    public Task EnterAsync(TextureFetchClass cls, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Waiter w;
        lock (_lock)
        {
            var now = _clock();
            if (_free > 0)
            {
                _free--;
                _stats[(int)cls].Served(TimeSpan.Zero, aged: false);
                return Task.CompletedTask;
            }
            w = new Waiter { Class = cls, Arrived = now, Seq = _seq++ };
            w.Node = _waiting.AddLast(w);
        }
        if (ct.CanBeCanceled)
            w.Cancel = ct.Register(() =>
            {
                lock (_lock)
                {
                    if (w.Node is null) return;   // already granted
                    _waiting.Remove(w.Node);
                    w.Node = null;
                }
                w.Done.TrySetCanceled(ct);
            });
        return w.Done.Task;
    }

    /// <summary>Gives a slot back: it goes to the next waiting request at once, or becomes free.</summary>
    public void Release()
    {
        Waiter? next;
        lock (_lock)
        {
            next = Next(_clock(), out bool aged);
            if (next is null) { _free++; return; }
            _waiting.Remove(next.Node!);
            next.Node = null;
            _stats[(int)next.Class].Served(_clock() - next.Arrived, aged);
        }
        next.Cancel.Dispose();
        next.Done.TrySetResult();
    }

    /// <summary>The longest-waiting request if it has aged; else the first-arrived request of the earliest class waiting.
    /// The list is in arrival order, so its first request is the one that has waited longest. Aged: the request goes
    /// ahead of a waiting request of an earlier class.</summary>
    private Waiter? Next(TimeSpan now, out bool aged)
    {
        aged = false;
        var oldest = _waiting.First?.Value;
        if (oldest is null) return null;
        Waiter best = oldest;
        for (var n = _waiting.First; n is not null && best.Class != TextureFetchClass.BaseColour; n = n.Next)
            if (n.Value.Class < best.Class) best = n.Value;
        if (best != oldest && now - oldest.Arrived >= Aging) { aged = true; return oldest; }
        return best;
    }

    /// <summary>The waits so far, per class.</summary>
    public FetchQueueStats Stats
    {
        get
        {
            lock (_lock)
                return new FetchQueueStats(_stats[0].Snapshot(), _stats[1].Snapshot(), _stats[2].Snapshot());
        }
    }

    private sealed class ClassStats
    {
        private const int MaxSamples = 65536;
        private readonly List<double> _waitMs = new();
        private long _requests, _aged;
        private double _maxMs;

        public void Served(TimeSpan wait, bool aged)
        {
            _requests++;
            if (aged) _aged++;
            double ms = wait.TotalMilliseconds;
            if (ms > _maxMs) _maxMs = ms;
            if (_waitMs.Count < MaxSamples) _waitMs.Add(ms);
        }

        public FetchClassStats Snapshot()
        {
            var v = _waitMs.OrderBy(x => x).ToArray();
            double Rank(double p) => v.Length == 0 ? 0 : v[Math.Clamp((int)Math.Ceiling(p * v.Length) - 1, 0, v.Length - 1)];
            return new FetchClassStats(_requests, v.Count(x => x > 0), Math.Round(Rank(0.5), 1), Math.Round(Rank(0.95), 1), Math.Round(_maxMs, 1), _aged);
        }
    }
}

/// <param name="Requests">Requests given a slot.</param>
/// <param name="Waited">Of those, requests that waited for it.</param>
/// <param name="Aged">Requests served ahead of a waiting request of an earlier class because they had waited the aging time.</param>
public sealed record FetchClassStats(long Requests, long Waited, double WaitP50Ms, double WaitP95Ms, double WaitMaxMs, long Aged);

public sealed record FetchQueueStats(FetchClassStats BaseColour, FetchClassStats PbrBaseColour, FetchClassStats Detail);

/// <summary>
/// Each texture's fetch class: the earliest class among its users (a texture also used as a face's own texture is
/// fetched as one; a detail map also used as a PBR base colour is fetched as a PBR base colour). Textures not listed are
/// base colours. Written by the residency manager on the main thread, read by the fetches on any thread.
/// </summary>
public sealed class TextureFetchClasses
{
    private readonly ConcurrentDictionary<Guid, TextureFetchClass> _class = new();

    public TextureFetchClass Of(Guid texture) => _class.TryGetValue(texture, out var c) ? c : TextureFetchClass.BaseColour;

    public void Set(Guid texture, TextureFetchClass cls)
    {
        if (cls != TextureFetchClass.BaseColour) _class[texture] = cls;
        else _class.TryRemove(texture, out _);
    }

    public int DetailTextures => _class.Count(kv => kv.Value == TextureFetchClass.Detail);

    public int PbrBaseColourTextures => _class.Count(kv => kv.Value == TextureFetchClass.PbrBaseColour);

    public void Clear() => _class.Clear();
}
