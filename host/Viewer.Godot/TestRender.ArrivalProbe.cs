// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Viewer.Godot.Presentation;

namespace Viewer.Godot;

public partial class TestRender
{
    /// <summary>
    /// Where the arrival's main-thread time and allocation go (--tr-frame-probe): each presentation frame's own figures from
    /// <see cref="FrameProbe"/>, with the managed allocation and the garbage-collector pauses that fell inside the frame. Measurement only.
    /// </summary>
    private sealed class ArrivalProbe
    {
        private readonly int _sections = FrameProbe.Count;
        private readonly double[] _totalMs, _maxMs, _msInSlowFrames;
        private readonly long[] _totalBytes;
        private readonly int[] _framesDominant, _calls;
        private readonly List<double> _allocKiB = new();
        private readonly List<(double Ms, string Top)> _worst = new();
        private readonly List<(double Ms, string Name, double TopMs, string Second, double SecondMs)> _slow = new();   // every slice over the limit: its length and its two largest sections
        private long _a0;
        private int _g0a, _g1a, _g2a;
        private TimeSpan _pause0;
        public int SlicesWithCollection, SlicesOver;

        /// <summary>The most time one slice spent on the bind-table atlas (growing it and handing it to the engine), in milliseconds.</summary>
        public double SkinMaxMs;
        public double PauseInSlicesMs;
        public long AllocInSlices;
        public int Gen0InSlices, Gen1InSlices, Gen2InSlices;

        public ArrivalProbe()
        {
            _totalMs = new double[_sections]; _maxMs = new double[_sections]; _msInSlowFrames = new double[_sections];
            _totalBytes = new long[_sections]; _framesDominant = new int[_sections]; _calls = new int[_sections];
        }

        public void Begin()
        {
            _a0 = GC.GetAllocatedBytesForCurrentThread();
            (_g0a, _g1a, _g2a) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
            _pause0 = GC.GetTotalPauseDuration();
        }

        public void End(double sliceMs)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread() - _a0;
            int d0 = GC.CollectionCount(0) - _g0a, d1 = GC.CollectionCount(1) - _g1a, d2 = GC.CollectionCount(2) - _g2a;
            double pause = (GC.GetTotalPauseDuration() - _pause0).TotalMilliseconds;
            var f = FrameProbe.TakeFrame();
            AllocInSlices += allocated;
            _allocKiB.Add(allocated / 1024.0);
            if (d0 + d1 + d2 > 0) SlicesWithCollection++;
            Gen0InSlices += d0; Gen1InSlices += d1; Gen2InSlices += d2;
            PauseInSlicesMs += pause;
            // (a slice the collector paused in is left out: the collections come from decoding on other threads, and a pause lands in whatever section is running)
            if (d0 + d1 + d2 == 0) SkinMaxMs = Math.Max(SkinMaxMs, f.Ms[FrameProbe.SkinResize] + f.Ms[FrameProbe.SkinFlushAtlas]);
            int top = -1;
            for (int i = 0; i < _sections; i++)
            {
                _totalMs[i] += f.Ms[i]; _totalBytes[i] += f.Bytes[i]; _calls[i] += f.Calls[i];
                if (f.Ms[i] > _maxMs[i]) _maxMs[i] = f.Ms[i];
                if (top < 0 || f.Ms[i] > f.Ms[top]) top = i;
            }
            if (sliceMs > MainThreadSliceLimitMs && top >= 0)
            {
                SlicesOver++;
                _framesDominant[top]++;
                int second = -1;
                for (int i = 0; i < _sections; i++) if (i != top && (second < 0 || f.Ms[i] > f.Ms[second])) second = i;
                _slow.Add((sliceMs, FrameProbe.NameOf(top), f.Ms[top], second < 0 ? "" : FrameProbe.NameOf(second), second < 0 ? 0 : f.Ms[second]));
                for (int i = 0; i < _sections; i++) _msInSlowFrames[i] += f.Ms[i];
                var order = Enumerable.Range(0, _sections).Where(i => f.Ms[i] > 0.05).OrderByDescending(i => f.Ms[i]).Take(4)
                    .Select(i => $"{FrameProbe.NameOf(i)} {f.Ms[i]:F1}").ToArray();
                string text = string.Join("; ", order) + (pause > 0.05 ? $"; gc pause {pause:F1} ({d0}/{d1}/{d2})" : "") + $"; alloc {allocated / 1024.0:F0} KiB; probe total {f.TotalMs:F1}";
                _worst.Add((sliceMs, text));
                if (_worst.Count > 12) { _worst.Sort((a, b) => b.Ms.CompareTo(a.Ms)); _worst.RemoveRange(8, _worst.Count - 8); }
            }
        }

        public object Report()
        {
            var worst = _worst.OrderByDescending(w => w.Ms).Take(8).Select(w => $"{w.Ms:F1} ms: {w.Top}").ToArray();
            var rows = Enumerable.Range(0, _sections).Where(i => _calls[i] > 0).OrderByDescending(i => _totalMs[i])
                .Select(i => new
                {
                    section = FrameProbe.NameOf(i), total_ms = Math.Round(_totalMs[i], 1), max_frame_ms = Math.Round(_maxMs[i], 2), in_slow_frames_ms = Math.Round(_msInSlowFrames[i], 1),
                    slow_frames_it_led = _framesDominant[i], entered = _calls[i], alloc_mib = Math.Round(_totalBytes[i] / 1048576.0, 2),
                }).ToArray();
            return new
            {
                slices_over_limit = SlicesOver, sections = rows, worst_slices = worst,
                // every slice over the limit: whether one section alone is over the limit (an indivisible unit), or the sections add up
                slow_slices = new
                {
                    n = _slow.Count,
                    one_section_alone_over_limit = _slow.Count(x => x.TopMs > MainThreadSliceLimitMs),
                    top_section_share_median = _slow.Count == 0 ? 0 : Math.Round(_slow.Select(x => x.TopMs / x.Ms).OrderBy(x => x).ElementAt(_slow.Count / 2), 2),
                    list = _slow.Select(x => $"{x.Ms:F1}|{x.Name} {x.TopMs:F1}|{x.Second} {x.SecondMs:F1}").ToArray(),
                },
                allocation_kib_per_slice = Dist(_allocKiB), alloc_in_slices_mib = Math.Round(AllocInSlices / 1048576.0, 1),
                slices_with_a_collection = SlicesWithCollection, collections_in_slices = new { gen0 = Gen0InSlices, gen1 = Gen1InSlices, gen2 = Gen2InSlices },
                gc_pause_in_slices_ms = Math.Round(PauseInSlicesMs, 1),
            };
        }
    }
}
