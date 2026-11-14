// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Map;
using Viewer.Core.Session;
using Viewer.Core.Social;
using Viewer.Shell;
using Viewer.Shell.Travel;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The travel hub: events queued from any thread reach the teleport model and the world map on the main thread, the map and mini-map
/// settings are read and kept, and the tiles are asked for through the map's links. No grid, no engine. Every member builds its own
/// objects, so the class runs in parallel.
/// </summary>
public class TravelHubShellTests
{
    private static readonly SimRef There = new(300000UL * 256 << 32 | 300000UL * 256, "Far Region");
    private static readonly Guid Self = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Friend = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abcd");

    private sealed class MemoryStore : ISettingsStore
    {
        public string? Text;
        public string? Read() => Text;
        public bool Write(string text) { Text = text; return true; }
    }

    private static (TravelHub Hub, FakeTransport Fake, SettingsService Settings, MemoryStore Store, MapLinks Links) New(MemoryStore? store = null)
    {
        store ??= new MemoryStore();
        var fake = new FakeTransport();
        var pipeline = new ActionPipeline(() => fake, () => true, new CoreLog(new ListSink(), "test"));
        var settings = new SettingsService(store);
        var links = new MapLinks();
        var hub = new TravelHub(Self, new NameCache(), pipeline.Submit, settings, () => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), links);
        return (hub, fake, settings, store, links);
    }

    [Fact]
    public void Events_queued_from_another_thread_are_handled_only_when_the_main_thread_pumps()
    {
        var (hub, _, _, _, _) = New();
        Task.Run(() =>
        {
            hub.Enqueue(new TeleportOffered(Friend, "Friendly Resident", Guid.NewGuid(), new MessageBody("come", MessageChannel.InstantMessage)));
            hub.Enqueue(new MapBlockSize(1000, 1000, 256, 256, "Example Region", Guid.NewGuid(), 13));
        }).Wait();
        Assert.Empty(hub.Teleport.Offers);                     // nothing is touched off the main thread
        Assert.Empty(hub.WorldMap.Regions);
        Assert.Equal(2, hub.Pump());
        Assert.Single(hub.Teleport.Offers);
        Assert.Single(hub.WorldMap.Regions);
        Assert.Equal(0, hub.Pump());
    }

    [Fact]
    public void A_teleport_runs_from_the_pipeline_through_progress_and_arrival_using_only_queued_events()
    {
        var (hub, fake, _, _, _) = New();
        Assert.True(hub.Teleport.Home());
        Assert.IsType<TeleportHomeCommand>(Assert.Single(fake.Commands));
        hub.Enqueue(new TeleportProgressed(TeleportPhase.Started, "", true));
        hub.Enqueue(new TeleportProgressed(TeleportPhase.Progress, "sending_home", true));
        hub.Pump();
        Assert.Equal(("teleport.progress.sending_home", null), hub.Teleport.Progress);
        hub.Enqueue(new SimConnected(There, 256, 256));
        hub.Enqueue(new MovementComplete(There));
        hub.Pump();
        Assert.Equal(TeleportStage.Arriving, hub.Teleport.Stage);   // the progress stays up for the arrival delay (the model's tests move the clock)
        Assert.Equal(1, hub.Teleport.Arrivals);
    }

    [Fact]
    public void The_map_teleports_through_the_same_model_so_one_teleport_at_a_time_holds_there_too()
    {
        var (hub, fake, _, _, links) = New();
        hub.Enqueue(new MapBlockSize(1001, 1001, 256, 256, "Example Region", null, 13));
        hub.Pump();
        hub.WorldMap.CentreOnMetres(1001 * 256 + 128, 1001 * 256 + 128);
        Assert.True(hub.WorldMap.Click(400, 300, 800, 600));
        Assert.True(hub.WorldMap.TeleportToSelected());
        var command = Assert.IsType<TeleportHandleCommand>(Assert.Single(fake.Commands));
        Assert.Equal(MapMath.HandleOf(1001, 1001), command.RegionHandle);
        Assert.True(hub.Teleport.Busy);
        Assert.False(hub.WorldMap.TeleportToSelected());       // the second one is refused by the teleport model
        Assert.False(hub.WorldMap.TeleportHome());
        Assert.Single(fake.Commands);
        Assert.NotNull(links.Teleport);
    }

    [Fact]
    public void The_map_and_mini_map_settings_default_to_the_official_values_and_are_kept_when_changed()
    {
        var (hub, _, settings, store, _) = New();
        Assert.Equal(128, hub.WorldMap.PixelsPerRegion);
        Assert.Equal(128, hub.MiniMap.PixelsPerRegion);
        Assert.True(hub.MiniMap.Rotate);
        hub.MiniMap.SetScale(256);
        hub.MiniMap.Rotate = false;
        hub.WorldMap.SetScale(64);
        Assert.Equal(256, settings.Get(Settings.MiniMapScale));
        Assert.False(settings.Get(Settings.MiniMapRotate));
        Assert.Equal(64, settings.Get(Settings.WorldMapScale));
        var again = New(store);
        Assert.Equal(256, again.Hub.MiniMap.PixelsPerRegion);
        Assert.False(again.Hub.MiniMap.Rotate);
        Assert.Equal(64, again.Hub.WorldMap.PixelsPerRegion);
    }

    [Fact]
    public void The_map_settings_are_clamped_when_read_and_a_wrong_type_gives_the_default()
    {
        var store = new MemoryStore { Text = "{\"minimap_scale\": 5, \"world_map_scale\": 100000, \"minimap_rotate\": \"yes\"}" };
        var (hub, _, _, _, _) = New(store);
        Assert.Equal(MiniMapModel.MinScale, hub.MiniMap.PixelsPerRegion);
        Assert.Equal(WorldMapModel.MaxScale, hub.WorldMap.PixelsPerRegion);
        Assert.True(hub.MiniMap.Rotate);
    }

    [Fact]
    public void Events_the_hub_does_not_know_are_left_alone()
    {
        var (hub, _, _, _, _) = New();
        hub.Enqueue(new Disconnected("x"));
        hub.Enqueue(new ChatReceived(null, Guid.Empty, Guid.Empty, "n", ChatSource.System, ChatLineType.Say, ChatAudible.Fully, Vector3.Zero, new MessageBody("t", MessageChannel.LocalChat)));
        Assert.Equal(2, hub.Pump());
        Assert.Equal(TeleportStage.Idle, hub.Teleport.Stage);
    }
}
