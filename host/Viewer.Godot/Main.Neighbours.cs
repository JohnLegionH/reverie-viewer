// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Core.Diagnostics;
using Viewer.Core.Residency;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Godot.Presentation;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// Neighbouring regions in the scene (with the setting on; otherwise none of this runs). The presentation built at login is the anchor's;
/// each neighbour the session connects gets a <see cref="NeighbourStack"/>, drawn at its offset from the anchor region. When the agent
/// walks into a neighbour nothing is rebuilt: the own-avatar driver moves to that region's avatar presenter, and the camera it
/// reports to the region is expressed in that region's own frame.
/// </summary>
public partial class Main
{
    /// <summary>Region addresses ("ip:port") the next login's transport is told not to connect in advance (set by the development automation).</summary>
    internal List<string> HoldsForNextLogin { get; } = new();

    private readonly ConcurrentQueue<RegionWorldChange> _regionChanges = new();
    private readonly Dictionary<SimRef, NeighbourStack> _stacks = new();
    private readonly List<NeighbourStack> _retiring = new();   // dropped neighbours whose instances are being freed a slice a frame

    /// <summary>The most a frame spends freeing a dropped neighbour's instances. The official viewer has no such limit: it kills a removed region's
    /// objects and cleans them up in the one call (LLViewerObjectList::killObjects, llviewerobjectlist.cpp:1359-1377).</summary>
    private const double RetireBudgetMs = 2.0;

