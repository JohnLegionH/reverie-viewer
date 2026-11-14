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
    private sealed class HandoffSample
    {
        public Guid Agent;
        public string Region = "";
        public double Until;
        public double Started;
        public int Baseline = -1;
        public int Frames, AvatarMissing, WornShort, MinWorn = int.MaxValue;
        public int Leg;
        public int NotOnce, OnOtherSkeleton, BodyOff;
        public double MaxBodyOff;
        public double MaxMismatch;
        public HashSet<Guid> Base = new();
        public List<string> Missing = new();
    }

    private HandoffSample? _handoffSample;
    private int _handoffLegs;

    /// <summary>After the agent crosses into a region the avatar and the worn pieces are looked at in every frame for three seconds: is the
    /// avatar drawn (by the region left or the region entered), and how many different worn pieces are drawn in all. A frame with the avatar
    /// not drawn or fewer pieces than the first frame had is a gap. One line per frame and a summary at the end.</summary>
    private void StartHandoffSample(Guid agent)
    {
        WornTraceNoteHandoff(agent);
        if (_handoffSample is not null) return;   // one at a time
        double now = Time.GetTicksMsec() / 1000.0;
        _handoffSample = new HandoffSample { Agent = agent, Region = _presented?.Region?.Name ?? "", Started = now, Until = now + 3.0, Leg = ++_handoffLegs };
    }

    private void SampleHandoff()
    {
        if (_handoffSample is not { } s) return;
        double now = Time.GetTicksMsec() / 1000.0;
        bool avatar = (_presenter?.Avatars?.AgentDrawn(s.Agent) ?? false) || _stacks.Values.Any(x => x.Avatars.AgentDrawn(s.Agent));
        var worn = new HashSet<Guid>();   // the pieces of the sampled agent only, in whichever region's layer draws them
        _presenter?.Prims?.CollectWornDrawn(worn, s.Agent);
        foreach (var st in _stacks.Values) st.Prims.CollectWornDrawn(worn, s.Agent);
        if (s.Baseline < 0) s.Baseline = worn.Count;
        s.Frames++;
        s.MinWorn = Math.Min(s.MinWorn, worn.Count);
        if (!avatar) s.AvatarMissing++;
        if (worn.Count < s.Baseline)
        {
            s.WornShort++;
            if (s.Baseline > 0 && s.Missing.Count < 4) s.Missing.Add(string.Join(",", s.Base.Where(x => !worn.Contains(x)).Select(x => x.ToString("N")[..8])));
        }
        if (s.Frames == 1) s.Base = new HashSet<Guid>(worn);
        // one avatar: the bodies shown for the agent (once, in every frame), the skeleton each worn piece is bound to, and the largest gap between a piece's joint and the body's
        var presenters = new List<Presentation.AvatarPresenter>();
        if (_presenter?.Avatars is { } own) presenters.Add(own);
        presenters.AddRange(_stacks.Values.Select(x => x.Avatars));
        var shown = presenters.SelectMany(p => p.DrawnInstances(s.Agent)).Distinct().ToList();
        var bound = new List<Presentation.WornBinding>();
        _presenter?.Prims?.WornBindings(s.Agent, bound);
        foreach (var st in _stacks.Values) st.Prims.WornBindings(s.Agent, bound);
        int elsewhere = 0;
        double mismatch = 0;
        foreach (var b in bound)
        {
            if (shown.Count == 1 && !(ReferenceEquals(b.Layer, shown[0].Layer) && b.SkeletonRow == shown[0].Row)) elsewhere++;
            System.Numerics.Matrix4x4 bodyJoint = default;
            bool have = false;
            foreach (var p in presenters)
                if (b.Rigged ? p.TryDrawnRigged(s.Agent, out bodyJoint) : p.TryDrawnPoint(s.Agent, b.Point, out bodyJoint)) { have = true; break; }
            if (!have) { elsewhere++; continue; }
            mismatch = Math.Max(mismatch, Math.Max(Math.Abs(b.Joint.M41 - bodyJoint.M41), Math.Max(Math.Abs(b.Joint.M42 - bodyJoint.M42), Math.Max(Math.Abs(b.Joint.M43 - bodyJoint.M43),
                Math.Max(Math.Abs(b.Joint.M11 - bodyJoint.M11), Math.Max(Math.Abs(b.Joint.M22 - bodyJoint.M22), Math.Abs(b.Joint.M33 - bodyJoint.M33)))))));
        }
        string skeletons = string.Join(",", bound.Select(b => $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(b.Layer) & 0xFFFF:x}.{b.SkeletonRow}").Distinct());
        double bodyOff = BodyOffMetres(s.Agent);
        if (s.Frames > 1 && bodyOff > 0.5) s.BodyOff++;
        s.MaxBodyOff = Math.Max(s.MaxBodyOff, bodyOff);
        if (s.Frames > 1)
        {
            if (shown.Count != 1) s.NotOnce++;
            s.OnOtherSkeleton += elsewhere;
            s.MaxMismatch = Math.Max(s.MaxMismatch, mismatch);
        }
        Log.Info("handoff.frame", CoreLog.F("leg", s.Leg), CoreLog.F("t_ms", (int)((now - s.Started) * 1000)), CoreLog.F("avatar_drawn", avatar ? 1 : 0), CoreLog.F("worn_drawn", worn.Count), CoreLog.F("worn_expected", s.Baseline),
            CoreLog.F("bodies", shown.Count), CoreLog.F("pieces_bound", bound.Count), CoreLog.F("on_other_skeleton", elsewhere), CoreLog.F("joint_mismatch", mismatch.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)), CoreLog.F("skeletons", skeletons), CoreLog.F("body_off_m", bodyOff.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)));
        if (now < s.Until) return;
        Log.Info("handoff.frames_summary", CoreLog.F("leg", s.Leg), CoreLog.F("own", s.Agent == _presented?.AgentId ? 1 : 0), CoreLog.F("region", s.Region), CoreLog.F("frames", s.Frames), CoreLog.F("frames_without_avatar", s.AvatarMissing),
            CoreLog.F("frames_with_fewer_pieces", s.WornShort), CoreLog.F("worn_expected", s.Baseline), CoreLog.F("fewest_worn_drawn", s.MinWorn == int.MaxValue ? 0 : s.MinWorn), CoreLog.F("missing", string.Join(";", s.Missing)),
            CoreLog.F("frames_not_one_body", s.NotOnce), CoreLog.F("pieces_on_other_skeleton", s.OnOtherSkeleton), CoreLog.F("largest_joint_mismatch", s.MaxMismatch.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)),
            CoreLog.F("frames_body_off", s.BodyOff), CoreLog.F("largest_body_off_m", s.MaxBodyOff.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)));
        _handoffSample = null;
    }
}
