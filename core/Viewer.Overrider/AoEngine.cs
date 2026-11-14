// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file aoengine.cpp
 * @brief The core Animation Overrider engine
 *
 * $LicenseInfo:firstyear=2001&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2011, Zi Ree @ Second Life
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
 * $/LicenseInfo$
 */

// Adapted 2026 by Legion Builds: rewritten in C# from the file named above (the Firestorm viewer's indra/newview/aoengine.cpp):
// reading the sets from the inventory (tick, update, reloadStateAnimations, readAnimationLinks), the overrider itself (override,
// enable, enableStands, cycle, selectSet, checkBelowWater, inMouselook, onRegionChange, checkSitCancel, the tracks and the
// "Always" state) and its timers. The calls that feed it (MotionStarted, MotionStopped, SignalledChanged) follow the
// Firestorm viewer's llvoavatar.cpp (startMotion, stopMotion, processAnimationStateChanges).
// What differs from the source:
// - the inventory is only read: the folders are not made when they are missing, items that are not animation links are
//   passed over instead of being moved away, and sort orders are not written back;
// - sets are not edited, imported or saved, and the step each state played last is not kept between runs;
// - an animation is stopped by asking the region; the avatar's own copy stops when the region's list comes back;
// - timers are values read against a clock the caller passes in, checked by Tick.

using System.Collections.Immutable;
using System.Globalization;

namespace Viewer.Overrider;

/// <summary>How a state moves to another step.</summary>
public enum AoCycleMode { Any, Next, Previous }

/// <summary>
/// The animation overrider's engine: it reads the user's sets, and when the user's own avatar starts or stops one of the
/// grid's default animations it asks the region to start or stop the set's animation for that state. Main thread only.
/// </summary>
public sealed class AoEngine
{
    private const string RootFirestormFolder = "#Firestorm";
    private const string RootAoFolder = "#AO";
    private const double InventoryPollingInterval = 5.0;
    private const int TrackIndexSteps = -1;

    private sealed class TrackTimer(AoStateId state, int trackIndex)
    {
        public readonly AoStateId State = state;
        public readonly int TrackIndex = trackIndex;
        public readonly AoTimer Timer = new();
    }

    private readonly IAoInventory _inventory;
    private readonly IAoAgent _agent;
    private readonly Func<double> _clock;
    private readonly Func<double> _random;

    private readonly List<AoSet> _sets = [];
    private AoSet? _currentSet;
    private AoSet? _defaultSet;
    private bool _enabled;
    private bool _enabledStands;
    private bool _inMouselook;
    private bool _underWater;
    private Guid _aoFolder;
    private Guid _lastMotion = AoAnims.Stand;
    private Guid _lastOverriddenMotion = AoAnims.Stand;
    private Guid _ignoreMotionStopOnce;
    private Guid _transitionId;
    private AoState? _activeTrackState;
    private AoState? _activeAlwaysState;
    private readonly List<TrackTimer> _activeTrackTimers = [];
    private readonly List<TrackTimer> _alwaysTimers = [];

    // the sit cancel timer: ten looks, a tenth of a second apart
    private readonly AoTimer _sitCancelTimer = new() { Period = 0.1 };
    private int _sitCancelTicks;

    // the inventory timer
    private bool _started;
    private bool _inventoryTimer = true;
    private bool _polledOnce;
    private double _lastPoll;

    private Dictionary<Guid, int> _signalled = [];

    /// <param name="clock">The time in seconds; only differences are used.</param>
    /// <param name="random">A number from 0 up to, not including, 1 (for states and tracks that cycle at random).</param>
    public AoEngine(IAoInventory inventory, IAoAgent agent, Func<double> clock, Func<double>? random = null)
    {
        _inventory = inventory;
        _agent = agent;
        _clock = clock;
        _random = random ?? System.Random.Shared.NextDouble;
    }

    /// <summary>The setting: whether the overrider is to be used.</summary>
    public bool UseAo { get; private set; }

    /// <summary>The setting: whether standing and turning are replaced while the overrider is on.</summary>
    public bool UseAoStands { get; private set; } = true;

    /// <summary>The name of the set used last, kept by the caller between runs; it is chosen when no set is marked as the default.</summary>
    public string? SavedSetName { get; set; }

    public bool Enabled => _enabled;

    /// <summary>Whether the sets have been read from the inventory.</summary>
    public bool Loaded { get; private set; }

    public IReadOnlyList<AoSet> Sets => _sets;
    public AoSet? CurrentSet => _currentSet;
    public AoSet? DefaultSet => _defaultSet;

    /// <summary>The state of the default animation the avatar plays now, in the current set.</summary>
    public AoState? CurrentState => _currentSet?.GetStateByRemapId(_lastMotion);

    /// <summary>The sets were read.</summary>
    public event Action? Updated;

    /// <summary>What the overrider plays changed.</summary>
    public event Action? AnimationChanged;

    private void Send(Guid animation, bool start)
    {
        if (animation != Guid.Empty)   // a request for no animation is not sent
            _agent.SendAnimation(animation, start);
    }

    // ---------------- settings ----------------

    public void SetUseAo(bool use)
    {
        UseAo = use;
        Enable(use);
        if (_enabled && !UseAoStands)
            SetUseAoStands(true);   // enabling the AO always enables stands to start with
    }

    public void SetUseAoStands(bool use)
    {
        UseAoStands = use;
        EnableStands(use);
    }

    public void SetPaused(bool paused)
    {
        if (UseAo)
            Enable(!paused);
    }

