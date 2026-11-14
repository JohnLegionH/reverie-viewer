// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file llmotioncontroller.cpp
 * @brief Implementation of LLMotionController class.
 *
 * $LicenseInfo:firstyear=2001&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2010, Linden Research, Inc.
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation;
 * version 2.1 of the License only.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 *
 * Linden Research, Inc., 945 Battery Street, San Francisco, CA  94111  USA
 * $/LicenseInfo$
 */

/**
 * @File llvoavatar.cpp
 * @brief Implementation of LLVOAvatar class which is a derivation of LLViewerObject
 *
 * $LicenseInfo:firstyear=2001&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2010, Linden Research, Inc.
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation;
 * version 2.1 of the License only.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 *
 * Linden Research, Inc., 945 Battery Street, San Francisco, CA  94111  USA
 * $/LicenseInfo$
 */

// Adapted 2026 by Legion Builds. The way a motion starts, stops, deprecates and eases (LLMotionController startMotion, stopMotion and updateMotionsByType) and
// the way an avatar starts and stops its motions (LLVOAvatar::processAnimationStateChanges) follow the files named above, rewritten in C# for our own types.
// The report of a motion that runs out on its own follows the send-stop time in updateMotionsByType (identical in release 26.4, tag Second_Life_Release#e960123-26.4,
// commit e960123230d521e8557a9582100bebcac36a7ca2).
// The rest of this file is our own code.
// Copyright (c) 2026 Legion Builds

namespace Viewer.Core.Avatars;

/// <summary>An animation the region says plays on an avatar, with its clip (ready to play) and the region's sequence number for it.</summary>
public readonly record struct SignalledMotion(Guid Id, AnimationClip Clip, int Sequence);

/// <summary>One playing instance of an animation: which, when it started, its weight at a time and whether it has been told to stop.</summary>
public readonly record struct MotionState(Guid Id, int Instance, double Activation, double Weight, bool Stopped);

/// <summary>
/// The motions of one avatar as the official viewer's motion controller keeps them: an animation starts when it is newly signalled (or its sequence
/// number changed while it was still blending or stopping), stops when it is no longer signalled, and fades in and out by cubic_step over its clip's
/// ease durations. A motion that is active and not blending keeps playing when it is signalled again (llmotioncontroller.cpp startMotion); one that is
/// blending or stopping is deprecated, which stops it (it finishes easing out) and a new instance starts. Time is the caller's clock in seconds.
/// <see cref="Mix"/> is the clip that poses the avatar; it is made again only when a motion starts, stops or finishes, because the weights are a
/// function of time and are read inside it.
/// </summary>
public sealed class MotionController
{
    private sealed class Instance
    {
        public int Serial;
        public Guid Id;
        public AnimationClip Clip = null!;
        public int Sequence;
        public int Order;
        public MotionTiming Timing;
        public bool Deprecated;
        public bool SelfStop, Notified;   // a clip that does not loop stops itself at the end of its duration; Notified: that has been reported

        public bool Stopped(double now) => Timing.Stop is { } s && s <= now;
    }

    private sealed class Running(IProceduralMotion motion, MotionTiming timing, int order)
    {
        public readonly IProceduralMotion Motion = motion;
        public MotionTiming Timing = timing;
        public readonly int Order = order;
        public bool Stopped(double now) => Timing.Stop is { } s && s <= now;
    }

    private readonly List<Running> _procedural = new();         // the built-in motions: they run until stopped and are not animations
    private readonly Dictionary<Guid, int> _playing = new();   // what is told to play: animation id to the sequence number it started with
    private readonly List<Instance> _live = new();             // live instances, the newest last
    private int _serial, _order;
    private AnimationClip? _mix;
    private int _signature;
    private bool _signatureKnown;
    private double _nextFinish = double.PositiveInfinity;   // the first time a stopped motion is done easing out

    /// <summary>The avatar's facial expressions: an animation that names an expression as its emote starts it when the animation starts.</summary>
    public FaceExpressions Expressions { get; } = new();

    /// <summary>A joint's own rotation (the skeleton's), for an additive built-in motion on a joint no animation turns; null: the identity.</summary>
    public Func<string, System.Numerics.Quaternion?>? DefaultRotation { get; set; }

    /// <summary>Raised with the animation's id when a motion that does not loop reaches its end on its own (the official requestStopMotion for a motion that ran out of time).</summary>
    public event Action<Guid>? MotionRanOut;

    /// <summary>Starts a built-in motion at <paramref name="now"/> (false when it already runs).</summary>
    public bool StartProcedural(IProceduralMotion motion, double now)
    {
        if (_procedural.Any(p => p.Motion.Name == motion.Name && !p.Stopped(now))) return false;
        _procedural.Add(new Running(motion, new MotionTiming(now, motion.EaseIn, motion.EaseOut, null, 0), ++_order));
        _mix = Build();
        _nextFinish = NextFinish();
        return true;
    }

    /// <summary>Stops the built-in motion called <paramref name="name"/> at <paramref name="now"/> (false when none runs).</summary>
    public bool StopProcedural(string name, double now)
    {
        var running = _procedural.FirstOrDefault(p => p.Motion.Name == name && !p.Stopped(now));
        if (running is null) return false;
        running.Timing = running.Timing.StoppedAt(now);
        _mix = Build();
        _nextFinish = NextFinish();
        return true;
    }

