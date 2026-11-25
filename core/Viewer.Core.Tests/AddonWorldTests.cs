// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Addons;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Viewer.Shell.Addons;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// What interface 0.6 adds: reading the world (permission region.read, built-in add-ons only) with its snapshot, ground, wind and
/// events, the grid declaration (undeclared means off on Second Life), and the slider. A fake session's world state and recording
/// services; pure, runs in parallel.
/// </summary>
public class AddonWorldTests
{
    private static readonly SimRef Home = new(1000UL * 256 << 32 | 1000UL * 256, "Home");

    private sealed class Services : IAddonServices
    {
        public AddonWorldSnapshot? World;
        public double Now => 0;
        public void SendAnimations(IReadOnlyList<(Guid Animation, bool Start)> requests) { }
        public bool Say(string text) => false;
        public IAddonInventory? Inventory => null;
        public AddonWorldSnapshot? ReadWorld() => World;
        public float? GroundHeight(float x, float y) => x + y;
        public Vector3? WindAt(float x, float y) => new Vector3(x, y, 0);
    }

    private sealed class Probe : IAddon
    {
        public IAddonContext? Context;
        public Action<IAddonContext>? OnStart;
        public void Start(IAddonContext context) { Context = context; OnStart?.Invoke(context); }
        public void Stop() { }
    }

    private static AddonWorldSnapshot Sample(string region = "Home") =>
        new(region, 256, 256, new AddonCameraPose(new Vector3(1, 2, 3), Vector3.UnitY, Vector3.UnitZ),
            new Vector3(4, 5, 6), Vector3.Zero, 20f, false, Vector3.UnitZ, new AddonEnvironment("region", "region", false, 0.25f));

    private static AddonManifest Manifest(string[] permissions, params GridKind[] grids) =>
        new("probe", "Probe", "1.0", "Someone", "MIT", "0.6", null, permissions, "probe.dll") { Grids = grids.Length == 0 ? null : grids };

    private static (AddonHost Host, Services Services, Probe Probe, AddonEntry Entry) Start(bool builtIn, string[] permissions, params GridKind[] grids)
    {
        var services = new Services { World = Sample() };
        var host = new AddonHost(services, new CoreLog(new ListSink(), "test"), null);
        var probe = new Probe();
        var entry = host.Register(Manifest(permissions, grids), () => probe, builtIn);
        host.StartEnabled();
        if (!builtIn)
            host.SetEnabled("probe", true, grantPermissions: true);
        return (host, services, probe, entry);
    }

    // ---------------- the permission ----------------

    [Fact]
    public void Reading_the_world_needs_region_read()
    {
        var (_, _, probe, _) = Start(builtIn: true, [AddonPermissions.Window]);
        var refused = Assert.Throws<AddonPermissionException>(() => probe.Context!.World);
        Assert.Equal(AddonPermissions.RegionRead, refused.Permission);
    }

    [Fact]
    public void An_installed_add_on_is_refused_region_read_even_when_the_user_granted_it()
    {
        var (_, _, probe, entry) = Start(builtIn: false, [AddonPermissions.RegionRead]);
        Assert.Contains(AddonPermissions.RegionRead, entry.Granted);
        Assert.Throws<AddonPermissionException>(() => probe.Context!.World);
    }

    [Fact]
    public void An_installed_add_on_that_reads_the_world_is_switched_off_with_a_note()
    {
        var services = new Services { World = Sample() };
        var host = new AddonHost(services, new CoreLog(new ListSink(), "test"), null);
        var probe = new Probe { OnStart = c => c.World!.Snapshot() };
        var entry = host.Register(Manifest([AddonPermissions.RegionRead]), () => probe, builtIn: false);
        host.StartEnabled();
        host.SetEnabled("probe", true, grantPermissions: true);
        Assert.False(entry.Running);
        Assert.Contains("region.read", entry.Note ?? "");
    }