    /// <summary>The user's own avatar is in the world: the settings take effect and the inventory is looked at from now on.</summary>
    public void LoginComplete()
    {
        _started = true;
        if (UseAo)
        {
            _enabled = true;
            EnableStands(true);   // enabling the AO always enables stands to start with
        }
        else
        {
            EnableStands(UseAoStands);
            Enable(false);
        }
    }

    // ---------------- timers ----------------

    /// <summary>Runs whatever timer is due. Called every frame.</summary>
    public void Tick()
    {
        double now = _clock();

        if (_started && _inventoryTimer && (!_polledOnce || now - _lastPoll >= InventoryPollingInterval))
        {
            _polledOnce = true;
            _lastPoll = now;
            PollInventory();
        }

        foreach (var set in _sets.ToArray())
        {
            if (set.Timer.Fires(now))
                CycleTimeout(set);
        }

        foreach (var timer in _activeTrackTimers.Concat(_alwaysTimers).ToArray())
        {
            if ((_activeTrackTimers.Contains(timer) || _alwaysTimers.Contains(timer)) && timer.Timer.Fires(now))
                TrackTimeout(timer.State, timer.TrackIndex);
        }

        if (_sitCancelTimer.Fires(now))
        {
            _sitCancelTicks++;
            CheckSitCancel();
            if (_sitCancelTicks == 10)
                _sitCancelTimer.Running = false;
        }
    }

    private void SitCancelOneShot()
    {
        _sitCancelTicks = 0;
        _sitCancelTimer.Start(0.1, _clock());
    }

    private TrackTimer NewTrackTimer(AoStateId state, int trackIndex, float period)
    {
        var timer = new TrackTimer(state, trackIndex);
        timer.Timer.Start(period, _clock());
        return timer;
    }

    // ---------------- stopping groups of animations ----------------

    private void StopAllStandVariants()
    {
        Send(AoAnims.Stand1, false);
        Send(AoAnims.Stand2, false);
        Send(AoAnims.Stand3, false);
        Send(AoAnims.Stand4, false);
    }

    private void StopAllSitVariants()
    {
        Send(AoAnims.SitFemale, false);
        Send(AoAnims.SitGeneric, false);

        // scripted seats that use ground_sit as animation need special treatment
        if (_agent.SittingOnObject)
            return;

        Send(AoAnims.SitGround, false);
        Send(AoAnims.SitGroundConstrained, false);
    }

    private void StopCurrentAnimations(AoState state, bool skipFirst = false)
    {
        for (int index = skipFirst ? 1 : 0; index < state.CurrentAnimationIds.Count; index++)
            Send(state.CurrentAnimationIds[index], false);
        state.CurrentAnimationIds = [];
    }

    private bool SwapStateAnimations(AoState state, List<Guid> animations)
    {
        var oldAnimations = state.CurrentAnimationIds;
        if (animations.SequenceEqual(oldAnimations))
            return false;

        state.CurrentAnimationIds = animations;

        foreach (Guid animation in animations)
        {
            if (!oldAnimations.Contains(animation))
                Send(animation, true);
        }

        foreach (Guid oldAnimation in oldAnimations)
        {
            if (!animations.Contains(oldAnimation))
                Send(oldAnimation, false);
        }

        return true;
    }

    // ---------------- tracks and the "Always" state ----------------

    private void StartStateTracks(AoState? state)
    {
        StopActiveTracks();

        if (state is null || !_enabled)
            return;

        for (int index = 0; index < state.Tracks.Count; index++)
        {
            var track = state.Tracks[index];
            if (track.Animations.Count == 0)
                continue;

            track.CurrentAnimationId = _currentSet is not null ? _currentSet.GetAnimationForTrack(track, _inventory, _random) : Guid.Empty;
            if (track.CurrentAnimationId == Guid.Empty)
                continue;

            Send(track.CurrentAnimationId, true);

            // tracks with only one animation and no cycling don't need a timer at all
            if (track.Cycle && track.CycleTime > 0.0f && track.Animations.Count > 1)
                _activeTrackTimers.Add(NewTrackTimer(state.Num, index, track.CycleTime));
        }

        _activeTrackState = state;
        AnimationChanged?.Invoke();
    }

    private void StopActiveTracks()
    {
        _activeTrackTimers.Clear();

        if (_activeTrackState is null)
            return;

        foreach (var track in _activeTrackState.Tracks)
        {
            if (track.CurrentAnimationId != Guid.Empty)
            {
                Send(track.CurrentAnimationId, false);
                track.CurrentAnimationId = Guid.Empty;
            }
        }

        _activeTrackState = null;
    }

    private void StartAlwaysAnimations()
    {
        var state = _enabled && _currentSet is not null ? _currentSet.GetState(AoStateId.AlwaysPlay) : null;

        if (state is not null && state == _activeAlwaysState)
            return;

        StopAlwaysAnimations();

        if (state is null || (state.Steps.Count == 0 && state.Tracks.Count == 0))
            return;

        if (state.Steps.Count > 0)
        {
            var animations = _currentSet!.GetAnimationsForState(state, _inventory, _random);
            state.CurrentAnimationIds = animations;
            foreach (Guid animation in animations)
                Send(animation, true);
        }

        foreach (var track in state.Tracks)
        {
            if (track.Animations.Count == 0)
                continue;

            track.CurrentAnimationId = _currentSet!.GetAnimationForTrack(track, _inventory, _random);
            if (track.CurrentAnimationId != Guid.Empty)
                Send(track.CurrentAnimationId, true);
        }

        _activeAlwaysState = state;
        RefreshAlwaysTimers();
        AnimationChanged?.Invoke();
    }

