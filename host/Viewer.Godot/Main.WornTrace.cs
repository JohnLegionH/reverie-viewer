// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Core.Diagnostics;

namespace Viewer.Godot;

public partial class Main
{
    // NV_WORN_TRACE=1: once a second, for the user's own avatar and for every avatar handed to a region, where it is, how far from the last
    // crossing, and each worn piece of it (drawn, shown, held, level of detail and the distance that level used).
    private static readonly bool WornTraceOn = System.Environment.GetEnvironmentVariable("NV_WORN_TRACE") == "1";
    private readonly HashSet<Guid> _traceAgents = new();
    private double _traceNext;
    private System.Numerics.Vector3? _crossPoint;

    /// <summary>Per presenter: avatars in its scene / held / the scene's region visit / the world's region visit and avatars (a gap in a view is found by whose scene lacks the avatar).</summary>
    private string Scenes()
    {
        var parts = new List<string>();
        if (_presenter?.Avatars is { } a) parts.Add($"anchor={a.Scene.Count}/{a.Scene.Held.Count}/e{a.Scene.Epoch}/w{_anchorWorld?.Epoch}:{_anchorWorld?.Snapshot().Avatars.Count}");
        foreach (var st in _stacks.Values) parts.Add($"{st.Sim.Name}={st.Avatars.Scene.Count}/{st.Avatars.Scene.Held.Count}/e{st.Avatars.Scene.Epoch}/w{st.World.Epoch}:{st.World.Snapshot().Avatars.Count}");
        return string.Join(",", parts);
    }

    private readonly Presentation.WornFrameTrace.Recorder _wornFrames = new();

    /// <summary>NV_WORN_FRAMES=1: after every region's frame, which worn pieces of the user's own avatar are drawn by which region's layer (see <see cref="Presentation.WornFrameTrace"/>).</summary>
    private void WornFrameTick()
    {
        if (!Presentation.WornFrameTrace.On || _presented is null) return;
        var layers = new List<Presentation.PrimLayer>();
        if (_presenter?.Prims is { } own) layers.Add(own);
        layers.AddRange(_stacks.Values.Select(s => s.Prims));
        layers.AddRange(_retiring.Select(s => s.Prims));
        _wornFrames.Tick(_presented.AgentId, layers);
    }

    private void WornTraceNoteHandoff(Guid agent) { if (WornTraceOn) _traceAgents.Add(agent); }

    private void WornTraceTick()
    {
        if (!WornTraceOn || _presented is null) return;
        double now = Time.GetTicksMsec() / 1000.0;
        if (now < _traceNext) return;
        _traceNext = now + 1.0;
        var agents = new HashSet<Guid>(_traceAgents) { _presented.AgentId };
        var worlds = new List<Viewer.Core.World.WorldState>();
        if (_anchorWorld is not null) worlds.Add(_anchorWorld);
        worlds.AddRange(_stacks.Values.Select(s => s.World));
        foreach (var agent in agents)
        {
            var at = worlds.Select(w => w.AvatarOf(agent)).FirstOrDefault(a => a is not null);
            var pos = at?.Position;
            if (agent == _presented.AgentId && pos is { } p0 && _crossPoint is null) _crossPoint = p0;
            float fromCross = pos is { } p && _crossPoint is { } c ? System.Numerics.Vector3.Distance(p, c) : -1;
            var pieces = new List<string>();
            _presenter?.Prims?.WornReport(agent, pieces);
            foreach (var st in _stacks.Values) st.Prims.WornReport(agent, pieces);
            var wornDrawn = new HashSet<Guid>();
            _presenter?.Prims?.CollectWornDrawn(wornDrawn, agent);
            foreach (var st in _stacks.Values) st.Prims.CollectWornDrawn(wornDrawn, agent);
            bool avatar = (_presenter?.Avatars?.AgentDrawn(agent) ?? false) || _stacks.Values.Any(x => x.Avatars.AgentDrawn(agent));
            Log.Info("worn.second", CoreLog.F("agent", agent.ToString("N")[..8]), CoreLog.F("own", agent == _presented.AgentId ? 1 : 0), CoreLog.F("region", _presented.Region?.Name ?? ""),
                CoreLog.F("pos", pos is { } q ? $"{q.X:0},{q.Y:0},{q.Z:0}" : "none"), CoreLog.F("from_cross_m", fromCross.ToString("0")),
                CoreLog.F("avatar_drawn", avatar ? 1 : 0), CoreLog.F("body_off_m", BodyOffMetres(agent).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)), CoreLog.F("worn_drawn", wornDrawn.Count), CoreLog.F("scenes", Scenes()), CoreLog.F("pieces", string.Join(";", pieces)));
        }
    }
}

public partial class Main
{
    /// <summary>How far, in metres on the ground, the body drawn for <paramref name="agent"/> is from where the scene places the agent; -1 when no body is drawn or no
    /// region holds the agent live. The body must follow the agent in every frame (the official viewer's avatar drawable follows the agent's position every frame:
    /// llvoavatar.cpp idleUpdate 2675, llviewerobject.cpp interpolateLinearMotion 2484).</summary>
    private double BodyOffMetres(Guid agent)
    {
        var presenters = new List<Presentation.AvatarPresenter>();
        if (_presenter?.Avatars is { } own) presenters.Add(own);
        presenters.AddRange(_stacks.Values.Select(x => x.Avatars));
        foreach (var p in presenters)
            if (p.TryDrawnSoles(agent, out var drawn, out var wanted))
                return Math.Sqrt((drawn.X - wanted.X) * (drawn.X - wanted.X) + (drawn.Y - wanted.Y) * (drawn.Y - wanted.Y));
        return -1;
    }
}

public partial class Main
{
    private double _bodyWatchNext;
    private int _bodyOffSeconds;

    /// <summary>Once a second, in every build and without the trace: when the body drawn for the user's own avatar is more than a metre from where the scene places it
    /// for two seconds running, one warning says so (how far, and what each region holds), so a log shows an avatar that does not follow the agent.</summary>
    private void BodyOffWatch()
    {
        if (_presented is null) return;
        double now = Time.GetTicksMsec() / 1000.0;
        if (now < _bodyWatchNext) return;
        _bodyWatchNext = now + 1.0;
        double off = BodyOffMetres(_presented.AgentId);
        _bodyOffSeconds = off > 1.0 ? _bodyOffSeconds + 1 : 0;
        if (_bodyOffSeconds == 2 || (_bodyOffSeconds > 2 && _bodyOffSeconds % 10 == 0))
            Log.Warn("avatars.body_off", CoreLog.F("metres", off.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)), CoreLog.F("seconds", _bodyOffSeconds), CoreLog.F("region", _presented.Region?.Name ?? ""), CoreLog.F("scenes", Scenes()));
    }
}
