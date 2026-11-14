// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using LibreMetaverse;
using Viewer.Core.Diagnostics;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;

namespace Viewer.Core.Tests;

/// <summary>
/// The region's built-in trees. They arrive as objects of kind tree (not prim) whose first data byte is the species; the world draws
/// each as a few prims built from the species' numbers, the root under the tree's own local id so a kill of the tree takes them all.
/// </summary>
public class TreeTests
{
    private static readonly SimRef Home = new(256000UL << 32 | 256000UL, "Home");
    private static readonly SimRef East = new(256256UL << 32 | 256000UL, "East");
    private static readonly Guid OakTexture = Guid.Parse("8a515889-eac9-fb55-8eba-d2dc09eb32c8");
    private static readonly Guid PineTexture = Guid.Parse("0187babf-6c0d-5891-ebed-4ecab1426683");

    private static TreeData Tree(uint local, int species, float scale = 1f) =>
        new(local, Guid.NewGuid(), species, new Vector3(100, 120, 25), Quaternion.Identity, new Vector3(scale, scale, scale));

    private static WorldState NewWorld()
    {
        var w = new WorldState(new CoreLog(new ListSink(), "test"));
        w.BeginRegion(Home, new RegionInfo("Home", 1000, 1000, 256, 256, true));
        return w;
    }

    [Fact]
    public void A_tree_is_one_mesh_prim_under_its_own_local_id()
    {
        var w = NewWorld();

        w.ApplyTree(Home, Tree(7, 1));

        var objects = w.Snapshot().Objects;
        Assert.True(objects.ContainsKey(7));
        Assert.Equal(0u, objects[7].ParentLocalId);
        Assert.Single(objects);
        Assert.Equal(Viewer.Core.Meshing.PrimShape.SculptTypeMesh, objects[7].Shape?.SculptType);
        Assert.True(TreeMesh.TryGet(objects[7].Shape!.SculptId, out _, out _, out _));
    }

    [Theory]
    [InlineData(0, "0187babf-6c0d-5891-ebed-4ecab1426683")]
    [InlineData(1, "8a515889-eac9-fb55-8eba-d2dc09eb32c8")]
    [InlineData(15, "8872f2b8-31db-42d8-580a-b3e4a91262de")]
    public void The_leaves_wear_the_species_texture(int species, string texture)
    {
        var w = NewWorld();

        w.ApplyTree(Home, Tree(7, species));

        Assert.Contains(w.Snapshot().Objects.Values, o => o.Textures?.Default.TextureId == Guid.Parse(texture));
    }

    [Fact]
    public void Every_species_builds_one_prim_with_a_mesh()
    {
        for (int species = 0; species <= 20; species++)
        {
            var w = NewWorld();
            w.ApplyTree(Home, Tree(7, species));
            int n = w.Snapshot().Objects.Count;
            Assert.Equal(1, n);
        }
    }

    [Fact]
    public void A_species_not_in_the_table_is_drawn_as_a_known_one()
    {
        var w = NewWorld();

        w.ApplyTree(Home, Tree(7, 200));

        Assert.Single(w.Snapshot().Objects);
        Assert.Contains(w.Snapshot().Objects.Values, o => o.Textures is not null && o.Textures.Default.TextureId != Guid.Empty);
    }

    [Fact]
    public void A_kill_of_the_tree_removes_every_part()
    {
        var w = NewWorld();
        w.ApplyTree(Home, Tree(7, 1));
        w.ApplyTree(Home, Tree(8, 0));
        int both = w.Snapshot().Objects.Count;

        w.Kill(Home, [7]);

        var left = w.Snapshot().Objects;
        Assert.DoesNotContain(left.Values, o => o.Id.LocalId == 7 || o.ParentLocalId == 7);
        Assert.Single(left);
        Assert.True(left.ContainsKey(8));
    }

    [Fact]
    public void A_second_update_replaces_the_parts_instead_of_adding_to_them()
    {
        var w = NewWorld();
        var tree = Tree(7, 1);
        w.ApplyTree(Home, tree);
        int first = w.Snapshot().Objects.Count;
        Assert.Equal(1, first);

        w.ApplyTree(Home, tree with { Scale = new Vector3(2, 2, 2) });

        Assert.Equal(first, w.Snapshot().Objects.Count);
        Assert.NotEqual(tree.Scale, w.Snapshot().Objects[7].Scale);   // rebuilt for the new scale
    }