    private void StopAlwaysAnimations()
    {
        _alwaysTimers.Clear();

        if (_activeAlwaysState is null)
            return;

        StopCurrentAnimations(_activeAlwaysState);

        foreach (var track in _activeAlwaysState.Tracks)
        {
            if (track.CurrentAnimationId != Guid.Empty)
            {
                Send(track.CurrentAnimationId, false);
                track.CurrentAnimationId = Guid.Empty;
            }
        }

        _activeAlwaysState = null;
    }

    private void RefreshAlwaysTimers()
    {
        _alwaysTimers.Clear();

        var state = _activeAlwaysState;
        if (state is null)
            return;

        if (state.Cycle && state.CycleTime > 0.0f && state.Steps.Count > 1)
            _alwaysTimers.Add(NewTrackTimer(state.Num, TrackIndexSteps, state.CycleTime));

        for (int index = 0; index < state.Tracks.Count; index++)
        {
            var track = state.Tracks[index];
            if (track.Cycle && track.CycleTime > 0.0f && track.Animations.Count > 1)
                _alwaysTimers.Add(NewTrackTimer(state.Num, index, track.CycleTime));
        }
    }

    private void CycleAlwaysStep()
    {
        var state = _activeAlwaysState;
        if (!_enabled || _currentSet is null || state is null || state.Steps.Count == 0)
            return;

        var animations = _currentSet.GetAnimationsForState(state, _inventory, _random);

        if (SwapStateAnimations(state, animations))
            AnimationChanged?.Invoke();
    }

    /// <summary>Asks again for the "Always" animations: all of them, or only those the region no longer lists.</summary>
    public void ReassertAlways(bool onlyIfMissing)
    {
        if (!_enabled || _activeAlwaysState is null)
            return;

        bool NeedsRestart(Guid id) => id != Guid.Empty && (!onlyIfMissing || !_agent.IsSignalled(id));

        foreach (Guid animation in _activeAlwaysState.CurrentAnimationIds)
        {
            if (NeedsRestart(animation))
                Send(animation, true);
        }

        foreach (var track in _activeAlwaysState.Tracks)
        {
            if (NeedsRestart(track.CurrentAnimationId))
                Send(track.CurrentAnimationId, true);
        }
    }

    private void TrackTimeout(AoStateId stateNum, int trackIndex)
    {
        if (!_enabled || _currentSet is null)
            return;

        AoState? state = null;
        if (_activeTrackState is not null && _activeTrackState.Num == stateNum)
            state = _activeTrackState;
        else if (_activeAlwaysState is not null && _activeAlwaysState.Num == stateNum)
            state = _activeAlwaysState;

        if (state is null)
            return;

        if (trackIndex == TrackIndexSteps)
        {
            if (state == _activeAlwaysState)
                CycleAlwaysStep();
            return;
        }

        if (trackIndex < 0 || trackIndex >= state.Tracks.Count)
            return;

        var track = state.Tracks[trackIndex];

        Guid oldAnimation = track.CurrentAnimationId;
        Guid animation = _currentSet.GetAnimationForTrack(track, _inventory, _random);

        if (animation == oldAnimation)
            return;

        track.CurrentAnimationId = animation;
        Send(animation, true);
        Send(oldAnimation, false);

        AnimationChanged?.Invoke();
    }

    // ---------------- the state the avatar is in ----------------

    private void SetLastMotion(Guid motion)
    {
        if (motion != AoAnims.Type)
            _lastMotion = motion;
    }

    private void SetLastOverriddenMotion(Guid motion)
    {
        if (motion != AoAnims.Type)
            _lastOverriddenMotion = motion;
    }

    private bool ForeignAnimations()
    {
        // checking foreign animations only makes sense when smart sit is enabled
        if (_currentSet is null || !_currentSet.Smart)
            return false;

        if (!_agent.SittingOnObject)
            return false;

        return _agent.ForeignAnimationOnSeat;
    }

    // map motion to underwater state, null if not applicable
    private AoState? MapSwimming(Guid motion)
    {
        AoStateId stateNum;

        if (motion == AoAnims.Hover)
            stateNum = AoStateId.Floating;
        else if (motion == AoAnims.Fly)
            stateNum = AoStateId.SwimmingForward;
        else if (motion == AoAnims.HoverUp)
            stateNum = AoStateId.SwimmingUp;
        else if (motion == AoAnims.HoverDown)
            stateNum = AoStateId.SwimmingDown;
        else
            return null;   // motion not applicable for underwater mapping

        return _currentSet?.GetState(stateNum);
    }

    /// <summary>Switches between swimming and flying when the avatar goes under the region's water or comes out of it.</summary>
    public void CheckBelowWater(bool underwater)
    {
        // there was no transition, do nothing
        if (_underWater == underwater)
            return;

        // only applies to motions that actually change underwater and have animations inside
        var mapped = MapSwimming(_lastMotion);
        if (mapped is null || mapped.Steps.Count == 0)
        {
            // set underwater status but do nothing else
            _underWater = underwater;
            return;
        }

        // find animation id to stop when transitioning
        Guid id = Override(_lastMotion, false);
        if (id == Guid.Empty)
            id = _lastMotion;   // no animation in overrider for this state, use the default motion

        // stop currently running animation
        Send(id, false);

        if (!_underWater)
        {
            // remember which animation we stopped while going under water, to catch the stop request later in the
            // overrider: this prevents the overrider from triggering itself after the region comes back with the stop
            // request for the default motion ids
            _transitionId = id;
        }

        // set requested underwater status for overrider
        _underWater = underwater;

        // find animation id to start when transitioning
        id = Override(_lastMotion, true);
        if (id == Guid.Empty)
            id = _lastMotion;   // no animation in overrider for this state, use the default motion

        // start new animation
        Send(id, true);
    }