    [Fact]
    public void A_built_in_add_on_with_region_read_reads_the_snapshot_ground_and_wind_while_logged_in()
    {
        var (host, services, probe, _) = Start(builtIn: true, [AddonPermissions.RegionRead]);
        var world = probe.Context!.World;
        Assert.NotNull(world);
        Assert.Null(world.Snapshot());   // not logged in yet
        host.LoggedIn(GridKind.OpenSim);
        Assert.Same(services.World, world.Snapshot());
        Assert.Equal(7f, world.GroundHeight(3, 4));
        Assert.Equal(new Vector3(3, 4, 0), world.WindAt(3, 4));
        host.LoggedOut();
        Assert.Null(world.Snapshot());
        Assert.Null(world.GroundHeight(3, 4));
        Assert.Null(world.WindAt(3, 4));
    }

    [Fact]
    public void A_world_handle_kept_past_the_add_ons_stop_reads_nothing()
    {
        var (host, _, probe, _) = Start(builtIn: true, [AddonPermissions.RegionRead]);
        var world = probe.Context!.World!;
        host.LoggedIn(GridKind.OpenSim);
        Assert.NotNull(world.Snapshot());
        host.SetEnabled("probe", false);
        Assert.Null(world.Snapshot());
        Assert.Null(world.GroundHeight(1, 1));
        Assert.Null(world.WindAt(1, 1));
    }

    // ---------------- events ----------------

    private static (AddonHost Host, List<string> Heard) Listening(GridKind grid, params GridKind[] declared)
    {
        var (host, _, probe, _) = Start(builtIn: true, [AddonPermissions.RegionRead], declared);
        var heard = new List<string>();
        var world = probe.Context!.World!;
        world.RegionChanged += r => heard.Add("region " + r);
        world.Teleported += r => heard.Add("teleport " + r);
        probe.Context.Tick += () => heard.Add("tick");
        host.LoggedIn(grid);
        return (host, heard);
    }

    private static void Frame(AddonHost host, string region, ulong handle)
    {
        host.InRegion(region, handle);
        host.Frame();
    }

    [Fact]
    public void The_first_region_raises_nothing_and_each_change_raises_one_region_event()
    {
        var (host, heard) = Listening(GridKind.OpenSim);
        Frame(host, "Home", 1);
        Frame(host, "Home", 1);
        Frame(host, "Next", 2);
        Frame(host, "Next", 2);
        Frame(host, "Next", 3);   // another place with the same name is another region
        Assert.Equal(["tick", "tick", "region Next", "tick", "tick", "region Next", "tick"], heard);
    }

    [Fact]
    public void A_teleport_to_another_region_raises_one_region_event_then_one_teleport_event()
    {
        var (host, heard) = Listening(GridKind.OpenSim);
        Frame(host, "Home", 1);
        heard.Clear();
        host.TeleportArrived();
        Frame(host, "Far", 9);
        Frame(host, "Far", 9);
        Assert.Equal(["region Far", "teleport Far", "tick", "tick"], heard);
    }

    [Fact]
    public void A_teleport_inside_the_region_raises_only_the_teleport_event_and_two_teleports_raise_two()
    {
        var (host, heard) = Listening(GridKind.OpenSim);
        Frame(host, "Home", 1);
        heard.Clear();
        host.TeleportArrived();
        Frame(host, "Home", 1);
        host.TeleportArrived();
        host.TeleportArrived();
        Frame(host, "Home", 1);
        Assert.Equal(["teleport Home", "tick", "teleport Home", "teleport Home", "tick"], heard);
    }

    [Fact]
    public void Logging_out_forgets_the_region_and_a_teleport_not_yet_told()
    {
        var (host, heard) = Listening(GridKind.OpenSim);
        Frame(host, "Home", 1);
        host.TeleportArrived();
        host.LoggedOut();
        host.LoggedIn(GridKind.OpenSim);
        heard.Clear();
        Frame(host, "Next", 2);
        Assert.Equal(["tick"], heard);
    }

