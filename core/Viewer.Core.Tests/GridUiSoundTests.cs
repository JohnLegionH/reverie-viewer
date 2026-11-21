// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Assets;
using Viewer.Shell;
using Viewer.Shell.Sound;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The interface sounds come from the grid the viewer is connected to, by the id the official viewer uses, and the viewer ships none:
/// the slot-to-id table, the grid as the only source, silence for a sound the grid lacks (one log line per id per session), a cache
/// that never lets one grid's audio play on another, the login screen's cached sounds, and no sound files in the assembly.
/// Plain .NET with recording fakes; every member builds its own objects, so the class runs in parallel.
/// </summary>
public class GridUiSoundTests
{
    private sealed class MemoryStore : ISettingsStore
    {
        public string? Read() => null;
        public bool Write(string text) => true;
    }

    private sealed class Output : IUiSoundOutput
    {
        public readonly List<(UiSoundSlot Slot, Guid Id, int Length)> Plays = new();
        public void PlayOurs(UiSoundSlot slot, float gain) => Plays.Add((slot, Guid.Empty, 0));
        public bool PlayGrid(UiSoundSlot slot, Guid id, byte[] ogg, float gain)
        {
            Plays.Add((slot, id, ogg.Length));
            return true;
        }
    }

    private static readonly byte[] OggA = [(byte)'O', (byte)'g', (byte)'g', (byte)'S', 0, 2, 1, 1, 1, 1, 1];
    private static readonly byte[] OggB = [(byte)'O', (byte)'g', (byte)'g', (byte)'S', 0, 2, 9, 9, 9, 9, 9, 9];

    private sealed class Rig
    {
        public readonly SettingsService Settings = new(new MemoryStore());
        public readonly Output Out = new();
        public readonly List<string> Log = new();
        public readonly Dictionary<Guid, byte[]> Served = new();
        public readonly UiSounds Sounds;

        public Rig()
        {
            var grid = new GridSounds((id, _) => Task.FromResult(Served.TryGetValue(id, out var b) ? AssetFetchResult.Ok(b) : AssetFetchResult.Of(AssetFetchStatus.NotFound)),
                null, Log.Add);
            Sounds = new UiSounds(Settings, Out, grid, () => false, () => 0) { InWorld = true };
        }
    }

    [Fact]
    public void Twenty_two_slots_have_the_official_id_and_the_object_rez_out_slot_has_none()
    {
        Assert.Equal(23, UiSoundCatalog.All.Count);
        Assert.Equal(22, UiSoundCatalog.All.Count(i => i.GridId != Guid.Empty));
        Assert.Equal([UiSoundSlot.ObjectRezOut], UiSoundCatalog.All.Where(i => i.GridId == Guid.Empty).Select(i => i.Slot));
        // the click and its release share the official click sound: 21 distinct ids
        Assert.Equal(UiSoundCatalog.Of(UiSoundSlot.Click).GridId, UiSoundCatalog.Of(UiSoundSlot.ClickRelease).GridId);
        Assert.Equal(21, UiSoundCatalog.All.Select(i => i.GridId).Where(g => g != Guid.Empty).Distinct().Count());
    }

    [Fact]
    public void Every_slot_defaults_to_the_grids_sound()
    {
        var s = new SettingsService(new MemoryStore());
        foreach (var info in UiSoundCatalog.All)
            Assert.Equal(UiSoundSource.Grid, UiSoundSettings.Source(s, info.Slot));
    }

    [Fact]
    public void A_stored_source_of_ours_from_an_older_viewer_plays_the_grids_sound()
    {
        var s = new SettingsService(new MemoryStore());
        s.SetText(UiSoundSettings.SourceKey(UiSoundSlot.Alert), "ours");
        Assert.Equal(UiSoundSource.Grid, UiSoundSettings.Source(s, UiSoundSlot.Alert));
    }

    [Fact]
    public void A_play_with_the_default_settings_plays_the_grids_asset_and_never_a_file_of_ours()
    {
        var r = new Rig();
        var id = UiSoundCatalog.Of(UiSoundSlot.WindowOpen).GridId;
        r.Served[id] = OggA;
        r.Sounds.Preload();
        r.Sounds.Pump();
        Assert.Equal(UiSoundOutcome.PlayedGrid, r.Sounds.Play(UiSoundSlot.WindowOpen));
        Assert.Equal((UiSoundSlot.WindowOpen, id, OggA.Length), Assert.Single(r.Out.Plays));
    }

    [Fact]
    public void A_sound_the_grid_lacks_is_silent_and_logged_once_per_id_however_often_it_is_played()
    {
        var r = new Rig();
        var id = UiSoundCatalog.Of(UiSoundSlot.Alert).GridId;
        r.Sounds.Preload();
        r.Sounds.Pump();
        for (int i = 0; i < 5; i++)
            Assert.Equal(UiSoundOutcome.NotOnGrid, r.Sounds.Play(UiSoundSlot.Alert));
        r.Sounds.Pump();
        Assert.Empty(r.Out.Plays);
        Assert.Single(r.Log, l => l.Contains(id.ToString()));
        // one line per id: the click and its release are one id, so 21 lines for the 21 ids
        Assert.Equal(21, r.Log.Count);
    }