    // find the correct animation state for the requested motion, mapping flying to swimming where necessary
    private AoState? GetStateForMotion(Guid motion)
    {
        // get default state for this motion
        var defaultState = _currentSet!.GetStateByRemapId(motion);
        if (!_underWater)
            return defaultState;

        // get state for underwater motion
        var mapped = MapSwimming(motion);
        if (mapped is null)
            return defaultState;   // not applicable for underwater motion, so use default state

        // check if the underwater state has any animations to play
        if (mapped.Steps.Count == 0)
            return defaultState;   // no animations in underwater state, return default
        return mapped;
    }

    private void EnableStands(bool enableStands)
    {
        _enabledStands = enableStands;
        // let the main enable routine decide if we need to change animations
        // but don't actually change the state of the enabled flag
        Enable(_enabled);
    }

    private void Enable(bool enable)
    {
        _enabled = enable;

        if (_currentSet is null)
            return;

        var state = _currentSet.GetStateByRemapId(_lastMotion);
        if (_enabled)
        {
            StartAlwaysAnimations();
            if (state is not null && state.Steps.Count > 0)
            {
                // do not stop underlying sit animations when re-enabling the AO
                if (_lastOverriddenMotion != AoAnims.SitGround &&
                    _lastOverriddenMotion != AoAnims.SitGroundConstrained &&
                    _lastOverriddenMotion != AoAnims.Sit)
                {
                    Send(_lastOverriddenMotion, false);
                }

                Guid animation = Override(_lastMotion, true);
                if (animation == Guid.Empty)
                    return;

                if (_lastMotion == AoAnims.Stand)
                {
                    if (!_enabledStands)
                        return;
                    StopAllStandVariants();
                }
                else if (_lastMotion == AoAnims.Walk)
                {
                    Send(AoAnims.WalkNew, false);
                    Send(AoAnims.FemaleWalk, false);
                    Send(AoAnims.FemaleWalkNew, false);
                }
                else if (_lastMotion == AoAnims.Run)
                {
                    Send(AoAnims.RunNew, false);
                    Send(AoAnims.FemaleRunNew, false);
                }
                else if (_lastMotion == AoAnims.Sit)
                {
                    StopAllSitVariants();
                    Send(AoAnims.SitGeneric, true);
                }

                Send(animation, true);
                AnimationChanged?.Invoke();

                // remember to ignore this motion once in the overrider so stopping the default motion
                // will not trigger a stop of the override animation
                _ignoreMotionStopOnce = _lastMotion;
            }
        }
        else
        {
            if (_lastOverriddenMotion == AoAnims.Sit)
            {
                // remove sit cycle cover up
                Send(AoAnims.SitGeneric, false);
            }

            StopActiveTracks();
            StopAlwaysAnimations();

            // stop all overriders, catch leftovers
            for (int index = 0; index < AoSet.StateCount; index++)
            {
                var leftover = _currentSet.GetState((AoStateId)index);
                if (leftover.CurrentAnimationIds.Count > 0)
                    StopCurrentAnimations(leftover);
            }

            AnimationChanged?.Invoke();

            // restore the default animation if applicable
            if (_lastOverriddenMotion != AoAnims.Sit || !ForeignAnimations())
                Send(_lastMotion, true);

            _currentSet.Timer.Running = false;
        }
    }

    private void SetStateCycleTimer(AoState state)
    {
        float timeout = state.CycleTime;
        if (timeout > 0.0f)
            _currentSet!.Timer.Start(timeout, _clock());
    }

    private static readonly ImmutableArray<AoStateId> CleanupStates =
    [
        AoStateId.Standing, AoStateId.Walking, AoStateId.Running, AoStateId.Sitting, AoStateId.SittingOnGround, AoStateId.Crouching,
        AoStateId.CrouchWalking, AoStateId.Falling, AoStateId.FlyingDown, AoStateId.FlyingUp, AoStateId.Flying, AoStateId.FlyingSlow,
        AoStateId.Hovering, AoStateId.Jumping, AoStateId.TurningRight, AoStateId.TurningLeft, AoStateId.Floating,
        AoStateId.SwimmingForward, AoStateId.SwimmingUp, AoStateId.SwimmingDown,
    ];

    // the animations the viewer needs the default of, to know the agent's state: the set's animation plays beside it
    private static bool IsTransient(Guid motion) =>
        motion == AoAnims.SitGround || motion == AoAnims.SitGroundConstrained || motion == AoAnims.PreJump ||
        motion == AoAnims.StandUp || motion == AoAnims.Land || motion == AoAnims.MediumLand;