    [Fact]
    public void A_teleport_is_told_when_the_region_it_ended_in_is_told_not_by_the_frame()
    {
        var (host, heard) = Listening(GridKind.OpenSim);
        Frame(host, "Home", 1);
        heard.Clear();
        host.TeleportArrived();
        host.Frame();   // the add-ons' frame runs before the avatar is placed
        Assert.Equal(["tick"], heard);
        host.InRegion("Home", 1);
        Assert.Equal(["tick", "teleport Home"], heard);
    }

    [Fact]
    public void A_region_whose_name_arrives_late_is_the_same_region()
    {
        var (host, heard) = Listening(GridKind.OpenSim);
        Frame(host, "", 1);
        Frame(host, "Home", 1);
        Frame(host, "", 1);
        Frame(host, "Home", 1);
        Assert.DoesNotContain(heard, h => h.StartsWith("region", StringComparison.Ordinal));
        heard.Clear();
        host.TeleportArrived();
        host.InRegion("Home", 1);
        Assert.Equal(["teleport Home"], heard);
        heard.Clear();
        host.InRegion("", 2);        // another place, not named yet: told once its name is known
        host.InRegion("Next", 2);
        Assert.Equal(["region Next"], heard);
    }

    [Fact]
    public void A_teleport_to_a_region_not_named_yet_waits_for_the_name()
    {
        var (host, heard) = Listening(GridKind.OpenSim);
        Frame(host, "Home", 1);
        heard.Clear();
        host.TeleportArrived();
        host.InRegion("", 2);
        Assert.Empty(heard);
        host.InRegion("Far", 2);
        Assert.Equal(["region Far", "teleport Far"], heard);
    }

    [Fact]
    public void A_manifest_for_an_interface_before_0_6_may_carry_any_grids_key_and_it_is_ignored()
    {
        string old = Json(""", "grids": "anything" """).Replace("\"0.6\"", "\"0.5\"");
        var manifest = AddonManifest.Parse(old, out var problems);
        Assert.Empty(problems);
        Assert.NotNull(manifest);
        Assert.Null(manifest.Grids);
    }

    [Fact]
    public void A_new_region_starts_with_no_wind_of_the_one_before()
    {
        var world = FakeWorld(Vector3.Zero);
        Assert.NotNull(AddonWorldReader.WindAt(world, 20, 5));
        var next = new SimRef(1000UL * 256 << 32 | 1001UL * 256, "Next");
        world.BeginRegion(next, new RegionInfo("Next", next.GridX, next.GridY, 256, 256, true));
        Assert.Null(AddonWorldReader.WindAt(world, 20, 5));
    }

    [Theory]
    [InlineData(10, -1, 9)]    // from the far end, which is off the steps, down to the last step
    [InlineData(9, 1, 10)]     // and back up to the far end
    [InlineData(9, -1, 6)]
    [InlineData(6, 1, 9)]
    [InlineData(0, -1, 0)]
    [InlineData(10, 1, 10)]
    public void An_arrow_key_moves_a_slider_to_the_next_value_it_can_show(double from, int direction, double to)
    {
        var slider = new AddonSlider("s", 0, 10, 3) { Value = from };
        Assert.Equal(from, slider.Value);
        Assert.Equal(to, AddonSliderSteps.Next(slider, direction));
    }

    [Fact]
    public void An_arrow_key_on_decimal_steps_gives_clean_values()
    {
        var slider = new AddonSlider("s", 0, 1, 0.1) { Value = 0.3 };
        Assert.Equal(0.4, AddonSliderSteps.Next(slider, 1));
        Assert.Equal(0.2, AddonSliderSteps.Next(slider, -1));
    }

    // ---------------- the grid declaration ----------------

