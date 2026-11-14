// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Viewer.Motion;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A development aid (a real skip unless the folder is named): another avatar's recorded motion, as a second viewer drew it, against the
/// official viewer's drawing of the statements that viewer took (<see cref="OfficialOwnAvatarReplay"/>), frame by frame. The official viewer
/// has no settling time before it follows a statement: a statement sets the object's place at once, and the one-second limit it has applies
/// only to a prediction across a border (llviewerobject.cpp, interpolateLinearMotion, mRegionCrossExpire; LLViewerObject::sMaxRegionCrossingInterpolationTime),
/// so every frame from the first statement the record holds is compared (a record begins in the middle of a flight, and the statements before it are not in it). NV_OBSERVER_RECORDS names the folder of motion records (observer-motion-*.json: the
/// automation's motion reply with the samples of every avatar the viewer drew); NV_OBSERVER_OUT a file to write the per-record table to.
/// A statement is taken in the frame the avatar's update count rises, each frame is read at the time the avatar was placed at.
/// </summary>
public class ObserverReplayAid
{
    [ObserverRecordsFact]
    public void Another_avatars_recorded_drawing_against_the_official_replay_of_its_statements()
    {
        string folder = Environment.GetEnvironmentVariable("NV_OBSERVER_RECORDS")!;
        var I = CultureInfo.InvariantCulture;
        string? dumpOf = Environment.GetEnvironmentVariable("NV_OBSERVER_DUMP");   // a record's name: its frames (y drawn, y official) are written beside the table
        var dump = dumpOf is null ? null : new StringBuilder("t\tcount\tdrawn_y\tofficial_y\tgap\n");
        var table = new StringBuilder("record\tframes\tmax_all_m\tat_s\tmax_after_0.3s_m\n");
        foreach (var file in Directory.GetFiles(folder, "observer-motion-*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file).TrimStart('﻿'));
            var rows = doc.RootElement.GetProperty("samples").EnumerateArray().Where(s => !s[2].GetBoolean()).ToList();
            var replay = new OfficialOwnAvatarReplay();
            long lastCount = long.MinValue;
            double lastPlaced = double.NaN, first = double.NaN, worst = 0, worstAt = 0, worstLate = 0;
            int frames = 0;
            foreach (var s in rows)
            {
                double placed = s[17].GetDouble();
                long count = s[12].GetInt64();
                var stated = new Vector3(s[6].GetSingle(), s[7].GetSingle(), s[8].GetSingle());
                var velocity = new Vector3(s[9].GetSingle(), s[10].GetSingle(), s[11].GetSingle());
                double dt = double.IsNaN(lastPlaced) ? 0.016 : Math.Max(0, placed - lastPlaced);
                lastPlaced = placed;
                var drawnNow = new Vector3(s[3].GetSingle(), s[4].GetSingle(), s[5].GetSingle());
                if (lastCount == long.MinValue)
                {
                    // the record begins mid-flight: the first statement it shows was taken some time before the first sample. Its age is read
                    // from how far the avatar had been carried from it, and the replay starts from what was drawn then
                    double age = velocity.LengthSquared() > 1e-6f ? Math.Max(0, Vector3.Dot(drawnNow - stated, velocity) / velocity.LengthSquared()) : 0;
                    replay.SeedDrawn(drawnNow);
                    var origin0 = new Vector3(MathF.Floor(stated.X / 256f) * 256f, MathF.Floor(stated.Y / 256f) * 256f, 0);
                    replay.Statement(stated, velocity, origin0, 256f, placed - age, dt, 1f);
                    first = placed;
                    lastCount = count;
                }
                else if (count != lastCount)
                {
                    var origin = new Vector3(MathF.Floor(stated.X / 256f) * 256f, MathF.Floor(stated.Y / 256f) * 256f, 0);
                    replay.Statement(stated, velocity, origin, 256f, placed, dt, 1f);
                    lastCount = count;
                }
                if (double.IsNaN(first)) continue;
                var official = replay.Frame(placed, dt, 1f, _ => true);
                double gap = Vector3.Distance(drawnNow, official);
                if (dump is not null && Path.GetFileNameWithoutExtension(file) == dumpOf) dump.Append(I, $"{placed - first:0.000}\t{count}\t{drawnNow.Y:0.000}\t{official.Y:0.000}\t{gap:0.000}\n");
                frames++;
                if (gap > worst) { worst = gap; worstAt = placed - first; }
                if (placed - first >= 0.3 && gap > worstLate) worstLate = gap;
            }
            table.Append(I, $"{Path.GetFileNameWithoutExtension(file)}\t{frames}\t{worst:0.000}\t{worstAt:0.00}\t{worstLate:0.000}\n");
        }
        if (Environment.GetEnvironmentVariable("NV_OBSERVER_OUT") is { Length: > 0 } outPath) File.WriteAllText(outPath, table.ToString());
        if (dump is not null && Environment.GetEnvironmentVariable("NV_OBSERVER_OUT") is { Length: > 0 } o) File.WriteAllText(o + ".frames.tsv", dump.ToString());
        Assert.NotEmpty(table.ToString());
    }
}

/// <summary>A [Fact] that is a real SKIP, with its reason, unless NV_OBSERVER_RECORDS names a folder of motion records (they hold grid content and are never committed).</summary>
public sealed class ObserverRecordsFactAttribute : FactAttribute
{
    public ObserverRecordsFactAttribute()
    {
        if (!Directory.Exists(Environment.GetEnvironmentVariable("NV_OBSERVER_RECORDS") ?? ""))
            Skip = "development aid: NV_OBSERVER_RECORDS does not name a folder of motion records (they hold grid content and are never committed)";
    }
}