    /// <summary>
    /// The user's own avatar starts (or stops) a default animation: answers the animation to start (or stop) in its
    /// place, or empty when the default is to play. Some requests are sent from here, as the source does.
    /// </summary>
    public Guid Override(Guid motion, bool start)
    {
        if (!_enabled)
        {
            if (start && _currentSet is not null)
            {
                var known = _currentSet.GetStateByRemapId(motion);
                if (known is not null)
                {
                    SetLastMotion(motion);
                    if (known.Steps.Count > 0)
                        SetLastOverriddenMotion(motion);
                }
            }
            return Guid.Empty;
        }

        if (_sets.Count == 0)
            return Guid.Empty;

        if (_currentSet is null)
            return Guid.Empty;

        // ignore stopping this motion once so we can stop the default animation
        // without killing our overrider when logging in or re-enabling
        if (!start && motion == _ignoreMotionStopOnce)
        {
            _ignoreMotionStopOnce = Guid.Empty;

            // when stopping a sit motion make sure to stop the cycle point cover-up animation
            if (motion == AoAnims.Sit)
                Send(AoAnims.SitGeneric, false);

            return Guid.Empty;
        }

        // map the requested motion to an animation state, taking underwater
        // swimming into account where applicable
        var state = GetStateForMotion(motion);
        if (state is null)
            return Guid.Empty;

        AnimationChanged?.Invoke();

        // clean up stray animations as an additional safety measure
        // unless current motion is the typing one, which is a special
        // case, as it plays at the same time as other motions
        if (motion != AoAnims.Type)
        {
            foreach (var stateNum in CleanupStates)
            {
                // check if the next state is the one we are currently animating and skip that
                var stateToCheck = _currentSet.GetState(stateNum);
                if (stateToCheck != state)
                {
                    // check if there are animations left over for that state
                    if (stateToCheck.CurrentAnimationIds.Count > 0)
                    {
                        // stop the leftover animations in the region for everyone
                        StopCurrentAnimations(stateToCheck);
                    }
                }
            }
        }

        Guid animation;

        _currentSet.Timer.Running = false;
        if (start)
        {
            if (motion != AoAnims.Type)
                StopActiveTracks();

            SetLastMotion(motion);

            // Disable start stands in Mouselook
            if (_currentSet.MouselookStandDisable && motion == AoAnims.Stand && _inMouselook)
                return Guid.Empty;

            // Don't override start and turning stands if stand override is disabled
            if (!_enabledStands && (motion == AoAnims.Stand || motion == AoAnims.TurnRight || motion == AoAnims.TurnLeft))
                return Guid.Empty;

            // Do not start override sits if not selected
            if (!_currentSet.SitOverride && motion == AoAnims.Sit)
                return Guid.Empty;

            // scripted seats that use ground_sit as animation need special treatment
            if (motion == AoAnims.SitGround || motion == AoAnims.SitGroundConstrained)
            {
                if (_agent.SittingOnObject)
                    return Guid.Empty;
            }

            if (state.Steps.Count > 0)
                SetLastOverriddenMotion(motion);

            // do not remember typing as set-wide motion
            if (motion != AoAnims.Type)
                _currentSet.Motion = motion;

            var animations = _currentSet.GetAnimationsForState(state, _inventory, _random);

            // previous animations for this state were not stopped, but we were asked to start a new one: stop the old ones
            if (state.CurrentAnimationIds.Count > 0)
                StopCurrentAnimations(state);

            state.CurrentAnimationIds = animations;
            animation = animations.Count == 0 ? Guid.Empty : animations[0];

            if (animation != Guid.Empty)
                AnimationChanged?.Invoke();

            SetStateCycleTimer(state);

            if (motion == AoAnims.Sit)
            {
                // Use the generic sit, so we don't create an overrider loop with the default sit
                // while still having a base sitting pose to cover up cycle points
                Send(AoAnims.SitGeneric, true);
                if (_currentSet.Smart)
                    SitCancelOneShot();
            }
            // special treatment for "transient animations" because the viewer needs the default animation to know the agent's state
            else if (IsTransient(motion))
            {
                foreach (Guid animationId in animations)
                    Send(animationId, true);
                return Guid.Empty;
            }

            for (int index = 1; index < animations.Count; index++)
                Send(animations[index], true);
            if (motion != AoAnims.Type)
                StartStateTracks(state);
        }
        else
        {
            // check for previously remembered transition motion from/to underwater movement and don't
            // allow the overrider to stop it, or it will cancel the transition to underwater motion
            // immediately after starting it
            if (motion == _transitionId)
            {
                // clear transition motion id and return nothing to allow the default animation system to take over
                _transitionId = Guid.Empty;
                return Guid.Empty;
            }

            // clear transition motion id here as well, to make sure there is no stray id left behind
            _transitionId = Guid.Empty;

            animation = state.CurrentAnimationIds.Count == 0 ? Guid.Empty : state.CurrentAnimationIds[0];

            // for the typing animation, just return the stored animation, reset the state timer, and don't memorize anything else
            if (motion == AoAnims.Type)
            {
                StopCurrentAnimations(state, true);
                AnimationChanged?.Invoke();
                var previousState = _currentSet.GetStateByRemapId(_lastMotion);
                if (previousState is not null)
                    SetStateCycleTimer(previousState);
                return animation;
            }

            // stop the underlying default motion, in case it's still running.
            // frequently happens with sits, so we keep it only for those currently.
            if (motion == AoAnims.Sit)
                StopAllSitVariants();

            if (motion != _currentSet.Motion)
            {
                // a stop for a motion the set is not in
                StopCurrentAnimations(state, true);
                AnimationChanged?.Invoke();
                return animation;
            }

            _currentSet.Motion = Guid.Empty;
            StopActiveTracks();

            // again, special treatment for "transient" animations to make sure our own animation gets stopped properly
            if (IsTransient(motion))
            {
                StopCurrentAnimations(state);
                AnimationChanged?.Invoke();
                SetStateCycleTimer(state);
                return Guid.Empty;
            }

            StopCurrentAnimations(state, true);
            AnimationChanged?.Invoke();
        }

        return animation;
    }

    /// <summary>The avatar starts a default animation: when the set has one for it, the region is asked to start that,
    /// and the answer is true (the default is not to be played on the user's own screen).</summary>
    public bool MotionStarted(Guid motion)
    {
        Guid remapId = Override(motion, true);
        if (remapId == Guid.Empty)
            return false;
        Send(remapId, true);
        return true;
    }