    [Fact]
    public void A_feature_that_declares_no_grid_is_off_on_second_life_and_a_declaration_says_exactly_where()
    {
        Assert.False(GridDeclaration.Allows(null, GridKind.SecondLife));
        Assert.True(GridDeclaration.Allows(null, GridKind.OpenSim));
        Assert.False(GridDeclaration.Allows([GridKind.OpenSim], GridKind.SecondLife));
        Assert.True(GridDeclaration.Allows([GridKind.OpenSim, GridKind.SecondLife], GridKind.SecondLife));
        Assert.False(GridDeclaration.Allows([GridKind.SecondLife], GridKind.OpenSim));
        Assert.True(GridDeclaration.Allows([GridKind.SecondLife], GridKind.SecondLife));
    }

    private static string Json(string extra) =>
        """{ "id": "x", "name": "x", "version": "1", "author": "a", "licence": "l", "interface": "0.6", "library": "x.dll" """ + extra + " }";

    [Fact]
    public void A_manifest_declares_its_grids_and_a_bad_declaration_is_refused()
    {
        Assert.Null(AddonManifest.Parse(Json(""), out _)!.Grids);
        Assert.Equal([GridKind.OpenSim, GridKind.SecondLife], AddonManifest.Parse(Json(""", "grids": ["opensim", "secondlife"]"""), out _)!.Grids);
        foreach (string bad in new[] { """["mars"]""", "[]", "\"opensim\"", "[1]" })
        {
            Assert.Null(AddonManifest.Parse(Json($""", "grids": {bad}"""), out var problems));
            Assert.Contains(problems, p => p.Contains("'grids'"));
        }
    }

    [Fact]
    public void On_second_life_an_add_on_that_declares_nothing_reads_nothing_and_hears_no_world_event()
    {
        var (host, services, probe, entry) = Start(builtIn: true, [AddonPermissions.RegionRead]);
        var heard = new List<string>();
        var world = probe.Context!.World!;
        world.RegionChanged += r => heard.Add(r);
        world.Teleported += r => heard.Add(r);
        host.LoggedIn(GridKind.SecondLife);
        Assert.Null(world.Snapshot());
        Assert.Null(world.GroundHeight(1, 1));
        Assert.Null(world.WindAt(1, 1));
        Frame(host, "Home", 1);
        host.TeleportArrived();
        Frame(host, "Next", 2);
        Assert.Empty(heard);
        Assert.True(entry.Running);   // off there, not at fault: it stays on
    }

    [Fact]
    public void On_second_life_an_add_on_that_declares_it_reads_the_world_and_hears_the_events()
    {
        var (host, heard) = Listening(GridKind.SecondLife, GridKind.OpenSim, GridKind.SecondLife);
        Frame(host, "Home", 1);
        host.TeleportArrived();
        Frame(host, "Next", 2);
        Assert.Equal(["tick", "region Next", "teleport Next", "tick"], heard);
    }

    [Fact]
    public void A_declaration_that_leaves_out_the_grid_in_use_is_off_there()
    {
        var (host, _, probe, _) = Start(builtIn: true, [AddonPermissions.RegionRead], GridKind.SecondLife);
        host.LoggedIn(GridKind.OpenSim);
        Assert.Null(probe.Context!.World!.Snapshot());
    }

    // ---------------- values from a fake session's world ----------------

    /// <summary>A 256 m region lying <paramref name="offset"/> from the anchor: land at 10 + x/16 patch columns, water at 20 m.</summary>
    private static WorldState FakeWorld(Vector3 offset, bool wind = true, bool patches = true)
    {
        var w = new WorldState(new CoreLog(new ListSink(), "test"));
        w.SetOriginOffset(offset);
        w.BeginRegion(Home, new RegionInfo("Home", Home.GridX, Home.GridY, 256, 256, true) { WaterHeight = 20f });
        if (patches)
            for (int py = 0; py < 16; py++)
                for (int px = 0; px < 16; px++)
                    if (!(px == 15 && py == 15))
                        w.ApplyPatch(Home, px, py, 16, Enumerable.Repeat(10f + px, 256).ToArray());
        if (wind)
        {
            var x = new float[256];
            var y = new float[256];
            x[1] = 3f;    // the cell east of the south-west one
            y[1] = -4f;
            w.Wind = new RegionWind(x, y);
        }
        return w;
    }

