// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Shell.Sound;
using Viewer.Shell.Travel;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Regions larger than 256 m: the size the login reply names belongs to the login region only (Firestorm sizes each region from its own
/// message: login reply, TeleportFinish, CrossedRegion, EnableSimulator; llstartup.cpp, llviewermessage.cpp, llworld.cpp), a region's
/// parcel overlay is as many 1024-byte packets as its size needs (llviewerparceloverlay.cpp: width squared over 128 squared), and a
/// parcel bitmap covers the whole region (one bit per 4 m square). In-memory sessions and maps; the class runs in parallel.
/// </summary>
public class LargeRegionTests
{
    private static readonly SimRef Home = new(1000UL * 256 << 32 | 1000UL * 256, "Home");
    private static readonly SimRef Away = new(1010UL * 256 << 32 | 1000UL * 256, "Away");
    private static LoginCredentials Creds => new("Test", "Resident", new Secret("correct horse", SensitiveKind.PasswordDigest));

    [Fact]
    public async Task A_teleport_from_a_large_login_region_into_a_small_one_takes_the_new_regions_size()
    {
        var t = new FakeTransport();
        using var s = new ViewerSession(new SessionOptions { LoginUri = "http://grid.example:8002/" }, t, new CoreLog(new ListSink(), "test"));
        t.OnLogin = (_, _) => Task.FromResult(FakeTransport.Ok(Guid.NewGuid(), sizeX: 1024, sizeY: 1024));
        await s.LoginAsync(Creds, CancellationToken.None);
        t.DriveToInWorld(Home, simSizeX: 256, simSizeY: 256);
        Assert.Equal((1024, 1024), (s.Region!.SizeX, s.Region.SizeY));   // the login region: the login reply's size

        t.Raise(new SimConnected(Away, 256, 256));                      // a teleport lands in a 256 m region
        Assert.Equal("Away", s.Region!.Name);
        Assert.Equal((256, 256), (s.Region.SizeX, s.Region.SizeY));
    }

    [Fact]
    public async Task A_teleport_into_a_large_region_takes_the_size_its_landing_named()
    {
        var t = new FakeTransport();
        using var s = new ViewerSession(new SessionOptions { LoginUri = "http://grid.example:8002/" }, t, new CoreLog(new ListSink(), "test"));
        await s.LoginAsync(Creds, CancellationToken.None);
        t.DriveToInWorld(Home, simSizeX: 256, simSizeY: 256);
        t.Raise(new SimConnected(Away, 512, 512));
        Assert.Equal((512, 512), (s.Region!.SizeX, s.Region.SizeY));
    }

    // ---- ban lines: the bitmap of a parcel in a 512 m region has 128 squares per edge

    private static byte[] Bitmap(int cells, params (int X, int Y)[] set)
    {
        var b = new byte[cells * cells / 8];
        foreach (var (x, y) in set)
        {
            int i = x + y * cells;
            b[i / 8] |= (byte)(1 << (i % 8));
        }
        return b;
    }

    [Fact]
    public void A_parcel_bitmap_says_how_many_squares_its_region_has_per_edge()
    {
        Assert.Equal(64, BanWall.CellsFor(Bitmap(64)));
        Assert.Equal(128, BanWall.CellsFor(Bitmap(128)));
        Assert.Equal(256, BanWall.CellsFor(Bitmap(256)));
    }

    [Fact]
    public void A_ban_wall_in_a_large_region_is_built_on_that_regions_squares()
    {
        var state = new BanLineState { Mode = BanLineMode.OnProximity };
        state.Collision(new ParcelCollision(ParcelDenial.Banned, Bitmap(128, (100, 120))), 0);
        Assert.Equal(128, state.Cells);
        Assert.Equal(129 * 129, state.Segments!.Length);
        // the square at (100, 120) is outlined: its south-west corner carries both its south and its west edge
        Assert.Equal(BanWall.SouthMask | BanWall.WestMask, state.Segments[100 + 120 * 129] & (BanWall.SouthMask | BanWall.WestMask));
    }

    // ---- the parcel overlay of a 512 m region is 16 packets of 1024 squares

    [Fact]
    public void The_last_overlay_packet_of_a_large_region_covers_its_far_corner()
    {
        const ulong region = 1000UL * 256 << 32 | 1000UL * 256;
        var m = new ParcelSoundMap();
        var last = new byte[1024];
        last[1023] = ParcelSoundMap.SoundLocalBit;   // the last square of the last packet: (127, 127), the north-east corner
        m.SetOverlay(region, 15, last, regionWidth: 512);
        Assert.False(m.CanHear(region, new Vector3(510, 510, 20)));
        Assert.True(m.CanHear(region, new Vector3(250, 250, 20)));
    }

    [Fact]
    public void The_sound_engine_lays_an_overlay_out_for_its_regions_real_size()
    {
        const ulong region = 1000UL * 256 << 32 | 1000UL * 256;
        var parcels = new ParcelSoundMap();
        var sounds = new WorldSounds(new Viewer.Shell.SettingsService(new MemoryStore()), new NullOutput(),
            new GridSounds((_, _) => Task.FromResult(Viewer.Core.Assets.AssetFetchResult.Of(Viewer.Core.Assets.AssetFetchStatus.NotFound))), parcels, () => 0)
        {
            RegionWidth = h => h == region ? 512 : 256,
        };
        var last = new byte[1024];
        last[1023] = ParcelSoundMap.SoundLocalBit;
        sounds.Receive(new ParcelOverlayReceived(Home, 15, last));
        Assert.False(parcels.CanHear(region, new Vector3(510, 510, 20)));
    }

    private sealed class NullOutput : IWorldSoundOutput
    {
        public bool Start(int voice, Guid soundId, byte[] bytes, bool loop) => true;
        public void Stop(int voice) { }
        public void Place(int voice, Vector3 position, float gain) { }
        public bool IsPlaying(int voice) => false;
        public double PlaybackPosition(int voice) => 0;
    }

    private sealed class MemoryStore : Viewer.Shell.ISettingsStore
    {
        public string? Text;
        public string? Read() => Text;
        public bool Write(string text) { Text = text; return true; }
    }
}