    /// <summary>The avatar stops a default animation: when the set played one for it, the region is asked to stop that.</summary>
    public bool MotionStopped(Guid motion)
    {
        Guid remapId = Override(motion, false);
        if (remapId == Guid.Empty)
            return false;
        Send(remapId, false);
        return true;
    }

    /// <summary>
    /// The region's list of what the user's own avatar plays, each animation with its sequence number: the ones no
    /// longer listed stop, then the ones newly listed (or listed with another sequence number) start.
    /// </summary>
    public void SignalledChanged(IReadOnlyList<(Guid Id, int Sequence)> now)
    {
        var next = new Dictionary<Guid, int>();
        foreach (var (id, sequence) in now)
            next[id] = sequence;

        foreach (Guid id in _signalled.Keys.ToArray())
        {
            if (!next.ContainsKey(id))
                MotionStopped(id);
        }

        foreach (var (id, sequence) in now)
        {
            if (!_signalled.TryGetValue(id, out int old) || old != sequence)
                MotionStarted(id);
        }

        _signalled = next;

        // keep the "Always" animations alive
        ReassertAlways(true);
    }

    /// <summary>Whether the set's animation plays in place of this default animation now (not beside it).</summary>
    public bool IsOverridden(Guid motion)
    {
        if (!_enabled || _currentSet is null || motion == AoAnims.Type || IsTransient(motion))
            return false;
        return GetStateForMotion(motion) is { CurrentAnimationIds.Count: > 0 };
    }

    private void CheckSitCancel()
    {
        if (!ForeignAnimations())
            return;

        var aoState = _currentSet!.GetStateByRemapId(AoAnims.Sit);
        if (aoState is not null && aoState.CurrentAnimationIds.Count > 0)
        {
            StopCurrentAnimations(aoState);
            // remove cycle point cover-up
            Send(AoAnims.SitGeneric, false);
            _sitCancelTimer.Running = false;
            // stop cycle timer
            _currentSet.Timer.Running = false;
            // sitting tracks make no sense when the sit override backs off
            StopActiveTracks();
            AnimationChanged?.Invoke();
        }
    }

    private void CycleTimeout(AoSet set)
    {
        if (!_enabled)
            return;

        if (set != _currentSet)
            return;

        Cycle(AoCycleMode.Any);
    }

    /// <summary>Moves the state the avatar is in to another step: the next one the state's own rule gives (the timer),
    /// or the next or previous one by hand.</summary>
    public void Cycle(AoCycleMode cycleMode, bool resetTimer = false)
    {
        if (!_enabled)
            return;

        if (_currentSet is null)
            return;

        // do not cycle if we're sitting and sit-override is off
        if (_lastMotion == AoAnims.Sit && !_currentSet.SitOverride)
            return;
        // do not cycle if we're standing and mouselook stand override is disabled while being in mouselook
        else if (_lastMotion == AoAnims.Stand && _currentSet.MouselookStandDisable && _inMouselook)
            return;

        var state = _currentSet.GetStateByRemapId(_lastMotion);
        if (state is null)
            return;

        if (state.Steps.Count == 0)
            return;

        // make sure we disable cycling only for timed cycle, so manual cycling still works, even with cycling switched off
        if (!state.Cycle && cycleMode == AoCycleMode.Any)
            return;

        var oldAnimations = state.CurrentAnimationIds;
        List<Guid> animations;

        if (cycleMode == AoCycleMode.Any)
        {
            animations = _currentSet.GetAnimationsForState(state, _inventory, _random);
        }
        else
        {
            if (cycleMode == AoCycleMode.Previous)
            {
                if (state.CurrentAnimation == 0)
                    state.CurrentAnimation = state.Steps.Count - 1;
                else
                    state.CurrentAnimation--;
            }
            else if (cycleMode == AoCycleMode.Next)
            {
                state.CurrentAnimation++;
                if (state.CurrentAnimation >= state.Steps.Count)
                    state.CurrentAnimation = 0;
            }

            animations = [];
            foreach (var anim in state.Steps[state.CurrentAnimation].Members)
            {
                Guid assetId = AoSet.ResolveAsset(anim, _inventory);
                if (assetId != Guid.Empty)
                    animations.Add(assetId);
            }
        }

        // don't do anything if the animation didn't change
        if (animations.SequenceEqual(oldAnimations))
            return;

        state.CurrentAnimationIds = animations;
        AnimationChanged?.Invoke();

        foreach (Guid animation in animations)
        {
            // don't restart animations that play in both the old and the new step
            if (!oldAnimations.Contains(animation))
                Send(animation, true);
        }

        foreach (Guid oldAnimation in oldAnimations)
        {
            if (!animations.Contains(oldAnimation))
                Send(oldAnimation, false);
        }

        if (resetTimer)
            _currentSet.Timer.Since = _clock();
    }

    // ---------------- reading the sets ----------------

    // the names in a folder name: the name itself first, then its options, separated by colons
    private static string[] Tokens(string name) => name.Split(':', StringSplitOptions.RemoveEmptyEntries);