    [Fact]
    public void The_snapshot_gives_the_camera_and_avatar_in_the_regions_own_metres()
    {
        var offset = new Vector3(256, 512, 0);
        var view = new AddonViewpoint(offset + new Vector3(10, 20, 30), new Vector3(0, 2, 0), new Vector3(0, 0, 3), offset + new Vector3(40, 50, 25), new Vector3(1, 0, 0));
        var sun = new RegionEnvironmentFrame().Frame(new Vector3(0, 3, 4));
        var s = AddonWorldReader.Read(FakeWorld(offset), view, sun, new AddonEnvironment("region", "region", false, 0.5f));
        Assert.NotNull(s);
        Assert.Equal(("Home", 256, 256), (s.RegionName, s.RegionSizeX, s.RegionSizeY));
        Assert.Equal(new Vector3(10, 20, 30), s.Camera.Position);
        Assert.Equal(new Vector3(0, 1, 0), s.Camera.Forward);
        Assert.Equal(new Vector3(0, 0, 1), s.Camera.Up);
        Assert.Equal(new Vector3(40, 50, 25), s.AgentPosition);
        Assert.Equal(new Vector3(1, 0, 0), s.AgentVelocity);
        Assert.Equal(20f, s.WaterHeight);
        Assert.False(s.CameraUnderWater);
        Assert.Equal(0.6f, s.SunDirection!.Value.Y, 4);
        Assert.Equal(0.8f, s.SunDirection!.Value.Z, 4);
        Assert.Equal(0.5f, s.Environment!.CloudShadow);
    }

    [Fact]
    public void The_camera_is_under_water_only_below_the_water_height_and_never_without_one()
    {
        var world = FakeWorld(Vector3.Zero);
        AddonViewpoint At(float z) => new(new Vector3(10, 10, z), Vector3.UnitY, Vector3.UnitZ, new Vector3(10, 10, z), Vector3.Zero);
        Assert.Equal(true, AddonWorldReader.Read(world, At(19.9f), null, null)?.CameraUnderWater);
        Assert.Equal(false, AddonWorldReader.Read(world, At(20.1f), null, null)?.CameraUnderWater);
        var dry = new WorldState(new CoreLog(new ListSink(), "test"));
        dry.BeginRegion(Home, new RegionInfo("Home", Home.GridX, Home.GridY, 256, 256, true));
        var s = AddonWorldReader.Read(dry, At(-5), null, null);
        Assert.NotNull(s);
        Assert.Null(s.WaterHeight);
        Assert.False(s.CameraUnderWater);
        Assert.Null(s.SunDirection);
        Assert.Null(s.Environment);
    }

    [Fact]
    public void There_is_no_snapshot_of_a_world_without_a_region()
    {
        Assert.Null(AddonWorldReader.Read(new WorldState(new CoreLog(new ListSink(), "test")), default, null, null));
        Assert.NotNull(AddonWorldReader.Read(FakeWorld(Vector3.Zero), default, null, null));
    }

    [Fact]
    public void The_ground_is_read_inside_the_region_where_it_has_arrived_and_is_unknown_elsewhere()
    {
        var world = FakeWorld(new Vector3(256, 0, 0));
        Assert.Equal(10f, AddonWorldReader.GroundHeight(world, 5, 5));
        Assert.Equal(12f, AddonWorldReader.GroundHeight(world, 40, 100));
        Assert.Null(AddonWorldReader.GroundHeight(world, -1, 5));
        Assert.Null(AddonWorldReader.GroundHeight(world, 256, 5));
        Assert.Null(AddonWorldReader.GroundHeight(world, float.NaN, 5));
        Assert.Null(AddonWorldReader.GroundHeight(world, 250, 250));   // the patch that never came
        Assert.Null(AddonWorldReader.GroundHeight(FakeWorld(Vector3.Zero, patches: false), 5, 5));
    }