    [Fact]
    public void The_tree_stands_on_its_place_and_grows_with_its_scale()
    {
        var small = NewWorld();
        var big = NewWorld();

        small.ApplyTree(Home, Tree(7, 1, 1f));
        big.ApplyTree(Home, Tree(7, 1, 2f));

        float Tallest(WorldState w) => w.Snapshot().Objects.Values.Select(o => Math.Max(o.Scale.X, Math.Max(o.Scale.Y, o.Scale.Z))).DefaultIfEmpty(0f).Max();
        Assert.True(Tallest(small) > 0f);
        Assert.Equal(2f, Tallest(big) / Tallest(small), 2);
        Assert.True(small.Snapshot().Objects.ContainsKey(7));
        var root = small.Snapshot().Objects[7];
        Assert.InRange(root.Position.X, 99f, 101f);   // the mesh is centred on its box, which stands on the tree place
        Assert.True(root.Position.Z > 25f);
    }

    [Fact]
    public void A_tree_belongs_to_the_region_that_sent_it()
    {
        var t = new FakeTransport();
        var s = new ViewerSession(new SessionOptions { LoginUri = "http://grid.example:8002/", Neighbours = true }, t, new CoreLog(new ListSink(), "test"));
        s.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw", SensitiveKind.PasswordDigest)), CancellationToken.None).GetAwaiter().GetResult();
        t.DriveToInWorld(Home);
        t.ConnectedSimulatorCount++;
        t.Raise(new SimConnected(East, 256, 256));

        t.Raise(new TreeUpdated(East, Tree(7, 1)));

        var east = s.Worlds.First(w => w.Sim == East).World;
        Assert.True(east.Snapshot().Objects.ContainsKey(7));
        Assert.Empty(s.World.Snapshot().Objects);
        s.Dispose();
    }

    [Theory]
    [InlineData(PCode.Tree)]
    [InlineData(PCode.NewTree)]
    public void The_adapter_reports_tree_objects_with_their_species(PCode kind)
    {
        var id = UUID.Random();
        var p = new Primitive
        {
            LocalID = 501,
            ID = id,
            Position = new LibreMetaverse.Vector3(1, 2, 3),
            Rotation = new LibreMetaverse.Quaternion(0, 0, 0.7071068f, 0.7071068f),
            Scale = new LibreMetaverse.Vector3(0.5f, 0.5f, 0.5f),
            TreeSpecies = (Tree)9,
            PrimData = new Primitive.ConstructionData { PCode = kind },
        };

        var d = LmvTransport.TreeFrom(p);

        Assert.NotNull(d);
        Assert.Equal(501u, d!.LocalId);
        Assert.Equal(id.Guid, d.FullId);
        Assert.Equal(9, d.Species);
        Assert.Equal(new Vector3(1, 2, 3), d.Position);
        Assert.Equal(new Vector3(0.5f, 0.5f, 0.5f), d.Scale);
    }

    [Fact]
    public void The_adapter_reports_nothing_for_a_prim()
    {
        var p = new Primitive { LocalID = 5, PrimData = new Primitive.ConstructionData { PCode = PCode.Prim } };

        Assert.Null(LmvTransport.TreeFrom(p));
    }

    [Fact]
    public void The_first_branches_of_a_tree_spread_round_the_trunk_each_leaning_out_by_the_droop_plus_25_degrees_not_all_towards_one_side()
    {
        // Oak: four branches, droop 35 degrees (the official viewer adds 25 degrees less the trunk's wind bend, which is none), twist 3 degrees.
        // genBranchPipeline turns each branch by 20 degrees about the trunk's axis, tips it over by the droop, and only then turns it about the axis
        // by (360 / branches +/- twist) * i, whose product order in its own quaternion class applies the first factor first. So the branches lean out
        // by 60 degrees towards azimuths 0, 87, 186 and 261 degrees; a tree whose branches all lean one way looks tilted as a whole.
        float radius = new Vector3(8, 8, 8).Length() * 0.05f;
        float firstBranchLength = 3f * 0.7f * radius;   // the oak's branch length times the scale step, in metres
        var directions = TreeMesh.Layout(1, radius).Segments
            .Where(s => MathF.Abs(s.Length - firstBranchLength) < 1e-3f)
            .Select(s => Vector3.Transform(Vector3.UnitZ, s.Rotation))
            .ToList();
        Assert.Equal(4, directions.Count);
        float[] azimuths = [0f, 87f, 186f, 261f];
        for (int i = 0; i < 4; i++)
        {
            float tilt = MathF.Acos(Math.Clamp(directions[i].Z, -1f, 1f)) * 180f / MathF.PI;
            float azimuth = (MathF.Atan2(directions[i].Y, directions[i].X) * 180f / MathF.PI + 360f) % 360f;
            Assert.InRange(tilt, 59f, 61f);
            float diff = MathF.Abs(azimuth - azimuths[i]);
            Assert.True(MathF.Min(diff, 360f - diff) < 1.5f, $"branch {i}: azimuth {azimuth}, expected {azimuths[i]}");
        }
    }
}