    private void RetireNeighbours()
    {
        if (_retiring.Count == 0) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        double Now() => (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        for (int i = 0; i < _retiring.Count && Now() < RetireBudgetMs; )
        {
            var s = _retiring[i];
            if (s.RetireStep(RetireBudgetMs - Now(), Now))
            {
                _retiring.RemoveAt(i);
                s.Dispose();
            }
            else i++;
        }
    }
    private NeighbourStack? _currentStack;   // null: the anchor's own presentation holds the agent
    private ViewerSession? _neighboursFor;
    private WorldState? _anchorWorld;
    private (int X, int Y)? _anchorCell;

    /// <summary>The avatar presenter that holds the agent's own avatar: the current region's.</summary>
    private AvatarPresenter? OwnAvatars => _currentStack?.Avatars ?? _presenter?.Avatars;

    /// <summary>What the session's regions look like in the scene (automation; answers grid cells, sizes, counts and ids, never names): each region
    /// with its offset from the anchor, whether it holds the agent, and what is drawn for it.</summary>
    internal object NeighboursReport()
    {
        if (_presented is not { } session)
            return new { in_world = false };
        var rows = new List<object>();
        foreach (var w in session.Worlds)
        {
            bool anchor = ReferenceEquals(w.World, _anchorWorld) || (_anchorWorld is null && w.IsCurrent);
            _stacks.TryGetValue(w.Sim, out var stack);
            var snap = w.World.Snapshot();
            rows.Add(new
            {
                grid = new[] { w.Region.GridX, w.Region.GridY },
                size = new[] { w.Region.SizeX, w.Region.SizeY },
                current = w.IsCurrent,
                anchor,
                origin_offset = new[] { w.OriginOffset.X, w.OriginOffset.Y },
                objects = snap.Objects.Count,
                avatars = snap.Avatars.Values.Select(a => a.AgentId.ToString()).ToArray(),
                // the look this client holds for each avatar of that region (a copy held for a neighbour is not the current region's)
                looks = w.World.Appearances().Select(x => (object)new { agent = x.AgentId.ToString(), look = LookReport(LookSummary.Of(x)) }).ToArray(),
                terrain_patches = w.World.TerrainPatchCount,
                water_height = w.Region.WaterHeight,
                chunks = anchor ? _presenter?.View.ChunkCount ?? 0 : stack?.View.ChunkCount ?? 0,
                instances = anchor ? _presenter?.Prims?.Instances ?? 0 : stack?.Prims.Instances ?? 0,
                drawn = anchor || (stack?.Drawn ?? false),
            });
        }
        return new { in_world = true, enabled = session.NeighboursEnabled, stacks = _stacks.Count, water_plan_regions = _presenter?.NeighbourWater.Count ?? 0, anchor_has_water = _presenter?.Environment?.HasWater ?? false, regions = rows };
    }

    /// <summary>The terrain texturing of each region in the scene (automation): where each detail layer came from, its pixel size, the repeat and the pattern offset.</summary>
    internal object TerrainReport()
    {
        if (_presented is not { } session)
            return new { in_world = false };
        var rows = new List<object>();
        foreach (var w in session.Worlds)
        {
            bool anchor = ReferenceEquals(w.World, _anchorWorld) || (_anchorWorld is null && w.IsCurrent);
            _stacks.TryGetValue(w.Sim, out var stack);
            var presenter = anchor ? _presenter : stack?.Presenter;
            rows.Add(new
            {
                grid = new[] { w.Region.GridX, w.Region.GridY },
                size = new[] { w.Region.SizeX, w.Region.SizeY },
                origin_offset = new[] { w.OriginOffset.X, w.OriginOffset.Y },
                has_terrain_settings = w.Region.Terrain is not null,
                detail_sources = presenter?.DetailSources,
                detail_sizes = presenter?.DetailSizes,
                repeat_metres = Viewer.Core.World.TerrainComposition.DetailRepeatMetres,
                pattern_offset = presenter is null ? null : new[] { presenter.DetailOffsetUsed.X, presenter.DetailOffsetUsed.Y },
                chunks = anchor ? _presenter?.View.ChunkCount ?? 0 : stack?.View.ChunkCount ?? 0,
            });
        }
        return new { in_world = true, regions = rows };
    }

    private static object LookReport(LookSummary s) => new { version = s.Version, cof_version = s.CofVersion, bakes = s.Bakes, params_hash = s.ParamsHash, height_byte = s.HeightByte };

    private void AttachNeighbours(ViewerSession session)
    {
        _neighboursFor = session;
        _anchorWorld = session.World;
        if (session.World.Region is { } start) _anchorCell = (start.GridX, start.GridY);   // kept: the anchor world is retired when its circuit goes, and its region with it
        session.WorldsChanged += _regionChanges.Enqueue;
        foreach (var w in session.Worlds)   // any already connected when the presentation was made
            if (!ReferenceEquals(w.World, _anchorWorld))
                _regionChanges.Enqueue(new RegionWorldChange(RegionWorldChangeKind.Added, w.Sim));
    }

    private void DetachNeighbours()
    {
        if (_neighboursFor is { } session)
            session.WorldsChanged -= _regionChanges.Enqueue;
        _neighboursFor = null;
        _regionChanges.Clear();
        _currentStack = null;
        _anchorWorld = null;
        TeardownSteps.RunAll(msg => Log.Warn("teardown.step-failed", CoreLog.F("detail", msg)), (name, ms) => Log.Info("teardown.step-slow", CoreLog.F("step", name), CoreLog.F("ms", Math.Round(ms, 1))),
            _retiring.Concat(_stacks.Values).Select(s => ("neighbour " + s.Sim.Name, (System.Action)s.Dispose)).ToArray());
        _retiring.Clear();
        _stacks.Clear();
    }

    /// <summary>Main thread, each frame after the anchor's own frame: applies the session's region changes, then works each neighbour.</summary>
    private void UpdateNeighbours()
    {
        RetireNeighbours();
        if (_neighboursFor is not { } session || _presenter is null || _camera is null)
            return;
        while (_regionChanges.TryDequeue(out var change))
        {
            CrossNote($"region {change.Kind} {change.Sim.Name}");
            switch (change.Kind)
            {
                case RegionWorldChangeKind.Added:
                    AddStack(session, change.Sim);
                    break;
                case RegionWorldChangeKind.Removed:
                    if (_stacks.Remove(change.Sim, out var gone))
                    {
                        if (ReferenceEquals(_currentStack, gone)) _currentStack = null;
                        gone.BeginRetire();
                        _retiring.Add(gone);
                        Log.Info("neighbour.removed", CoreLog.F("region", change.Sim.Name));
                    }
                    break;
            }
            PointDriverAtCurrent(session, change.Kind == RegionWorldChangeKind.Crossed);   // a crossing, or a teleport that went back to the anchor
        }
        RefreshEdges(session);
        RunAttachmentChecks(session);
        SampleHandoff();
        WornTraceTick();
        BodyOffWatch();
        if (_presenter is not null)
            _presenter.NeighbourWater = _stacks.Values
                .Where(s => s.World.Region is { WaterHeight: not null })
                .Select(s => new Viewer.Core.World.WaterRect(s.World.OriginOffset.X, s.World.OriginOffset.Y,
                s.World.OriginOffset.X + s.World.Region!.SizeX, s.World.OriginOffset.Y + s.World.Region.SizeY, s.World.Region.WaterHeight!.Value))
                .ToArray();
        if (_glow is not null)
        {
            // the neighbours' glowing faces glow too: the pass knows their prim layers
            var layers = _glow.OtherPrims;
            bool same = layers.Count == _stacks.Count;
            if (same)
            {
                int i = 0;
                foreach (var stack in _stacks.Values)
                    if (!ReferenceEquals(layers[i++], stack.Prims)) { same = false; break; }
            }
            if (!same) _glow.OtherPrims = _stacks.Values.Select(st => st.Prims).ToArray();
        }
        FrameBurstTick();
        if (_stacks.Count == 0)
        {
            WornFrameTick();
            return;
        }
        var view = _presenter.Prims?.ResidencyView;
        foreach (var stack in _stacks.Values)
        {
            var region = stack.World.Region;
            bool inReach = region is not null;   // the regions the server enables are drawn; the camera's far plane and the draw distance decide what shows
            stack.Frame(_camera, view, inReach);
        }
        WornFrameTick();
    }

    /// <summary>Whether a region the session holds lies at a place given in <paramref name="world"/>'s region coordinates: an object
    /// predicted off the region's edge is carried on only where there is one.</summary>
    private static System.Func<System.Numerics.Vector3, bool> RegionAtFor(ViewerSession session, WorldState world) => p =>
    {
        if (world.Region is not { } here) return false;
        double x = here.GridX * 256.0 + p.X, y = here.GridY * 256.0 + p.Y;
        foreach (var w in session.Worlds)
            if (w.World.Region is { } r && x >= r.GridX * 256.0 && x < r.GridX * 256.0 + r.SizeX && y >= r.GridY * 256.0 && y < r.GridY * 256.0 + r.SizeY)
                return true;
        return false;
    };

    /// <summary>Whether the object with local id <paramref name="local"/> in <paramref name="world"/> is selected: the selected linkset's root or one of
    /// its prims (the official viewer selects every prim of a linkset, and does not carry a selected object).</summary>
    private bool SelectedHere(WorldState world, uint local)
    {
        if (_interact?.Selected is not { } sel || !ReferenceEquals(_interactFor?.World, world) || sel.Epoch != world.Epoch) return false;
        return sel.LocalId == local || world.Object(new ObjectId(world.Epoch, local))?.ParentLocalId == sel.LocalId;
    }

    /// <summary>The ground outside <paramref name="world"/>'s region, read from the regions that lie there, for its terrain grid's border.</summary>
    private static System.Func<int, int, float?> OutsideHeightFor(ViewerSession session, WorldState world) => (gx, gy) =>
    {
        var all = session.Worlds;
        var self = all.FirstOrDefault(w => ReferenceEquals(w.World, world));
        return self is null ? null : OuterTerrain.HeightAt(all, self, gx, gy);
    };

    private readonly EdgeRefreshWatch<WorldState> _edgeWatch = new();
    private double _edgeCheckedAt;

    /// <summary>Once a second: the regions whose terrain changed since (or went), and the regions touching them build their border again.</summary>
    private void RefreshEdges(ViewerSession session)
    {
        double now = Time.GetTicksMsec() / 1000.0;
        if (now - _edgeCheckedAt < 1.0)
            return;
        _edgeCheckedAt = now;
        var worlds = session.Worlds;
        var counts = worlds.ToDictionary(w => w.World, w => w.World.TerrainPatchCount, ReferenceEqualityComparer.Instance as System.Collections.Generic.IEqualityComparer<WorldState>);
        var changed = _edgeWatch.Poll(counts, System.DateTime.UtcNow);
        if (changed.Count == 0)
            return;
        foreach (var w in worlds)
        {
            // a region whose neighbour changed builds its edges again (a region that gone has no entry in the list: its former neighbours are all of them, cheaply)
            bool touchesChanged = changed.Any(c => !ReferenceEquals(c, w.World) && (worlds.FirstOrDefault(x => ReferenceEquals(x.World, c)) is not { } cw || OuterTerrain.Touches(w, cw)));
            if (!touchesChanged)
                continue;
            if (ReferenceEquals(w.World, _anchorWorld)) _presenter?.MarkEdgesDirty();
            else if (_stacks.TryGetValue(w.Sim, out var stack)) stack.Presenter.MarkEdgesDirty();
        }
    }

    private void AddStack(ViewerSession session, SimRef sim)
    {
        var rw = session.Worlds.FirstOrDefault(w => ReferenceEquals(w.Sim, sim));
        // the anchor's cell is the one the session started in; when the agent has been so far away that the anchor world was retired and a new world was made
        // for that region on the way back, that region is drawn by a stack like any other, and its cell is still known
        var cell = _anchorWorld?.Region is { } anchorRegion ? (anchorRegion.GridX, anchorRegion.GridY) : _anchorCell;
        if (rw is null || ReferenceEquals(rw.World, _anchorWorld) || _stacks.ContainsKey(sim) || cell is not { } anchor)
            return;
        var stack = new NeighbourStack(Scenario(), _stage!, rw, session.AgentId, Body(), _animations, _textures, _meshes, _materials,
            _presenter.Prims?.Hub, MeshLodFactor(), (anchor.Item1, anchor.Item2),
            OutsideHeightFor(session, rw.World), _handoffLedger, _bodyHost);
        stack.Prims.Moving.HasRegionAt = RegionAtFor(session, rw.World);
        stack.Avatars.Scene.HasRegionAt = RegionAtFor(session, rw.World);
        var stackWorld = rw.World;
        stack.Prims.Moving.Selected = local => SelectedHere(stackWorld, local);
        _stacks[sim] = stack;
        stack.Avatars.HandedOver += StartHandoffSample;
        HookRiggedLogs(stack.Prims, session);
        Log.Info("neighbour.added", CoreLog.F("region", sim.Name), CoreLog.F("offset", $"{rw.OriginOffset.X:0},{rw.OriginOffset.Y:0}"));
    }

    /// <summary>The own-avatar driver follows the region the agent is in: its avatar presenter, and the camera expressed in that region's frame.</summary>
    private void PointDriverAtCurrent(ViewerSession session, bool crossed)
    {
        var world = session.World;
        NeighbourStack? now = ReferenceEquals(world, _anchorWorld) ? null : _stacks.Values.FirstOrDefault(s => ReferenceEquals(s.World, world));
        if (ReferenceEquals(now, _currentStack) && (_driver is null || _driver.CameraShift == world.OriginOffset))
            return;
        _currentStack = now;
        if (OwnAvatars is { } avatars && _driver is not null)
        {
            _driver.Rebind(avatars.Scene, world, keepHeading: crossed);   // a crossing keeps the agent's heading; a teleport takes the landing's
            _driver.CameraShift = world.OriginOffset;
            _addons?.RegionChanged(session.Region?.Name ?? "");   // the add-ons hear of it (the overrider asks the region entered again for what the avatar plays)
            Log.Info("neighbour.crossed", CoreLog.F("region", session.Region?.Name ?? ""));
            _crossPoint = null;   // the worn trace measures from here
            if (crossed) FrameBurstStart("crossing");
            if (crossed) CrossMark();
            QueueAttachmentChecks(session.Region?.Name ?? "");
        }
    }

    /// <summary>The log lines of a prim layer's worn rigged meshes, for the anchor's layer and each neighbour's: a piece not drawn and why, and a piece drawn.</summary>
    private void HookRiggedLogs(PrimLayer prims, ViewerSession session)
    {
        prims.RiggedAttachmentMet = n => Log.Info("avatars.rigged_attachment_not_drawn",   // once per object and reason
            CoreLog.F("reason", n.Why.ToString()), CoreLog.F("temporary", n.Temporary), CoreLog.F("wearer", n.WearerLocalId),
            CoreLog.F("wearer_is", n.WearerAgentId == session.AgentId ? "self" : "other"), CoreLog.F("wearer_name", n.WearerName),
            CoreLog.F("point", n.Point), CoreLog.F("asset", n.Asset));
        prims.RiggedAttachmentDrawn = d => Log.Info("avatars.rigged_attachment_drawn",   // once per object
            CoreLog.F("wearer", d.WearerLocalId), CoreLog.F("wearer_is", d.WearerAgentId == session.AgentId ? "self" : "other"), CoreLog.F("wearer_name", d.WearerName),
            CoreLog.F("point", d.Point), CoreLog.F("asset", d.Asset), CoreLog.F("lod", d.Lod), CoreLog.F("joints", d.Joints), CoreLog.F("plain_because", d.PlainBecause?.ToString() ?? ""));
    }

    private readonly List<(double At, string Region)> _attachmentChecks = new();

    /// <summary>After the agent crosses into a region, what it wears is counted twice (1.5 s and 6 s later): how many worn objects the region has told
    /// the session about and how many of them are drawn, so a piece lost in a crossing shows in the log whatever its kind.</summary>
    private void QueueAttachmentChecks(string region)
    {
        double now = Time.GetTicksMsec() / 1000.0;
        _attachmentChecks.Add((now + 1.5, region));
        _attachmentChecks.Add((now + 6.0, region));
    }

    private void RunAttachmentChecks(ViewerSession session)
    {
        if (_attachmentChecks.Count == 0) return;
        double now = Time.GetTicksMsec() / 1000.0;
        for (int i = _attachmentChecks.Count - 1; i >= 0; i--)
        {
            if (_attachmentChecks[i].At > now) continue;
            var region = _attachmentChecks[i].Region;
            _attachmentChecks.RemoveAt(i);
            var world = session.World;
            var prims = _currentStack?.Prims ?? _presenter?.Prims;
            if (prims is null || world.AvatarOf(session.AgentId) is not { } self) { Log.Info("attachments.after_crossing", CoreLog.F("region", region), CoreLog.F("self", "not known")); continue; }
            var roots = world.AttachmentRoots(self.Id.LocalId);
            Log.Info("attachments.after_crossing", CoreLog.F("region", region), CoreLog.F("wearer", self.Id.LocalId), CoreLog.F("worn_roots", roots.Count),
                CoreLog.F("drawn_roots", roots.Count(r => prims.Draws(r.Id.LocalId))), CoreLog.F("rigged_objects", prims.RiggedObjects), CoreLog.F("rigged_shown", prims.RiggedShown),
                CoreLog.F("waiting_for_wearer", world.PendingAttachmentRoots), CoreLog.F("handed_over", session.Identity.Handoffs));
        }
    }
}