    [Fact]
    public void The_wind_is_the_regions_cell_at_the_place_and_unknown_outside_it_or_before_any_wind()
    {
        var world = FakeWorld(new Vector3(256, 0, 0));
        Assert.Equal(new Vector3(3, -4, 0), AddonWorldReader.WindAt(world, 20, 5));
        Assert.Equal(Vector3.Zero, AddonWorldReader.WindAt(world, 5, 5));
        Assert.Null(AddonWorldReader.WindAt(world, 300, 5));
        Assert.Null(AddonWorldReader.WindAt(world, 5, -1));
        Assert.Null(AddonWorldReader.WindAt(FakeWorld(Vector3.Zero, wind: false), 20, 5));
    }

    [Fact]
    public void The_summary_names_the_source_the_time_of_day_choice_night_and_cloud_shadow()
    {
        var day = new RegionEnvironmentFrame().Frame(new Vector3(0, 0.6f, 0.8f)) with { CloudShadow = 0.4f };
        var night = new RegionEnvironmentFrame().Frame(new Vector3(0, 0.6f, -0.8f)) with { CloudShadow = 7f };
        Assert.Equal(new AddonEnvironment("region", "region", false, 0.4f), AddonWorldReader.Summary("region", SkyChoice.Region, day));
        Assert.Equal(new AddonEnvironment("parcel", "midnight", true, 1f), AddonWorldReader.Summary("parcel", SkyChoice.Midnight, night));
        Assert.Equal("default", AddonWorldReader.Summary("default: none served", SkyChoice.Region, day).Source);
        Assert.Equal("legacymidday", AddonWorldReader.Summary(null, SkyChoice.LegacyMidday, day).Sky);
        Assert.Equal("default", AddonWorldReader.Summary(null, SkyChoice.Region, day).Source);
    }

    private sealed class RegionEnvironmentFrame
    {
        public SkyFrame Frame(Vector3 sun) => RegionEnvironment.Default.Sky[0] with { SunDirection = sun };
    }

    // ---------------- the slider ----------------

    [Fact]
    public void A_slider_keeps_its_value_in_range_and_on_its_steps()
    {
        var s = new AddonSlider("s", 1, 10, 2);   // 1, 3, 5, 7, 9 and the far end, 10
        Assert.Equal(1, s.Value);
        s.Value = 4.2;
        Assert.Equal(5, s.Value);
        s.Value = -100;
        Assert.Equal(1, s.Value);
        s.Value = 100;
        Assert.Equal(10, s.Value);
        s.Value = 8.9;
        Assert.Equal(9, s.Value);
        s.Value = 9.8;
        Assert.Equal(10, s.Value);
        foreach (double bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            s.Value = bad;
            Assert.Equal(10, s.Value);
        }
    }

    [Fact]
    public void Decimal_steps_give_clean_values()
    {
        var s = new AddonSlider("s", 0, 1, 0.1);
        s.Value = 0.3000001;
        Assert.Equal(0.3, s.Value);
        s.Value = 0.69;
        Assert.Equal(0.7, s.Value);
    }

