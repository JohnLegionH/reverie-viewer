// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Meshing;
using Viewer.Core.Replay;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>Prims' shape and legacy colours travel through the core (world state, captures) and the zoo fixture.</summary>
public class PrimWorldTests
{
    public static string ZooPath => Path.Combine(TestPaths.RepoRoot, "fixtures", "prims", "prim-zoo.primzoo.json");

    [Fact]
    public void The_committed_prim_zoo_matches_its_generator()
    {
        string expected = PrimZoo.Serialise(PrimZoo.Make());
        if (Environment.GetEnvironmentVariable("NV_WRITE_PRIM_ZOO") == "1")
            File.WriteAllText(ZooPath, expected);   // proposal only; goldens are approved by a person after viewing
        Assert.Equal(expected, File.ReadAllText(ZooPath).Replace("\r\n", "\n"));
        var zoo = PrimZoo.Parse(File.ReadAllText(ZooPath));
        Assert.Equal(PrimZoo.Shapes.Count + 3, zoo.Count);
        foreach (var name in new[] { "box", "cylinder", "prism", "sphere", "torus", "tube", "ring" })
            Assert.Contains(zoo, p => p.Name == name);
        Assert.Equal(2, zoo.Count(p => p.Parent != 0));                                  // a linkset
        Assert.Contains(zoo, p => p.Colors.Default.A < 1);                                // semi-transparent prim
        Assert.Contains(zoo, p => p.Colors[1].A < 1 && p.Colors[0].A == 1);                // one semi-transparent face
        Assert.Contains(zoo, p => p.Colors.OverrideCount == 6);                           // per-face colours
        Assert.Equal(PrimZoo.Make().Select(p => p.Shape), zoo.Select(p => p.Shape));
    }

    [Fact]
    public void Shape_and_colours_reach_the_world_state_and_a_duplicate_update_is_still_a_no_op()
    {
        using var w = new WorldState(new CoreLog(new ListSink(), "t"));
        var sim = new object();
        w.BeginRegion(sim, new RegionInfo("r", 1, 1, 256, 256, true));
        var changes = new List<WorldChange>();
        w.Changed += changes.Add;
        var zoo = PrimZoo.AsPrimData(PrimZoo.Make()).ToList();
        foreach (var p in zoo) w.ApplyObject(sim, p);
        foreach (var p in zoo) w.ApplyObject(sim, p with { Colors = new FaceColors(p.Colors!.Default, p.Colors.Overrides) });   // equal, new instances
        Assert.Equal(zoo.Count, changes.Count);
        var box = w.Snapshot().Objects.Values.Single(o => o.Shape == PrimShape.Box && o.Colors!.OverrideCount == 6);
        Assert.Equal(PrimZoo.Make()[0].Colors[3], box.Colors![3]);
        w.ApplyObject(sim, zoo[0] with { Colors = new FaceColors(new Rgba(0, 0, 0, 1)) });
        Assert.Equal(zoo.Count + 1, changes.Count);
    }

    [Fact]
    public void Captures_carry_shape_and_colours_and_old_captures_read_as_null()
    {
        var p = PrimZoo.AsPrimData(PrimZoo.Make()).First(x => x.Colors!.OverrideCount == 6);
        var sim = new SimRef(1UL << 32 | 1UL, "zoo");
        string path = Path.Combine(TestPaths.TestOutputDir, "nv5-prim-capture.jsonl.gz");
        using (var wr = new CaptureFile.Writer(path, new CaptureHeader(CaptureFile.FormatVersion, "t", Guid.Empty, 0, 0, "test")))
            wr.Write(new CaptureRecord(0, null, 1, new ObjectUpdated(sim, p, false, null)));
        var back = (ObjectUpdated)CaptureFile.Read(path).Records.Single().Event;
        Assert.Equal(p.Shape, back.Prim.Shape);
        Assert.Equal(p.Colors, back.Prim.Colors);
        // A pre- record (no shape, no colours) still reads, with both null.
        string text = CaptureFile.ReadText(path);
        Assert.Contains("\"shape\":", text);
        var old = text.Replace(text[text.IndexOf(",\"shape\":", StringComparison.Ordinal)..text.LastIndexOf('}', text.LastIndexOf('}') - 1)], "");
        var parsed = CaptureFile.Parse(old);
        var o = (ObjectUpdated)parsed.Records.Single().Event;
        Assert.Null(o.Prim.Shape);
        Assert.Null(o.Prim.Colors);
    }
}
