// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Godot;
using Viewer.Core.World;

namespace Viewer.Godot;

/// <summary>
/// NV_CROSSING_FRAMES=1 with NV_LOG_DIR set (development builds only): a development aid that writes one line for every frame from one second before each region crossing to one second
/// after it (<see cref="Viewer.Shell.CrossingFrameTrace"/>), under NV_LOG_DIR\crossing-frames. Each line: the engine frame, the time on the hand-over clock, the
/// agent's current region and the camera shift, the camera's eye and the point it follows, then per region drawn (its handle, offset and time dilation) the
/// user's own avatar as that region's scene has it (held, placing the body, its motion, its place and where the body was drawn), and every worn piece's drawn
/// origin per region layer. World changes for avatars and the region changes are noted as events with the time they were applied or seen. Off, or with no
/// log folder, nothing is built or written.
/// </summary>
public partial class Main
{
    private Viewer.Shell.CrossingFrameTrace? _crossFrames;
    private readonly ConcurrentQueue<(double At, string Text)> _crossEvents = new();
    private readonly HashSet<WorldState> _crossWatched = new(ReferenceEqualityComparer.Instance);

    private static double CrossClock() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;   // the hand-over ledger's clock

    private Viewer.Shell.CrossingFrameTrace CrossFrames => _crossFrames ??= new Viewer.Shell.CrossingFrameTrace(CrossFramesFolder(), Viewer.Shell.CrossingFrameTrace.SwitchedOn && OS.IsDebugBuild());

    /// <summary>The log folder variable's folder, by the frame burst's rule: a full path, never the viewer's own per-user folders; null otherwise.</summary>
    private static string? CrossFramesFolder()
    {
        string? dir = Viewer.Core.Diagnostics.FrameBurstTarget.Resolve(System.Environment.GetEnvironmentVariable("NV_LOG_DIR"),
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), out _);
        return dir is null ? null : Viewer.Core.Diagnostics.FrameBurstTarget.Resolve(dir, OS.GetUserDataDir(), out _);
    }

    private static string V3(System.Numerics.Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"{v.X:0.000},{v.Y:0.000},{v.Z:0.000}");

    /// <summary>A region change seen by the neighbour code (added, removed, crossed).</summary>
    private void CrossNote(string text)
    {
        if (CrossFrames.Active) CrossFrames.Note(CrossClock(), text);
    }

    private void CrossMark()
    {
        if (CrossFrames.Active) CrossFrames.Mark(CrossClock(), "crossing");
    }

    /// <summary>Watches a region's world for avatar changes (any thread: they are queued with their time and noted on the next frame).</summary>
    private void CrossWatch(WorldState world, string region)
    {
        if (!CrossFrames.Active || !_crossWatched.Add(world)) return;
        world.Changed += c =>
        {
            if (c.Kind is WorldChangeKind.AvatarAdded or WorldChangeKind.AvatarChanged or WorldChangeKind.AvatarMoved or WorldChangeKind.AvatarRemoved or WorldChangeKind.RegionReset)
                _crossEvents.Enqueue((CrossClock(), string.Create(CultureInfo.InvariantCulture, $"{region} {c.Kind} local={c.Id.LocalId} epoch={c.Id.Epoch} handoff={(c.Handoff ? 1 : 0)}")));
        };
    }

    /// <summary>Once a frame, after the camera has been placed.</summary>
    private void CrossingFrameTick()
    {
        if (!CrossFrames.Active || _presented is not { } session) return;
        double now = CrossClock();
        CrossWatch(session.World, "here");
        foreach (var st in _stacks.Values) CrossWatch(st.World, st.Sim.Name);
        while (_crossEvents.TryDequeue(out var ev)) CrossFrames.Note(ev.At, ev.Text);
        var agent = session.AgentId;
        CrossFrames.Frame(now, () =>
        {
            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"frame={(long)Engine.GetProcessFrames()} t={now:0.0000} cur={session.Region?.Name ?? ""} cur_handle={session.World.Region?.GridX},{session.World.Region?.GridY}");
            sb.Append(" shift=").Append(V3(_driver?.CameraShift ?? default)).Append(" eye=").Append(V3(_eye)).Append(" anchor=").Append(V3(_agentCentre));
            sb.Append(CultureInfo.InvariantCulture, $" own_users={_handoffLedger.OwnMotion.Users}");
            void Region(string name, WorldState world, Presentation.AvatarPresenter? avatars)
            {
                sb.Append(CultureInfo.InvariantCulture, $" [{name} grid={world.Region?.GridX},{world.Region?.GridY} off={world.OriginOffset.X:0},{world.OriginOffset.Y:0} td={world.TimeDilation:0.000}{avatars?.TraceAgent(agent)}]");
            }
            if (_anchorWorld is { } anchor) Region("anchor", anchor, _presenter?.Avatars);
            foreach (var st in _stacks.Values) Region(st.Sim.Name, st.World, st.Avatars);
            var layers = new List<Presentation.PrimLayer>();
            if (_presenter?.Prims is { } own) layers.Add(own);
            layers.AddRange(_stacks.Values.Select(s => s.Prims));
            var states = new List<Presentation.WornFrameState>();
            foreach (var layer in layers)
            {
                states.Clear();
                layer.WornFrameStates(agent, states);
                foreach (var s in states)
                    if (s.Origin is { } o && s.Shown)
                        sb.Append(CultureInfo.InvariantCulture, $" worn={s.Full.ToString("N")[..8]}@{layer.Tag}{(s.Rigged ? "r" : "")}{(s.Held ? "H" : "")}:{o.X:0.000},{o.Y:0.000},{o.Z:0.000}");
            }
            return sb.ToString();
        });
    }

    /// <summary>Writes a window still open (the viewer's shutdown).</summary>
    private void CrossingFramesFlush() => _crossFrames?.Flush();
}
