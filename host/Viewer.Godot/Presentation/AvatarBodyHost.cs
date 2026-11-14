// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace Viewer.Godot.Presentation;

/// <summary>
/// The avatar body and skeleton every region's presenter shares, so an avatar that crosses from one region to the next is one drawn instance (one
/// skeleton, one animation state, its worn pieces bound to it) that the presenter of the region entered takes over, as the official viewer keeps one
/// avatar object whatever region it is in (llvoavatar.cpp, llviewerobject.cpp setRegion). A presenter given a host draws its avatars in the host's
/// layer; the avatar of an agent another presenter already draws is that presenter's drawn instance, shared, not made again. Main thread only.
/// </summary>
public sealed class AvatarBodyHost : IDisposable
{
    private readonly List<AvatarPresenter> _presenters = new();
    private readonly Dictionary<Guid, object> _shared = new();
    private AvatarLayer? _building;

    /// <param name="clock">Seconds; one clock for every presenter that shares the body (the poses are worked out from it). Default a stopwatch started here.</param>
    public AvatarBodyHost(Rid scenario, Task<AvatarPresenter.LoadedBody?>? body, Func<double>? clock = null)
    {
        Scenario = scenario;
        Body = body;
        if (clock is null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            clock = () => sw.Elapsed.TotalSeconds;
        }
        Clock = clock;
    }

    public Rid Scenario { get; }
    public Task<AvatarPresenter.LoadedBody?>? Body { get; }
    public Func<double> Clock { get; }

    /// <summary>The whole body's layer; null until it is built.</summary>
    public AvatarLayer? Layer { get; private set; }

    /// <summary>The joint textures worn rigged meshes skin by; made with the layer, shared by every region.</summary>
    public SkinTextures? Skin { get; private set; }

    /// <summary>How many times <see cref="Tick"/> has run.</summary>
    public int Ticks { get; private set; }

    /// <summary>The engine frame the last tick ran in.</summary>
    internal long LastEngineFrame { get; set; } = -1;

    internal void Join(AvatarPresenter p) => _presenters.Add(p);

    /// <summary>Where the middle of the body of <paramref name="agent"/> is drawn now and its heading, whichever region draws it (the name tag hangs from it). False when no body is drawn.</summary>
    public bool TryDrawnPose(Guid agent, out System.Numerics.Vector3 centre, out System.Numerics.Quaternion heading)
    {
        centre = default;
        heading = System.Numerics.Quaternion.Identity;
        foreach (var p in _presenters)
            if (p.TryDrawnPose(agent, out centre, out heading)) return true;
        return false;
    }
    /// <summary>Where the head joint of the body of <paramref name="agent"/> is drawn now, whichever region draws it. False when no body is drawn.</summary>
    public bool TryDrawnHead(Guid agent, out System.Numerics.Vector3 head)
    {
        head = default;
        foreach (var p in _presenters)
            if (p.TryDrawnHead(agent, out head)) return true;
        return false;
    }

    /// <summary>Where the lowest point of the seat of the body of <paramref name="agent"/> is drawn now (<see cref="AvatarPresenter.TryDrawnSeat"/>).</summary>
    public bool TryDrawnSeat(Guid agent, out System.Numerics.Vector3 seat)
    {
        seat = default;
        foreach (var p in _presenters)
            if (p.TryDrawnSeat(agent, out seat)) return true;
        return false;
    }

    /// <summary>Where the hand, foot and lower-leg volumes of the body of <paramref name="agent"/> are drawn now (<see cref="AvatarPresenter.DrawnLimbs"/>); null when no body is drawn.</summary>
    public SortedDictionary<string, float[]>? DrawnLimbs(Guid agent)
    {
        foreach (var p in _presenters)
            if (p.DrawnLimbs(agent) is { } limbs) return limbs;
        return null;
    }

    /// <summary>What the body of <paramref name="agent"/> is drawn with now (<see cref="AvatarPresenter.ShapeDump"/>); null when no body is drawn.</summary>
    /// <summary>Draws <paramref name="agent"/>'s body as a mask, or as before with null (<see cref="AvatarPresenter.SetMask"/>).</summary>
    public bool SetMask(Guid agent, Material? mask, bool hiddenToo, out System.Numerics.Matrix4x4 headWorld)
    {
        headWorld = default;
        foreach (var p in _presenters)
            if (p.SetMask(agent, mask, hiddenToo, out headWorld)) return true;
        return false;
    }

    /// <summary>The expression trace of <paramref name="agent"/>'s face (<see cref="AvatarPresenter.ExpressionTrace"/>); null when no body is drawn.</summary>
    public object? ExpressionTrace(Guid agent, bool reset)
    {
        foreach (var p in _presenters)
            if (p.ExpressionTrace(agent, reset) is { } trace) return trace;
        return null;
    }

    public object? ShapeDump(Guid agent)
    {
        foreach (var p in _presenters)
            if (p.ShapeDump(agent) is { } dump) return dump;
        return null;
    }

    /// <summary>The rigged meshes <paramref name="agent"/> wears and what they do to the skeleton (<see cref="AvatarPresenter.WornDump"/>); null when no body is drawn.</summary>
    public object? WornDump(Guid agent)
    {
        foreach (var p in _presenters)
            if (p.WornDump(agent) is { } dump) return dump;
        return null;
    }

    internal void Leave(AvatarPresenter p) => _presenters.Remove(p);

    /// <summary>The drawn instance of <paramref name="agent"/> some presenter has made (null: none).</summary>
    internal object? Find(Guid agent) => _shared.GetValueOrDefault(agent);

    internal void Share(Guid agent, object drawn) => _shared[agent] = drawn;

    internal void Unshare(Guid agent, object drawn)
    {
        if (_shared.TryGetValue(agent, out var d) && ReferenceEquals(d, drawn)) _shared.Remove(agent);
    }

    /// <summary>Builds the body's layer, a surface a call when <paramref name="defer"/>; true once it is whole.</summary>
    internal bool Build(bool defer)
    {
        if (Layer is not null) return true;
        if (Body is not { IsCompletedSuccessfully: true, Result: { } loaded }) return false;
        _building ??= new AvatarLayer(Scenario, loaded.Body, loaded.Surfaces,
            Skin ??= new SkinTextures(loaded.Body.Skeleton.Joints.Count + loaded.Body.Skeleton.AttachmentPoints.Count), deferSurfaces: defer);
        if (!_building.Step()) return false;
        Layer = _building;
        _building = null;
        return true;
    }

    /// <summary>
    /// One frame for every presenter that shares the body: first each presenter places and poses its avatars' state (the avatars it drives), then the layer
    /// poses them once, then the regions' prim layers read the skeleton. Every region reads the same pose, whichever presenter runs first.
    /// </summary>
    internal void Tick(AvatarPresenter trigger)
    {
        Ticks++;
        // every region's changes first, then the bodies placed: a region framed later would otherwise move an avatar after its body was placed
        Viewer.Shell.SharedBodyFrame.Run(_presenters.ToArray(), p => p.TakeChanges?.Invoke(), p => p.Drive(trigger.CurrentBudget));
        trigger.FrameLayer();
    }

    public void Dispose()
    {
        Layer?.Dispose();
        Layer = null;
        _building?.Dispose();
        _building = null;
        Skin?.Dispose();
        Skin = null;
        _presenters.Clear();
        _shared.Clear();
    }
}