    /// <summary>Whether the built-in motion called <paramref name="name"/> runs (and has not been stopped) at <paramref name="now"/>.</summary>
    public bool IsRunning(string name, double now) => _procedural.Any(p => p.Motion.Name == name && !p.Stopped(now));

    /// <summary>The clip that poses the avatar from the motions now live (null: none). Its time is the caller's clock.</summary>
    public AnimationClip? Mix => _mix;

    /// <summary>Applies what the region now says plays at <paramref name="now"/>. True when the motions that make up <see cref="Mix"/> changed.</summary>
    public bool Update(double now, IReadOnlyList<SignalledMotion> signalled)
    {
        // nothing to do while the same animations are signalled and no motion has finished easing out
        int signature = Signature(signalled);
        if (_signatureKnown && signature == _signature && now <= _nextFinish) return false;
        _signature = signature;
        _signatureKnown = true;
        bool changed = false;
        // a clip that does not loop has run out on its own once its stop time is past (llmotioncontroller.cpp: the send-stop time); each is reported once
        List<Guid>? ranOut = null;
        foreach (var i in _live)
            if (i.SelfStop && !i.Notified && i.Timing.Stop is { } own && own <= now) { i.Notified = true; (ranOut ??= []).Add(i.Id); }
        // motions that have finished easing out are gone
        if (_live.RemoveAll(i => i.Timing.Finished(now)) > 0) changed = true;
        if (_procedural.RemoveAll(p => p.Timing.Finished(now)) > 0) changed = true;
        var wanted = signalled.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.OrderBy(s => s.Sequence).Last());
        // playing, but not signalled: stop
        foreach (var id in _playing.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
        {
            _playing.Remove(id);
            foreach (var i in _live.Where(i => i.Id == id && !i.Stopped(now))) { Stop(i, now); changed = true; }
        }
        // signalled but not playing, or another sequence number: start (the oldest first, so a newer one is ahead among equals)
        foreach (var s in wanted.Values.OrderBy(s => s.Sequence))
        {
            if (_playing.TryGetValue(s.Id, out var seq) && seq == s.Sequence) continue;
            _playing[s.Id] = s.Sequence;
            if (Start(s, now)) changed = true;
        }
        if (changed) _mix = Build();
        _nextFinish = NextFinish();
        if (ranOut is not null) foreach (var id in ranOut) MotionRanOut?.Invoke(id);
        return changed;
    }

    private double NextFinish() =>
        _live.Where(i => i.Timing.Stop is not null).Select(i => i.Timing.Stop!.Value + i.Timing.EaseOut)
            .Concat(_live.Where(i => i.SelfStop && !i.Notified && i.Timing.Stop is not null).Select(i => i.Timing.Stop!.Value))
            .Concat(_procedural.Where(p => p.Timing.Stop is not null).Select(p => p.Timing.Stop!.Value + p.Timing.EaseOut))
            .DefaultIfEmpty(double.PositiveInfinity).Min();

    private static int Signature(IReadOnlyList<SignalledMotion> signalled)
    {
        var h = new HashCode();
        h.Add(signalled.Count);
        foreach (var s in signalled) { h.Add(s.Id); h.Add(s.Sequence); h.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(s.Clip)); }
        return h.ToHashCode();
    }

    /// <summary>The live instances and their weights at <paramref name="now"/>, oldest first.</summary>
    public IReadOnlyList<MotionState> States(double now) =>
        _live.Select(i => new MotionState(i.Id, i.Serial, i.Timing.Activation, i.Timing.Weight(now), i.Stopped(now))).ToList();

    private bool Start(SignalledMotion s, double now)
    {
        var current = _live.LastOrDefault(i => i.Id == s.Id && !i.Deprecated);
        if (current is not null)
        {
            bool blending = current.Timing.Weight(now) < 1.0;
            if (!blending && !current.Stopped(now)) { current.Sequence = s.Sequence; return false; }   // active and not blending: it keeps playing
            current.Deprecated = true;
            if (!current.Stopped(now)) Stop(current, now);
        }
        _live.Add(new Instance
        {
            Serial = ++_serial, Id = s.Id, Clip = s.Clip, Sequence = s.Sequence, Order = ++_order,
            Timing = MotionTiming.Start(s.Clip, now), SelfStop = s.Clip.Duration > 0 && !s.Clip.Loop,
        });
        // an animation that names an expression as its emote starts it when it starts, unless it already plays (llkeyframemotion.cpp onActivate)
        if (Emotes.FromEmoteName(s.Clip.Emote) is { } emote) Expressions.StartIfInactive(emote, now);
        return true;
    }

    private static void Stop(Instance i, double now)
    {
        if (i.Timing.Stop is not { } own || own > now) i.SelfStop = false;   // stopped from outside before it ran out: not a run-out
        i.Timing = i.Timing.StoppedAt(now);
    }

    private AnimationClip? Build()
    {
        if (_live.Count == 0 && _procedural.Count == 0) return null;
        return AnimationMix.Blend(_live.Select(i => new BlendInput(i.Clip, i.Timing, i.Order)).ToList(),
            _procedural.Select(p => new ProceduralInput(p.Motion, p.Timing, p.Order)).ToList(), DefaultRotation);
    }
}