    private bool ReadAnimationLinks(Guid categoryId, List<AoAnimation> animations)
    {
        if (!_inventory.IsComplete(categoryId))
        {
            _inventory.Fetch(categoryId);
            return false;
        }

        foreach (var item in _inventory.Items(categoryId))
        {
            // the source moves anything that is not a link to an animation out of the folder; here it is passed over
            if (!item.IsLink || !item.IsAnimation)
                continue;

            var anim = new AoAnimation
            {
                Name = item.Name,
                InventoryId = item.Id,
                OriginalId = item.LinkedId,
                // if the original animation is known already, save its asset id, otherwise this is tried again when the
                // animation is to play
                AssetId = item.AssetId,
            };

            if (!int.TryParse(item.Description, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sortOrder))
                sortOrder = -1;
            anim.SortOrder = sortOrder;

            if (sortOrder == -1)
            {
                // sort order was unknown so append to the end of the list
                animations.Add(anim);
            }
            else
            {
                bool inserted = false;
                for (int index = 0; index < animations.Count; index++)
                {
                    if (animations[index].SortOrder > sortOrder)
                    {
                        animations.Insert(index, anim);
                        inserted = true;
                        break;
                    }
                }
                if (!inserted)
                    animations.Add(anim);
            }
        }

        return true;
    }

    private static void InsertStep(AoState state, AoStep step)
    {
        if (step.SortOrder == -1)
        {
            // step sort order was unknown so append to the end of the list
            state.Steps.Add(step);
            return;
        }

        for (int index = 0; index < state.Steps.Count; index++)
        {
            if (state.Steps[index].SortOrder > step.SortOrder)
            {
                state.Steps.Insert(index, step);
                return;
            }
        }
        state.Steps.Add(step);
    }