    [Fact]
    public void The_user_moving_a_slider_is_told_once_for_each_new_value()
    {
        var s = new AddonSlider("s", 0, 10, 1);
        var moved = new List<double>();
        int redraws = 0;
        s.Moved += moved.Add;
        s.Changed += () => redraws++;
        s.Slide(2.2);
        s.Slide(1.9);
        s.Slide(double.NaN);
        s.Slide(50);
        s.Slide(11);
        Assert.Equal([2, 10], moved);
        Assert.Equal(0, redraws);   // the viewer drew what the user moved
        s.Value = 3;
        Assert.Equal(1, redraws);
        Assert.Equal([2, 10], moved);   // the add-on's own change is not the user's
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(5, 1, 1)]
    [InlineData(0, 10, 0)]
    [InlineData(0, 10, -1)]
    [InlineData(double.NaN, 10, 1)]
    [InlineData(0, double.PositiveInfinity, 1)]
    [InlineData(0, 10, double.NaN)]
    public void A_slider_with_no_usable_range_is_refused(double min, double max, double step)
    {
        Assert.Throws<ArgumentException>(() => new AddonSlider("s", min, max, step));
    }

    // ---------------- what stays as it was ----------------

    private static string RepoFile(params string[] parts)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Viewer.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        return Path.Combine([root.FullName, .. parts]);
    }

    [Fact]
    public void The_interface_is_0_6_and_the_add_ons_that_came_before_still_load_asking_for_what_they_asked_for()
    {
        Assert.Equal("0.6", AddonInterface.Version);
        Assert.False(AddonManifest.Supports("0.7"));
        foreach (var (folder, version, count) in new[] { ("Viewer.Addon.Ao", "0.5", 6), ("Viewer.Addon.Rlv", "0.4", 3) })
        {
            var manifest = AddonManifest.Parse(File.ReadAllText(RepoFile("addons", folder, AddonManifest.FileName)), out var problems);
            Assert.Empty(problems);
            Assert.Equal(version, manifest!.Interface);
            Assert.DoesNotContain(AddonPermissions.RegionRead, manifest.Permissions);
            Assert.Null(manifest.Grids);
            Assert.True(manifest.Permissions.Count >= count, folder);
        }
        var hello = AddonManifest.Parse(File.ReadAllText(RepoFile("samples", "HelloAddon", AddonManifest.FileName)), out _);
        Assert.Equal("0.1", hello!.Interface);
    }

    // ---------------- the sample ----------------

    private static string WorldSampleFolder(string into)
    {
        var bin = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        string output = RepoFile("samples", "WorldSample", "bin", bin.Parent!.Name, bin.Name);
        string folder = Path.Combine(into, "world-sample");
        Directory.CreateDirectory(folder);
        foreach (string file in new[] { "WorldSample.dll", AddonManifest.FileName })
            File.Copy(Path.Combine(output, file), Path.Combine(folder, file));
        return folder;
    }

    [Fact]
    public void The_world_sample_shows_position_water_and_wind_and_counts_one_teleport_as_one()
    {
        string root = TestTemp.NewFolder("addon-world-sample");
        try
        {
            WorldSampleFolder(root);
            var services = new Services { World = Sample() };
            var host = new AddonHost(services, new CoreLog(new ListSink(), "test"), null);
            Assert.Equal(1, host.Discover(root, builtIn: true));
            host.StartEnabled();
            var entry = Assert.Single(host.Addons);
            Assert.False(entry.Running);   // off until chosen
            Assert.True(host.SetEnabled("world-sample", true));
            host.LoggedIn(GridKind.OpenSim);
            Frame(host, "Home", 1);
            var window = Assert.Single(host.Windows).Window;
            string Text(string id) => window.Controls.Single(c => c.Id == id).Text;
            Assert.Equal("Region: Home, 256 x 256 m", Text("region"));
            Assert.Equal("Position: 4.0, 5.0, 6.0", Text("position"));
            Assert.Equal("Water: 20.0 m (camera above)", Text("water"));
            Assert.Equal("Wind: 4.0, 5.0 m/s", Text("wind"));
            Assert.Equal("Teleports: 0", Text("teleports"));
            Assert.Single(window.Controls.OfType<AddonSlider>());
            host.TeleportArrived();
            Frame(host, "Home", 1);
            Assert.Equal("Teleports: 1", Text("teleports"));
        }
        finally { TestTemp.Delete(root); }
    }
}
