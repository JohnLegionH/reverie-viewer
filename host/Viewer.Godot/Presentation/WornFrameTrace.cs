// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Core.Diagnostics;

namespace Viewer.Godot.Presentation;

/// <summary>One worn piece as one region's layer holds it this frame: whether it has an instance, whether the instance is shown, whether the layer only
/// keeps it for the region that took the wearer over.</summary>
public readonly record struct WornFrameState(Guid Full, bool Rigged, bool HasInstance, bool Shown, bool Held, Vector3? Origin = null);

/// <summary>
/// NV_WORN_FRAMES=1: a development aid that records, for every frame, which worn pieces are drawn (an instance that is shown) by any region's layer, and the
/// object changes and hand-over steps around a region change, each with the engine's frame number. Off by default: nothing is read, built or logged without
/// it. <c>worn.frame</c> lines are written when the set of drawn pieces or their layers changes (the frame number says how many frames each state lasted);
/// <c>worn.event</c> lines carry the change kind, local id and region of each object change that concerns a worn piece.
/// </summary>
public static class WornFrameTrace
{
    public static readonly bool On = System.Environment.GetEnvironmentVariable("NV_WORN_FRAMES") == "1";

    public static long Frame => (long)Engine.GetProcessFrames();

    public static string Short(Guid id) => id == Guid.Empty ? "-" : id.ToString("N")[..8];

    public static void Event(PrimLayer layer, string what, uint local, Guid full, string note = "")
    {
        if (!On) return;
        Main.Log.Info("worn.event", CoreLog.F("frame", Frame), CoreLog.F("layer", layer.Tag), CoreLog.F("region", layer.RegionName), CoreLog.F("what", what),
            CoreLog.F("local", local), CoreLog.F("piece", Short(full)), CoreLog.F("note", note));
    }

    /// <summary>The per-frame record for <paramref name="agent"/>'s pieces over <paramref name="layers"/>: the state is written when it differs from the last one written.</summary>
    public sealed class Recorder
    {
        private string _last = "";
        private readonly HashSet<Guid> _everSeen = new();
        private readonly Dictionary<(string Layer, Guid Piece), Vector3> _lastAt = new();

        /// <summary>A rigged piece drawn by the same layer in two frames in a row that moved more than this many metres between them (a flying avatar moves about 0.4 m a frame).</summary>
        public const float JumpMetres = 1.5f;

        public void Tick(Guid agent, IReadOnlyList<PrimLayer> layers)
        {
            if (!On) return;
            var states = new List<(string Layer, WornFrameState S)>();
            var tmp = new List<WornFrameState>();
            foreach (var layer in layers)
            {
                tmp.Clear();
                layer.WornFrameStates(agent, tmp);
                foreach (var s in tmp) states.Add((layer.Tag, s));
            }
            foreach (var (_, s) in states) _everSeen.Add(s.Full);
            foreach (var (layerTag, s) in states)
            {
                if (s.Origin is not { } at || !s.Shown) { _lastAt.Remove((layerTag, s.Full)); continue; }
                if (_lastAt.TryGetValue((layerTag, s.Full), out var was) && was.DistanceTo(at) > JumpMetres)
                    Main.Log.Info("worn.jump", CoreLog.F("frame", Frame), CoreLog.F("layer", layerTag), CoreLog.F("piece", Short(s.Full)), CoreLog.F("metres", Math.Round(was.DistanceTo(at), 2)));
                _lastAt[(layerTag, s.Full)] = at;
            }
            var parts = new List<string>();
            int drawn = 0;
            foreach (var piece in _everSeen.OrderBy(x => x))
            {
                var mine = states.Where(x => x.S.Full == piece).ToList();
                bool shown = mine.Any(x => x.S.HasInstance && x.S.Shown);
                if (shown) drawn++;
                string where = mine.Count == 0 ? "-" : string.Join("|", mine.Select(x => $"{x.Layer}:{(x.S.HasInstance ? (x.S.Shown ? "S" : "h") : "n")}{(x.S.Held ? "H" : "")}{(x.S.Rigged ? "r" : "")}"));
                parts.Add($"{Short(piece)}={(shown ? 1 : 0)}@{where}");
            }
            string now = string.Join(";", parts);
            if (now == _last) return;
            _last = now;
            Main.Log.Info("worn.frame", CoreLog.F("frame", Frame), CoreLog.F("drawn", drawn), CoreLog.F("pieces", _everSeen.Count), CoreLog.F("state", now));
        }
    }
}