    private bool ReloadStateAnimations(AoState state)
    {
        bool allComplete = true;
        state.Steps.Clear();
        state.Tracks.Clear();

        var directLinks = new List<AoAnimation>();
        if (!ReadAnimationLinks(state.InventoryId, directLinks))
            return false;

        foreach (var anim in directLinks)
        {
            var step = new AoStep { Name = anim.Name, InventoryId = anim.InventoryId, SortOrder = anim.SortOrder, IsGroup = false };
            step.Members.Add(anim);
            InsertStep(state, step);
        }

        foreach (var cat in _inventory.Folders(state.InventoryId))
        {
            string[] parameters = Tokens(cat.Name);
            if (parameters.Length == 0)
                continue;

            bool isTrack = false;
            for (int num = 1; num < parameters.Length; num++)
            {
                if (parameters[num] == "TR")
                {
                    isTrack = true;
                    break;
                }
            }

            if (isTrack)
            {
                var track = new AoTrack { Name = parameters[0], InventoryId = cat.Id };

                for (int num = 1; num < parameters.Length; num++)
                {
                    if (parameters[num] == "TR")
                        continue;
                    else if (parameters[num] == "CY")
                        track.Cycle = true;
                    else if (parameters[num] == "RN")
                        track.Random = true;
                    else if (parameters[num].StartsWith("CT", StringComparison.Ordinal))
                        ToFloat(parameters[num][2..], ref track.CycleTime);
                }

                if (!ReadAnimationLinks(track.InventoryId, track.Animations))
                {
                    allComplete = false;
                    continue;
                }

                state.Tracks.Add(track);
            }
            else
            {
                var step = new AoStep { Name = parameters[0], InventoryId = cat.Id, IsGroup = true, SortOrder = -1 };

                for (int num = 1; num < parameters.Length; num++)
                {
                    if (parameters[num].StartsWith("OR", StringComparison.Ordinal) &&
                        int.TryParse(parameters[num][2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sortOrder))
                    {
                        step.SortOrder = sortOrder;
                    }
                }

                if (!ReadAnimationLinks(step.InventoryId, step.Members))
                {
                    allComplete = false;
                    continue;
                }

                // an empty group subfolder is ignored
                if (step.Members.Count == 0)
                    continue;

                InsertStep(state, step);
            }
        }

        return allComplete;
    }

    private static void ToFloat(string text, ref float value)
    {
        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
            value = parsed;
    }

    private AoSet? GetSetByName(string name)
    {
        foreach (var set in _sets)
        {
            if (set.Name == name)
                return set;
        }
        return null;
    }

    // looks for the folder the sets are kept in
    private void PollInventory()
    {
        Guid root = _inventory.RootId;
        if (root == Guid.Empty)
            return;

        Guid categoryId = Guid.Empty;
        foreach (var cat in _inventory.Folders(root))
        {
            if (cat.Name == RootFirestormFolder)
            {
                categoryId = cat.Id;
                break;
            }
        }

        if (categoryId == Guid.Empty)
        {
            // the source makes the folder here; this viewer only reads
            _aoFolder = Guid.Empty;
            return;
        }

        foreach (var cat in _inventory.Folders(categoryId))
        {
            if (string.CompareOrdinal(cat.Name, RootAoFolder) == 0)
            {
                _aoFolder = cat.Id;
                break;
            }
        }

        if (_aoFolder != Guid.Empty)
            Update();
    }

    private void Update()
    {
        if (_aoFolder == Guid.Empty)
            return;

        if (!_inventory.IsComplete(_aoFolder))
        {
            // the folder hasn't fully fetched yet, try again next timer tick
            _inventory.Fetch(_aoFolder);
            return;
        }

        bool allComplete = true;

        var setsToWrite = new List<(Guid CategoryId, AoSet Set)>();
        foreach (var currentCategory in _inventory.Folders(_aoFolder))
        {
            string setFolderName = currentCategory.Name;
            if (setFolderName.Length == 0)
                continue;

            string[] parameters = Tokens(setFolderName);
            if (parameters.Length == 0)
                continue;

            string setName = parameters[0];
            var newSet = GetSetByName(setName);
            if (newSet is null)
            {
                newSet = new AoSet(currentCategory.Id) { Name = setName };
                _sets.Add(newSet);
                setsToWrite.Add((currentCategory.Id, newSet));
            }
            else
            {
                if (newSet.Complete)
                    continue;
                setsToWrite.Add((currentCategory.Id, newSet));
            }
            allComplete = false;

            for (int num = 1; num < parameters.Length; num++)
            {
                if (parameters[num].Length != 2)
                    continue;   // unknown option
                else if (parameters[num] == "SO")
                    newSet.SitOverride = true;
                else if (parameters[num] == "SM")
                    newSet.Smart = true;
                else if (parameters[num] == "DM")
                    newSet.MouselookStandDisable = true;
                else if (parameters[num] == "**")
                {
                    _defaultSet = newSet;
                    _currentSet = newSet;
                }
            }
        }

        foreach (var (categoryId, newSet) in setsToWrite)
        {
            if (_defaultSet is null)
            {
                // No default is set, so instead use the set used last
                if (SavedSetName is not null && SavedSetName == newSet.Name)
                    _currentSet = newSet;
            }

            if (_inventory.IsComplete(categoryId))
            {
                newSet.Complete = true;

                foreach (var stateCategory in _inventory.Folders(categoryId))
                {
                    string[] stateParameters = Tokens(stateCategory.Name);
                    if (stateParameters.Length == 0)
                        continue;
                    string stateName = stateParameters[0];

                    var state = newSet.GetStateByName(stateName);
                    if (state is null)
                        continue;   // unknown state

                    state.InventoryId = stateCategory.Id;
                    for (int num = 1; num < stateParameters.Length; num++)
                    {
                        if (stateParameters[num] == "CY")
                            state.Cycle = true;
                        else if (stateParameters[num] == "RN")
                            state.Random = true;
                        else if (stateParameters[num].StartsWith("CT", StringComparison.Ordinal))
                            ToFloat(stateParameters[num][2..], ref state.CycleTime);
                    }

                    if (!_inventory.IsComplete(state.InventoryId))
                    {
                        _inventory.Fetch(state.InventoryId);
                        allComplete = false;
                        newSet.Complete = false;
                        continue;
                    }
                    if (!ReloadStateAnimations(state))
                    {
                        // the state has incomplete subfolders
                        allComplete = false;
                        newSet.Complete = false;
                    }
                }
            }
            else
            {
                _inventory.Fetch(categoryId);
            }
        }

        if (allComplete)
        {
            _enabled = UseAo;

            if (_currentSet is null && _sets.Count > 0)
                SelectSet(_sets[0]);   // no set currently selected, choosing the first in the list

            _inventoryTimer = false;
            Loaded = true;

            Updated?.Invoke();
            Enable(_enabled);
        }
    }

    /// <summary>Forgets the sets and reads them from the inventory again.</summary>
    public void Reload()
    {
        bool wasEnabled = _enabled;

        if (wasEnabled)
            Enable(false);

        _lastOverriddenMotion = AoAnims.Stand;

        StopActiveTracks();
        StopAlwaysAnimations();
        _sets.Clear();
        _currentSet = null;
        _defaultSet = null;
        Loaded = false;

        _aoFolder = Guid.Empty;
        _inventoryTimer = true;
        _lastPoll = _clock();
        if (_started)
            PollInventory();

        if (wasEnabled)
            Enable(true);
    }

    // ---------------- choosing a set ----------------

    public void SelectSet(AoSet set)
    {
        if (_enabled && _currentSet is not null)
        {
            StopActiveTracks();
            StopAlwaysAnimations();
            var state = _currentSet.GetStateByRemapId(_lastOverriddenMotion);
            if (state is not null)
            {
                StopCurrentAnimations(state);
                _currentSet.Timer.Running = false;
            }
        }

        _currentSet = set;

        if (_enabled)
        {
            StartAlwaysAnimations();
            Send(Override(_lastMotion, true), true);
        }
    }

    public AoSet? SelectSetByName(string name)
    {
        var set = GetSetByName(name);
        if (set is null)
            return null;
        SelectSet(set);
        return set;
    }

    // ---------------- mouse-look and region changes ----------------

    public void InMouselook(bool mouselook)
    {
        if (_inMouselook == mouselook)
            return;

        _inMouselook = mouselook;

        if (_currentSet is null)
            return;

        if (!_currentSet.MouselookStandDisable)
            return;

        if (!_enabled)
            return;

        if (_lastMotion != AoAnims.Stand)
            return;

        if (mouselook)
        {
            var state = _currentSet.GetState(AoStateId.Standing);
            if (state.CurrentAnimationIds.Count > 0)
                StopCurrentAnimations(state);
            StopActiveTracks();
            Send(AoAnims.Stand, true);
        }
        else
        {
            StopAllStandVariants();
            Send(Override(AoAnims.Stand, true), true);
        }
    }

    /// <summary>The avatar is in another region: the region is asked again for what the avatar plays.</summary>
    public void RegionChanged()
    {
        // do nothing if the AO is off
        if (!_enabled)
            return;

        if (_currentSet is null)
            return;

        ReassertAlways(false);

        // sitting needs special attention
        if (_lastMotion == AoAnims.Sit)
        {
            // do nothing if sit overrides was disabled
            if (!_currentSet.SitOverride)
                return;

            // do nothing if the last overridden motion wasn't a sit.
            // happens when sit override is enabled but there were no
            // sit animations added to the set yet
            if (_lastOverriddenMotion != AoAnims.Sit)
                return;

            var state = _currentSet.GetState(AoStateId.Sitting);

            // do nothing if no AO animation is playing (e.g. smart sit cancel)
            if (state.CurrentAnimationIds.Count == 0)
                return;
        }

        // restart current animation on region crossing
        Send(_lastMotion, true);
    }
}
