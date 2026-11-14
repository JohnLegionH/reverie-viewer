// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using Viewer.Core.World;

namespace Viewer.Shell;

/// <summary>What the presentation does with the core's changes (the Godot side: terrain now, objects).</summary>
public interface IPresentationSink
{
    /// <summary>Applies one change cheaply (bookkeeping only: mark what is dirty). Main thread.</summary>
    void Apply(WorldChange change);

    /// <summary>Does one unit of deferred work (rebuild one dirty chunk); false when there is none. Main thread.</summary>
    bool Step();
}

/// <summary>One frame's drain, for the observe command and the budget measurements.</summary>
public readonly record struct DrainStats(int Applied, int Steps, int Backlog, double OldestAgeMs, double SpentMs, bool OverBudget);

/// <summary>
/// Presentation coordinator. The core raises <see cref="WorldChange"/>s on its network threads; they are queued here
/// with their arrival time and drained on the main thread by <see cref="RunFrame"/>, within a per-frame time budget:
/// first the queued changes (cheap), then deferred work (<see cref="IPresentationSink.Step"/>) while time remains.
/// Whatever does not fit waits for the next frame; <see cref="BacklogAgeMs"/> says how old the oldest waiting change is.
/// A frame always makes progress (at least one change or step), so a budget smaller than one unit cannot stall it.
/// </summary>
public sealed class PresentationCoordinator
{
    private readonly ConcurrentQueue<(WorldChange Change, long At, Slot Slot)> _queue = new();
    private readonly IPresentationSink _sink;
    private readonly Func<long> _now;
    private readonly double _tickMs;
    private long _enqueued, _applied, _framesOverBudget, _frames;

    /// <param name="clock">Ticks (tests pass a fake); default <see cref="Stopwatch.GetTimestamp"/>.</param>
    /// <param name="ticksPerSecond">The clock's frequency; default <see cref="Stopwatch.Frequency"/>.</param>
    public PresentationCoordinator(IPresentationSink sink, double budgetMs = 4.0, Func<long>? clock = null, long ticksPerSecond = 0)
    {
        _sink = sink;
        BudgetMs = budgetMs;
        _now = clock ?? Stopwatch.GetTimestamp;
        _tickMs = 1000.0 / (ticksPerSecond > 0 ? ticksPerSecond : Stopwatch.Frequency);
    }

    public double BudgetMs { get; set; }

    /// <summary>The frame's allowance shared with the other presentation work (avatars, posing): when set, this coordinator spends no more
    /// than <see cref="BudgetMs"/> and no more than the <see cref="SharedShare"/> of the shared allowance that is left when it starts, so
    /// the work after it keeps the rest. Null: <see cref="BudgetMs"/> alone, as before. A <see cref="BudgetMs"/> above the shared allowance
    /// (a caller draining everything) is not cut by it.</summary>
    public FrameBudget? Shared { get; set; }

    /// <summary>The share of <see cref="Shared"/> this coordinator may take (0 to 1).</summary>
    public double SharedShare { get; set; } = 0.45;

    public int Backlog => _queue.Count + _prompt.Count;
    public long Enqueued => Interlocked.Read(ref _enqueued);
    public long Applied => Interlocked.Read(ref _applied);
    public long Frames => Interlocked.Read(ref _frames);
    public long FramesOverBudget => Interlocked.Read(ref _framesOverBudget);
    public DrainStats Last { get; private set; }

    /// <summary>Age of the oldest change still queued, in ms (0 when empty).</summary>
    public double BacklogAgeMs => _queue.TryPeek(out var head) ? (_now() - head.At) * _tickMs : 0;

    /// <summary>Which changes also go to the fast lane (null: none). A change the lane takes is applied by <see cref="ApplyUrgent"/> at the start of the
    /// next frame, at once, whatever waits in the budgeted queue; it also waits its turn in that queue, where the sink is told it was applied early
    /// (<see cref="AppliedEarly"/>) and must not do the early work again. For changes whose result a person is waiting on and that cost nothing to apply (an
    /// avatar's arrival and movement in a region whose thousands of objects are still being taken in).</summary>
    public Func<WorldChange, bool>? IsUrgent { get; set; }

    /// <summary>What applies a fast-lane change.</summary>
    public Action<WorldChange>? ApplyUrgent { get; set; }

    /// <summary>The most fast-lane changes one frame applies; the rest wait for the next frame.</summary>
    public int UrgentPerFrame { get; set; } = 512;

