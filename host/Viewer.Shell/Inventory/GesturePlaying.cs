// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Inventory;

namespace Viewer.Shell.Inventory;

/// <summary>What playing a gesture does: start or stop an animation on the user, play a sound, say a line; and whether an animation plays.</summary>
public interface IGestureEffects
{
    void Animate(Guid animation, bool start);
    void Sound(Guid sound);
    void Say(string text);
    bool IsPlaying(Guid animation);
}

/// <summary>
/// Plays a gesture as the official gesture manager steps one (llgesturemgr.cpp stepGesture and runStep), in our words: the steps run one
/// after another without pause until a wait step; a timed wait waits its seconds; "until animations are done" waits until the animations the
/// gesture started have stopped, for at most 30 seconds (MAX_WAIT_ANIM_SECS); after the last step the gesture waits, with no limit, until its
/// animations have stopped of themselves, and stops none. An animation asked for counts as playing once the region lists it, or for a
/// short grace while it has not yet appeared. A gesture stopped (its token cancelled) stops the animations it started and runs no
/// further step (llgesturemgr.cpp stopGesture).
/// </summary>
public static class GesturePlayer
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(0.1), Grace = TimeSpan.FromSeconds(2), StepWait = TimeSpan.FromSeconds(30);

    public static async Task PlayAsync(GestureDocument gesture, IGestureEffects fx, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
    {
        var started = new List<Guid>();
        var seen = new HashSet<Guid>();
        try
        {
            await RunAsync(gesture, fx, delay, started, seen, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            foreach (var animation in started)
                fx.Animate(animation, false);
            throw;
        }
    }

    private static async Task RunAsync(GestureDocument gesture, IGestureEffects fx, Func<TimeSpan, CancellationToken, Task> delay, List<Guid> started, HashSet<Guid> seen, CancellationToken ct)
    {
        foreach (var step in gesture.Steps.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            switch (step.Kind)
            {
                case GestureStepKind.Animation when step.AssetId != Guid.Empty:
                    fx.Animate(step.AssetId, !step.Stops);
                    if (step.Stops)
                        started.Remove(step.AssetId);
                    else if (!started.Contains(step.AssetId))
                        started.Add(step.AssetId);
                    break;
                case GestureStepKind.Sound when step.AssetId != Guid.Empty:
                    fx.Sound(step.AssetId);
                    break;
                case GestureStepKind.Chat when step.Text.Length > 0:
                    fx.Say(step.Text);
                    break;
                case GestureStepKind.Wait when step.WaitsForTime:
                    if (step.Seconds > 0)
                        await delay(TimeSpan.FromSeconds(step.Seconds), ct).ConfigureAwait(false);
                    break;
                case GestureStepKind.Wait when step.WaitsForAnimations:
                    await WaitForAnimations(started, seen, fx, delay, StepWait, ct).ConfigureAwait(false);
                    break;
            }
        }
        await WaitForAnimations(started, seen, fx, delay, TimeSpan.MaxValue, ct).ConfigureAwait(false);
    }

    private static async Task WaitForAnimations(List<Guid> started, HashSet<Guid> seen, IGestureEffects fx, Func<TimeSpan, CancellationToken, Task> delay, TimeSpan cap, CancellationToken ct)
    {
        var waited = TimeSpan.Zero;
        while (waited < cap)
        {
            bool busy = false;
            foreach (var a in started)
            {
                if (fx.IsPlaying(a))
                {
                    seen.Add(a);
                    busy = true;
                }
                else if (!seen.Contains(a) && waited < Grace)
                    busy = true;   // asked for, not listed yet
            }
            if (!busy)
                return;
            await delay(Poll, ct).ConfigureAwait(false);
            waited += Poll;
        }
    }
}

/// <summary>
/// The gestures active for the user, each with its file once read: a line of nearby chat with a trigger plays that gesture and comes back
/// revised (null when the line holds no trigger), and a key with its modifier plays the gesture bound to it (<see cref="GestureTriggers"/>).
/// </summary>
public sealed class ActiveGestures(Action<GestureDocument> play, Func<int, int> pick)
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, GestureDocument> _byItem = new();

    public void Set(Guid itemId, GestureDocument gesture) { lock (_gate) _byItem[itemId] = gesture; }

    public void Remove(Guid itemId) { lock (_gate) _byItem.Remove(itemId); }

    public void Clear() { lock (_gate) _byItem.Clear(); }

    public bool IsActive(Guid itemId) { lock (_gate) return _byItem.ContainsKey(itemId); }

    /// <summary>An active gesture's file, or null when it is not active.</summary>
    public GestureDocument? Get(Guid itemId) { lock (_gate) return _byItem.GetValueOrDefault(itemId); }

    /// <summary>The items of the active gestures whose files are read.</summary>
    public IReadOnlyList<Guid> Items() { lock (_gate) return _byItem.Keys.ToArray(); }

    private GestureDocument[] All() { lock (_gate) return _byItem.Values.ToArray(); }

    public string? Revise(string line)
    {
        if (GestureTriggers.Match(line, All(), pick) is not { } hit)
            return null;
        play(hit.Gesture);
        return hit.Revised;
    }

    public bool Key(byte key, uint mask)
    {
        if (GestureTriggers.MatchKey(key, mask, All(), pick) is not { } g)
            return false;
        play(g);
        return true;
    }
}