    [Fact]
    public void The_cache_scope_differs_between_grids_and_is_one_for_one_grid()
    {
        Assert.NotEqual(GridSoundCache.Scope("http://grid-a.example.org:9000/"), GridSoundCache.Scope("http://grid-b.example.org:9000/"));
        Assert.NotEqual(GridSoundCache.Scope("https://login.example.org/cgi-bin/login.cgi"), GridSoundCache.Scope("https://login.example.net/cgi-bin/login.cgi"));
        Assert.Equal(GridSoundCache.Scope("http://Grid-A.example.org:9000/"), GridSoundCache.Scope("http://grid-a.example.org:9000"));
    }

    [Fact]
    public void A_decoded_stream_is_found_by_the_bytes_so_the_same_id_from_another_grid_is_decoded_afresh()
    {
        Assert.Equal(GridSoundCache.ContentKey(OggA), GridSoundCache.ContentKey((byte[])OggA.Clone()));
        Assert.NotEqual(GridSoundCache.ContentKey(OggA), GridSoundCache.ContentKey(OggB));
    }

    [Fact]
    public void A_sound_cached_from_one_grid_is_never_read_for_another_in_the_content_store()
    {
        var root = Path.Combine(Path.GetTempPath(), "gridsound-" + Guid.NewGuid().ToString("N"));
        var store = new ContentStore(new ContentStoreOptions { Root = root, KeyProtector = new OwnerOnlyFileKeyProtector() });
        var id = UiSoundCatalog.Of(UiSoundSlot.Click).GridId;
        var viewA = store.View(GridSoundCache.Scope("http://grid-a.example.org:9000/"), "s1");
        var viewB = store.View(GridSoundCache.Scope("http://grid-b.example.org:9000/"), "s2");
        int networkB = 0;
        var fetchA = viewA.Caching(GridSoundCache.Kind, (_, _) => Task.FromResult(AssetFetchResult.Ok(OggA)));
        var fetchB = viewB.Caching(GridSoundCache.Kind, (_, _) => { networkB++; return Task.FromResult(AssetFetchResult.Ok(OggB)); });
        Assert.Equal(OggA, fetchA(id, default).Result.Bytes.ToArray());
        // grid B has the same id with other audio: it is fetched from B, not read from A's cache
        Assert.Equal(OggB, fetchB(id, default).Result.Bytes.ToArray());
        Assert.Equal(1, networkB);
        // and A's entry is still A's, read without the network
        var again = viewA.Caching(GridSoundCache.Kind, (_, _) => throw new InvalidOperationException("network"));
        Assert.Equal(OggA, again(id, default).Result.Bytes.ToArray());
    }

    [Fact]
    public void On_the_login_screen_the_chosen_grids_cached_sound_plays_and_a_sound_not_cached_stays_silent()
    {
        var r = new Rig();
        r.Sounds.InWorld = false;
        var open = UiSoundCatalog.Of(UiSoundSlot.WindowOpen).GridId;
        var cached = new Dictionary<Guid, byte[]> { [open] = OggA };
        r.Sounds.LoginCache = id => cached.GetValueOrDefault(id);
        Assert.Equal(UiSoundOutcome.PlayedGrid, r.Sounds.Play(UiSoundSlot.WindowOpen));
        Assert.Equal((UiSoundSlot.WindowOpen, open, OggA.Length), Assert.Single(r.Out.Plays));
        Assert.Equal(UiSoundOutcome.NotOnGrid, r.Sounds.Play(UiSoundSlot.Alert));
        Assert.Single(r.Out.Plays);
        r.Sounds.LoginCache = null;
        Assert.Equal(UiSoundOutcome.NotOnGrid, r.Sounds.Play(UiSoundSlot.WindowOpen));
        Assert.Single(r.Out.Plays);
    }

    [Fact]
    public void The_shell_assembly_carries_no_sound_files()
    {
        var names = typeof(UiSoundCatalog).Assembly.GetManifestResourceNames();
        Assert.DoesNotContain(names, n => n.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_volume_and_mute_settings_still_set_the_gain()
    {
        var r = new Rig();
        r.Served[UiSoundCatalog.Of(UiSoundSlot.Click).GridId] = OggA;
        r.Sounds.Preload();
        r.Sounds.Pump();
        r.Settings.Set(UiSoundSettings.Volume, 0.5);
        r.Settings.Set(UiSoundSettings.VolumeOf(UiSoundSlot.Click), 0.5);
        Assert.Equal(0.25f, r.Sounds.GainOf(UiSoundSlot.Click), 4);
        r.Settings.Set(UiSoundSettings.MuteAll, true);
        Assert.Equal(UiSoundOutcome.MutedAll, r.Sounds.Play(UiSoundSlot.Click));
        Assert.Empty(r.Out.Plays);
    }
}