    /// <summary>Which changes the prompt lane takes (null: none). A prompt change is applied by <see cref="ApplyPrompt"/> at the start of the next
    /// frame within <see cref="PromptBudgetMs"/>, ahead of the queue; unless <see cref="PromptAlsoQueued"/> says it also waits in the queue, it is
    /// applied only there. For work that is cheap to apply and that nothing else may wait behind (a region's terrain patches).</summary>
    public Func<WorldChange, bool>? IsPrompt { get; set; }

    /// <summary>Which prompt changes also wait their turn in the queue (null: all).</summary>
    public Func<WorldChange, bool>? PromptAlsoQueued { get; set; }

    /// <summary>What applies a prompt change.</summary>
    public Action<WorldChange>? ApplyPrompt { get; set; }

    /// <summary>The share of the frame's limit the prompt lane may take, so the queue and the deferred work keep the rest.</summary>
    public double PromptShare { get; set; } = 0.3;

    /// <summary>The time one frame may spend on the prompt lane (a frame always applies at least one).</summary>
    public double PromptBudgetMs { get; set; } = 1.0;

    /// <summary>The share of the frame's limit the queued changes may take before the deferred work gets its turn; the deferred work
    /// that finds nothing to do hands its time back to the queue. 1: the queue may take all of it, as before.</summary>
    public double ApplyShare { get; set; } = 1.0;

    private readonly ConcurrentQueue<WorldChange> _prompt = new();
    private readonly ConcurrentQueue<(WorldChange Change, Slot Slot)> _urgent = new();

    /// <summary>One change that is in both lanes: whichever lane takes it first marks it, and the other leaves it alone, so a change is never
    /// applied twice (a second, later application of an older avatar state moves the avatar back).</summary>
    private sealed class Slot { public volatile bool Applied; }

    /// <summary>True while the sink is given a change the fast lane has already applied: the sink applies it for everything except what the
    /// fast lane did (<see cref="ApplyUrgent"/>). Main thread, during <see cref="IPresentationSink.Apply"/>.</summary>
    public bool AppliedEarly { get; private set; }

    /// <summary>Any thread: queue a change (subscribe this to <c>WorldState.Changed</c>).</summary>
    public void Enqueue(WorldChange change)
    {
        var slot = new Slot();
        if (ApplyUrgent is not null && IsUrgent?.Invoke(change) == true)
            _urgent.Enqueue((change, slot));
        bool prompt = ApplyPrompt is not null && IsPrompt?.Invoke(change) == true;
        if (prompt)
            _prompt.Enqueue(change);
        if (!prompt || PromptAlsoQueued?.Invoke(change) != false)
            _queue.Enqueue((change, _now(), slot));
        Interlocked.Increment(ref _enqueued);
    }

    /// <summary>Main thread, once per frame.</summary>
    public DrainStats RunFrame()
    {
        long start = _now();
        double limit = Shared is null || BudgetMs > Shared.TotalMs ? BudgetMs : Math.Min(BudgetMs, Shared.AllowanceMs(SharedShare));
        double Spent() => (_now() - start) * _tickMs;
        int applied = 0, steps = 0;
        for (int fast = 0; fast < UrgentPerFrame && ApplyUrgent is { } early && _urgent.TryDequeue(out var urgent); fast++)
        {
            if (urgent.Slot.Applied) continue;   // the budgeted queue reached it first
            urgent.Slot.Applied = true;
            early(urgent.Change);
        }
        if (ApplyPrompt is { } promptly)
            for (int p = 0; (p == 0 || Spent() < Math.Min(PromptBudgetMs, limit * PromptShare)) && _prompt.TryDequeue(out var early1); p++)
            {
                promptly(early1);
                applied++;
            }
        double applyLimit = limit * Math.Clamp(ApplyShare, 0, 1);
        while ((applied == 0 || Spent() < applyLimit) && _queue.TryDequeue(out var item))
        {
            AppliedEarly = item.Slot.Applied;
            item.Slot.Applied = true;
            _sink.Apply(item.Change);
            AppliedEarly = false;
            applied++;
        }
        while ((applied + steps == 0 || Spent() < limit) && _sink.Step())
            steps++;
        // the deferred work found nothing to do, or the frame has time left: the queue takes what it left
        while (ApplyShare < 1 && Spent() < limit && _queue.TryDequeue(out var more))
        {
            AppliedEarly = more.Slot.Applied;
            more.Slot.Applied = true;
            _sink.Apply(more.Change);
            AppliedEarly = false;
            applied++;
        }
        double spent = Spent();
        bool over = spent > limit;
        Interlocked.Add(ref _applied, applied);
        Interlocked.Increment(ref _frames);
        if (over)
            Interlocked.Increment(ref _framesOverBudget);
        return Last = new DrainStats(applied, steps, Backlog, BacklogAgeMs, spent, over);
    }
}
