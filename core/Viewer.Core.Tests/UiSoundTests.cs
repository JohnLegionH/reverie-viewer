// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Actions;
using Viewer.Core.Assets;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.Social;
using Viewer.Shell;
using Viewer.Shell.Chat;
using Viewer.Shell.Notifications;
using Viewer.Shell.Social;
using Viewer.Shell.Sound;
using Viewer.Shell.Travel;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The interface sounds: the 23 slots the official viewer names (settings.xml UISnd*), their grid sound ids, the defaults, the
/// settings kept in the settings file, the route each play takes (our file, the grid's asset, or nothing), the mute rules, the
/// money and pain thresholds, the grid asset that is not on the grid, which controls click, and the events that play a slot.
/// Plain .NET with recording fakes; every member builds its own objects, so the class runs in parallel.
/// </summary>
public class UiSoundTests
{
    private static readonly Guid Self = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Ann = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abcd");

    private sealed class MemoryStore(string? text = null) : ISettingsStore
    {
        public string? Text = text;
        public int Writes;
        public string? Read() => Text;
        public bool Write(string text) { Text = text; Writes++; return true; }
    }

    private sealed class Output : IUiSoundOutput
    {
        public readonly List<(UiSoundSlot Slot, string Route, float Gain, int Bytes)> Plays = new();
        public bool DecodeOk = true;
        public bool PlayGrid(UiSoundSlot slot, Guid id, byte[] ogg, float gain)
        {
            if (!DecodeOk)
                return false;
            Plays.Add((slot, "grid", gain, ogg.Length));
            return true;
        }
    }

    // the smallest bytes that start like an Ogg stream (the capture pattern "OggS")
    private static readonly byte[] Ogg = [(byte)'O', (byte)'g', (byte)'g', (byte)'S', 0, 2, 0, 0, 0, 0, 0, 0, 0, 0];

    private sealed class Rig
    {
        public readonly MemoryStore Store;
        public readonly SettingsService Settings;
        public readonly Output Out = new();
        public readonly Dictionary<Guid, AssetFetchResult> Grid = new();
        public readonly List<Guid> Fetched = new();
        public readonly List<TaskCompletionSource<AssetFetchResult>> Waiting = new();
        public bool Hold;
        public bool Minimized;
        public double Now = 100;
        public readonly GridSounds GridSounds;
        public readonly UiSounds Sounds;

        public Rig(string? text = null)
        {
            Store = new MemoryStore(text);
            Settings = new SettingsService(Store);
            GridSounds = new GridSounds((id, ct) =>
            {
                Fetched.Add(id);
                if (Hold)
                {
                    var t = new TaskCompletionSource<AssetFetchResult>();
                    Waiting.Add(t);
                    return t.Task;
                }
                return Task.FromResult(Grid.TryGetValue(id, out var r) ? r : AssetFetchResult.Of(AssetFetchStatus.NotFound));
            });
            Sounds = new UiSounds(Settings, Out, GridSounds, () => Minimized, () => Now);
        }

        public void SetSource(UiSoundSlot slot, UiSoundSource source) => UiSoundSettings.SetSource(Settings, slot, source);

        /// <summary>The grid serves every sound and the session is in the world with its sounds fetched.</summary>
        public void ServeAll()
        {
            foreach (var info in UiSoundCatalog.All.Where(i => i.GridId != Guid.Empty))
                Grid[info.GridId] = AssetFetchResult.Ok(Ogg);
            Sounds.InWorld = true;
            Sounds.Preload();
            Pump();
        }

        /// <summary>The main-thread pump: applies the fetches that have finished (the fakes finish at once or when a test says).</summary>
        public void Pump() => Sounds.Pump();
    }

    private static Guid Id(string s) => Guid.Parse(s);

    // ---------------- the slots ----------------

    private static readonly (UiSoundSlot Slot, string Id)[] Official =
    [
        (UiSoundSlot.Alert, "ed124764-705d-d497-167a-182cd9fa2e6c"),
        (UiSoundSlot.BadKeystroke, "2ca849ba-2885-4bc3-90ef-d4987a5b983a"),
        (UiSoundSlot.ChatMention, "03e77cb5-592c-5b33-d271-2e46497c3fb3"),
        (UiSoundSlot.ChatPing, "7dd36df6-2624-5438-f988-fdf8588a0ad9"),
        (UiSoundSlot.Click, "4c8c3c77-de8d-bde2-b9b8-32635e0fd4a6"),
        (UiSoundSlot.ClickRelease, "4c8c3c77-de8d-bde2-b9b8-32635e0fd4a6"),
        (UiSoundSlot.HealthReductionF, "219c5d93-6c09-31c5-fb3f-c5fe7495c115"),
        (UiSoundSlot.HealthReductionM, "e057c244-5768-1056-c37e-1537454eeb62"),
        (UiSoundSlot.InvalidOp, "4174f859-0d3d-c517-c424-72923dc21f65"),
        (UiSoundSlot.MoneyChangeDown, "104974e3-dfda-428b-99ee-b0d4e748d3a3"),
        (UiSoundSlot.MoneyChangeUp, "77a018af-098e-c037-51a6-178f05877c6f"),
        (UiSoundSlot.NewIncomingIMSession, "67cc2844-00f3-2b3c-b991-6418d01e1bb7"),
        (UiSoundSlot.ObjectCreate, "f4a0660f-5446-dea2-80b7-6482a082803c"),
        (UiSoundSlot.ObjectDelete, "0cb7b00a-4c10-6948-84de-a93c09af2ba9"),
        (UiSoundSlot.ObjectRezIn, "3c8fc726-1fd6-862d-fa01-16c5b2568db6"),
        (UiSoundSlot.ObjectRezOut, "00000000-0000-0000-0000-000000000000"),
        (UiSoundSlot.Restart, "b92a0f64-7709-8811-40c5-16afd624a45f"),
        (UiSoundSlot.Snapshot, "3d09f582-3851-c0e0-f5ba-277ac5c73fb4"),
        (UiSoundSlot.StartIM, "c825dfbc-9827-7e02-6507-3713d18916c1"),
        (UiSoundSlot.TeleportOut, "d7a9a565-a013-2a69-797d-5332baa1a947"),
        (UiSoundSlot.Typing, "5e191c7b-8996-9ced-a177-b2ac32bfea06"),
        (UiSoundSlot.WindowClose, "2c346eda-b60c-ab33-1119-b8941916a499"),
        (UiSoundSlot.WindowOpen, "c80260ba-41fd-8a46-768a-6bf236360e3a"),
    ];

    [Fact]
    public void The_catalogue_has_the_23_official_slots_in_order_with_their_official_names()
    {
        Assert.Equal(Official.Select(o => o.Slot), UiSoundCatalog.All.Select(i => i.Slot));
        Assert.Equal(23, UiSoundCatalog.All.Count);
        Assert.Equal("UISndNewIncomingIMSession", UiSoundCatalog.OfficialName(UiSoundSlot.NewIncomingIMSession));
    }

    [Fact]
    public void Each_slot_carries_the_official_grid_sound_id()
    {
        foreach (var (slot, id) in Official)
            Assert.Equal(Id(id), UiSoundCatalog.Of(slot).GridId);
    }

    [Fact]
    public void The_mention_sound_starts_switched_off_as_the_official_one_does_and_every_other_slot_on()
    {
        var s = new SettingsService(new MemoryStore());
        Assert.Equal(23, UiSoundCatalog.All.Count);
        foreach (var info in UiSoundCatalog.All)
        {
            bool want = info.Slot != UiSoundSlot.ChatMention;
            Assert.Equal(want, info.DefaultEnabled);
            Assert.Equal(want, s.Get(UiSoundSettings.EnabledOf(info.Slot)));
            Assert.Equal(1.0, s.Get(UiSoundSettings.VolumeOf(info.Slot)));
        }
    }

    [Fact]
    public void Each_slot_has_its_own_settings_key_and_the_keys_older_viewers_saved_are_kept()
    {
        var keys = UiSoundCatalog.All.Select(i => i.KeyName).ToList();
        Assert.Equal(23, keys.Distinct().Count());
        Assert.All(keys, k => Assert.Matches("^[a-z0-9_]+$", k));
        Assert.Equal("new_incoming_im_session", UiSoundCatalog.Of(UiSoundSlot.NewIncomingIMSession).KeyName);
        Assert.Equal("health_reduction_f", UiSoundCatalog.Of(UiSoundSlot.HealthReductionF).KeyName);
        Assert.Equal("ui_sound_click_release_source", UiSoundSettings.SourceKey(UiSoundSlot.ClickRelease));
    }

    [Fact]
    public void The_global_settings_start_at_the_official_ui_level_and_thresholds_with_mute_when_minimized_on()
    {
        var s = new SettingsService(new MemoryStore());
        Assert.Equal(0.5, s.Get(UiSoundSettings.Volume));               // AudioLevelUI
        Assert.False(s.Get(UiSoundSettings.MuteAll));                   // MuteUI
        Assert.True(s.Get(UiSoundSettings.MuteWhenMinimized));          // the order's default (the official MuteWhenMinimized is off)
        Assert.Equal(50.0, s.Get(UiSoundSettings.MoneyThreshold));      // UISndMoneyChangeThreshold
        Assert.Equal(10.0, s.Get(UiSoundSettings.HealthThreshold));     // UISndHealthReductionThreshold
    }

    // ---------------- settings in the file ----------------

    [Fact]
    public void A_slots_source_volume_and_switch_are_saved_and_read_back()
    {
        var store = new MemoryStore();
        var s = new SettingsService(store);
        UiSoundSettings.SetSource(s, UiSoundSlot.Click, UiSoundSource.Grid);
        s.Set(UiSoundSettings.VolumeOf(UiSoundSlot.Click), 0.25);
        s.Set(UiSoundSettings.EnabledOf(UiSoundSlot.Click), false);
        s.Set(UiSoundSettings.Volume, 0.8);
        s.Set(UiSoundSettings.MoneyThreshold, 5.0);
        var again = new SettingsService(new MemoryStore(store.Text));
        Assert.Equal(UiSoundSource.Grid, UiSoundSettings.Source(again, UiSoundSlot.Click));
        Assert.Equal(0.25, again.Get(UiSoundSettings.VolumeOf(UiSoundSlot.Click)));
        Assert.False(again.Get(UiSoundSettings.EnabledOf(UiSoundSlot.Click)));
        Assert.Equal(0.8, again.Get(UiSoundSettings.Volume));
        Assert.Equal(5.0, again.Get(UiSoundSettings.MoneyThreshold));
        Assert.Equal(UiSoundSource.Grid, UiSoundSettings.Source(again, UiSoundSlot.Alert));   // the others untouched
        Assert.Contains("ui_sound_click_source", store.Text);
    }

    [Fact]
    public void A_source_the_viewer_does_not_know_and_a_volume_out_of_range_are_not_kept()
    {
        var s = new SettingsService(new MemoryStore("{\"ui_sound_click_source\": \"loud\", \"ui_sound_alert_volume\": 7}"));
        Assert.Equal(UiSoundSource.Grid, UiSoundSettings.Source(s, UiSoundSlot.Click));
        Assert.Equal(1.0, s.Get(UiSoundSettings.VolumeOf(UiSoundSlot.Alert)));
        Assert.True(s.Set(UiSoundSettings.Volume, 3.0));
        Assert.Equal(1.0, s.Get(UiSoundSettings.Volume));
        Assert.True(s.Set(UiSoundSettings.VolumeOf(UiSoundSlot.Alert), -2.0));
        Assert.Equal(0.0, s.Get(UiSoundSettings.VolumeOf(UiSoundSlot.Alert)));
    }

    [Fact]
    public void Starting_the_sounds_writes_nothing_to_an_existing_settings_file_and_changes_no_other_setting()
    {
        var r = new Rig("{\"draw_distance\": 256, \"chat_timestamps\": false}");
        r.Sounds.Play(UiSoundSlot.Click);
        r.Sounds.InWorld = true;
        r.Sounds.Preload();
        Assert.Equal(0, r.Store.Writes);
        Assert.Equal(256, r.Settings.Get(Viewer.Shell.Settings.DrawDistance));
        Assert.False(r.Settings.Get(Viewer.Shell.Settings.ChatTimestamps));
        Assert.Empty(r.Out.Plays);   // the login screen has no cached sound to play
    }

    // ---------------- routing ----------------

    [Fact]
    public void The_grids_sound_plays_at_the_ui_level_times_the_slots_level()
    {
        var r = new Rig();
        r.ServeAll();
        r.Settings.Set(UiSoundSettings.Volume, 0.5);
        r.Settings.Set(UiSoundSettings.VolumeOf(UiSoundSlot.WindowOpen), 0.5);
        Assert.Equal(UiSoundOutcome.PlayedGrid, r.Sounds.Play(UiSoundSlot.WindowOpen));
        var p = Assert.Single(r.Out.Plays);
        Assert.Equal((UiSoundSlot.WindowOpen, "grid"), (p.Slot, p.Route));
        Assert.Equal(0.25f, p.Gain, 4);
    }

    [Fact]
    public void Off_plays_nothing_and_a_switched_off_slot_plays_nothing_on_an_event_but_its_play_button_still_plays()
    {
        var r = new Rig();
        r.ServeAll();
        r.SetSource(UiSoundSlot.Alert, UiSoundSource.Off);
        Assert.Equal(UiSoundOutcome.Off, r.Sounds.Play(UiSoundSlot.Alert));
        Assert.Equal(UiSoundOutcome.Off, r.Sounds.Audition(UiSoundSlot.Alert));
        r.Settings.Set(UiSoundSettings.EnabledOf(UiSoundSlot.Click), false);
        Assert.Equal(UiSoundOutcome.Disabled, r.Sounds.Play(UiSoundSlot.Click));
        Assert.Empty(r.Out.Plays);
        Assert.Equal(UiSoundOutcome.PlayedGrid, r.Sounds.Audition(UiSoundSlot.Click));
        Assert.Equal(UiSoundSlot.Click, Assert.Single(r.Out.Plays).Slot);
    }

    [Fact]
    public void In_the_world_the_grids_source_fetches_the_official_id_once_and_plays_its_bytes()
    {
        var r = new Rig();
        var click = UiSoundCatalog.Of(UiSoundSlot.Click).GridId;
        r.Grid[click] = AssetFetchResult.Ok(Ogg);
        r.SetSource(UiSoundSlot.Click, UiSoundSource.Grid);
        r.Sounds.InWorld = true;
        r.Sounds.Preload();
        r.Pump();
        Assert.Equal(GridSoundState.Ready, r.GridSounds.StateOf(click));
        Assert.Equal(UiSoundOutcome.PlayedGrid, r.Sounds.Play(UiSoundSlot.Click));
        Assert.Equal(UiSoundOutcome.PlayedGrid, r.Sounds.Play(UiSoundSlot.Click));
        Assert.Equal(1, r.Fetched.Count(f => f == click));   // cached: fetched once (the preload also asks for the pain voices, which default to the grid)
        Assert.All(r.Out.Plays, p => Assert.Equal(("grid", Ogg.Length), (p.Route, p.Bytes)));
        Assert.Equal(2, r.Out.Plays.Count);
    }

    [Fact]
    public void A_grid_sound_the_grid_does_not_have_plays_nothing_and_is_reported_as_not_on_this_grid()
    {
        var r = new Rig();
        var alert = UiSoundCatalog.Of(UiSoundSlot.Alert).GridId;
        r.SetSource(UiSoundSlot.Alert, UiSoundSource.Grid);
        r.Sounds.InWorld = true;
        r.Sounds.Play(UiSoundSlot.Alert);
        r.Pump();
        Assert.Equal(GridSoundState.NotOnGrid, r.GridSounds.StateOf(alert));
        Assert.Equal(UiSoundOutcome.NotOnGrid, r.Sounds.Play(UiSoundSlot.Alert));
        Assert.Empty(r.Out.Plays);   // no fallback to our file
        Assert.Equal(UiSoundStatus.NotOnGrid, r.Sounds.StatusOf(UiSoundSlot.Alert));
        Assert.Equal(UiSoundStatus.Fine, r.Sounds.StatusOf(UiSoundSlot.Click));
    }

    [Fact]
    public void Bytes_that_are_not_an_ogg_stream_or_that_do_not_decode_count_as_not_on_this_grid()
    {
        var r = new Rig();
        var open = UiSoundCatalog.Of(UiSoundSlot.WindowOpen).GridId;
        var close = UiSoundCatalog.Of(UiSoundSlot.WindowClose).GridId;
        r.Grid[open] = AssetFetchResult.Ok(new byte[] { 1, 2, 3, 4, 5 });
        r.Grid[close] = AssetFetchResult.Ok(Ogg);
        r.SetSource(UiSoundSlot.WindowOpen, UiSoundSource.Grid);
        r.SetSource(UiSoundSlot.WindowClose, UiSoundSource.Grid);
        r.Sounds.InWorld = true;
        r.Sounds.Preload();
        r.Pump();
        Assert.Equal(GridSoundState.NotOnGrid, r.GridSounds.StateOf(open));
        r.Out.DecodeOk = false;
        Assert.Equal(UiSoundOutcome.NotOnGrid, r.Sounds.Play(UiSoundSlot.WindowClose));
        Assert.Equal(GridSoundState.Undecodable, r.GridSounds.StateOf(close));
        Assert.Empty(r.Out.Plays);
    }

    [Fact]
    public void A_first_play_of_a_grid_sound_waits_for_its_fetch_and_plays_when_it_arrives_but_a_late_one_is_dropped()
    {
        var r = new Rig { Hold = true };
        var start = UiSoundCatalog.Of(UiSoundSlot.StartIM).GridId;
        var tp = UiSoundCatalog.Of(UiSoundSlot.TeleportOut).GridId;
        r.SetSource(UiSoundSlot.StartIM, UiSoundSource.Grid);
        r.SetSource(UiSoundSlot.TeleportOut, UiSoundSource.Grid);
        r.Sounds.InWorld = true;
        Assert.Equal(UiSoundOutcome.Fetching, r.Sounds.Play(UiSoundSlot.StartIM));
        Assert.Equal(UiSoundOutcome.Fetching, r.Sounds.Play(UiSoundSlot.TeleportOut));
        Assert.Equal(2, r.Waiting.Count);
        r.Waiting[0].SetResult(AssetFetchResult.Ok(Ogg));
        r.Pump();
        Assert.Equal(UiSoundSlot.StartIM, Assert.Single(r.Out.Plays).Slot);
        r.Now += UiSounds.PendingWindowSeconds + 1;
        r.Waiting[1].SetResult(AssetFetchResult.Ok(Ogg));
        r.Pump();
        Assert.Single(r.Out.Plays);   // the teleport sound came too late to belong to its event
        Assert.Equal(GridSoundState.Ready, r.GridSounds.StateOf(tp));
    }

    [Fact]
    public void A_fetch_that_failed_is_tried_again_at_the_next_play()
    {
        var r = new Rig();
        var id = UiSoundCatalog.Of(UiSoundSlot.Typing).GridId;
        r.Grid[id] = AssetFetchResult.Of(AssetFetchStatus.Failed);
        r.SetSource(UiSoundSlot.Typing, UiSoundSource.Grid);
        r.Sounds.InWorld = true;
        r.Sounds.Play(UiSoundSlot.Typing);
        r.Pump();
        Assert.Equal(GridSoundState.Failed, r.GridSounds.StateOf(id));
        r.Grid[id] = AssetFetchResult.Ok(Ogg);
        r.Sounds.Play(UiSoundSlot.Typing);
        r.Pump();
        Assert.Equal(2, r.Fetched.Count);
        Assert.Equal(UiSoundSlot.Typing, Assert.Single(r.Out.Plays).Slot);
    }

    [Fact]
    public void The_slot_the_official_viewer_leaves_silent_plays_nothing_from_the_grid_and_fetches_nothing()
    {
        var r = new Rig();
        r.SetSource(UiSoundSlot.ObjectRezOut, UiSoundSource.Grid);
        r.Sounds.InWorld = true;
        Assert.Equal(UiSoundOutcome.NoGridSound, r.Sounds.Play(UiSoundSlot.ObjectRezOut));
        r.Pump();
        Assert.Empty(r.Fetched);
        Assert.Empty(r.Out.Plays);
        Assert.Equal(UiSoundStatus.NoGridSound, r.Sounds.StatusOf(UiSoundSlot.ObjectRezOut));
    }

    [Fact]
    public void Set_all_puts_every_slot_on_one_source()
    {
        var r = new Rig();
        Assert.Equal(23, UiSoundCatalog.All.Count);
        UiSoundSettings.SetAll(r.Settings, UiSoundSource.Grid);
        Assert.All(UiSoundCatalog.All, i => Assert.Equal(UiSoundSource.Grid, UiSoundSettings.Source(r.Settings, i.Slot)));
        UiSoundSettings.SetAll(r.Settings, UiSoundSource.Off);
        Assert.Equal(UiSoundOutcome.Off, r.Sounds.Play(UiSoundSlot.Click));
    }

    // ---------------- mute rules ----------------

    [Fact]
    public void Mute_all_silences_every_slot_and_the_play_buttons()
    {
        var r = new Rig();
        r.Settings.Set(UiSoundSettings.MuteAll, true);
        foreach (var info in UiSoundCatalog.All)
            Assert.Equal(UiSoundOutcome.MutedAll, r.Sounds.Play(info.Slot));
        Assert.Equal(UiSoundOutcome.MutedAll, r.Sounds.Audition(UiSoundSlot.Click));
        Assert.Empty(r.Out.Plays);
    }

    [Fact]
    public void A_minimized_window_silences_the_sounds_while_mute_when_minimized_is_on()
    {
        var r = new Rig { Minimized = true };
        r.ServeAll();
        Assert.Equal(UiSoundOutcome.MutedMinimized, r.Sounds.Play(UiSoundSlot.Alert));
        Assert.Empty(r.Out.Plays);
        r.Settings.Set(UiSoundSettings.MuteWhenMinimized, false);
        Assert.Equal(UiSoundOutcome.PlayedGrid, r.Sounds.Play(UiSoundSlot.Alert));
        r.Minimized = false;
        r.Settings.Set(UiSoundSettings.MuteWhenMinimized, true);
        Assert.Equal(UiSoundOutcome.PlayedGrid, r.Sounds.Play(UiSoundSlot.Alert));
        Assert.Equal(2, r.Out.Plays.Count);
    }

    [Fact]
    public void Every_decision_is_kept_in_the_recent_list_for_checks()
    {
        var r = new Rig();
        r.ServeAll();
        r.SetSource(UiSoundSlot.Alert, UiSoundSource.Off);
        r.Sounds.Play(UiSoundSlot.Click);
        r.Sounds.Play(UiSoundSlot.Alert);
        Assert.Equal([(UiSoundSlot.Click, UiSoundOutcome.PlayedGrid), (UiSoundSlot.Alert, UiSoundOutcome.Off)],
            r.Sounds.Recent.Select(e => (e.Slot, e.Outcome)));
        r.Sounds.ClearRecent();
        Assert.Empty(r.Sounds.Recent);
    }

    // ---------------- thresholds ----------------

    [Fact]
    public void Money_plays_only_past_the_threshold_and_never_for_the_first_balance()
    {
        var r = new Rig();
        r.ServeAll();
        Assert.Null(r.Sounds.Balance(1000));    // the first balance known
        Assert.Null(r.Sounds.Balance(950));     // 50 is not more than the threshold
        Assert.Equal(UiSoundSlot.MoneyChangeDown, r.Sounds.Balance(899));
        Assert.Equal(UiSoundSlot.MoneyChangeUp, r.Sounds.Balance(1000));
        r.Settings.Set(UiSoundSettings.MoneyThreshold, 5.0);
        Assert.Equal(UiSoundSlot.MoneyChangeUp, r.Sounds.Balance(1006));
        Assert.Equal([UiSoundSlot.MoneyChangeDown, UiSoundSlot.MoneyChangeUp, UiSoundSlot.MoneyChangeUp], r.Out.Plays.Select(p => p.Slot));
    }

    [Fact]
    public void Pain_plays_the_voice_for_the_avatars_sex_only_past_the_threshold()
    {
        Assert.Null(UiSoundRules.Pain(100, 90, 10, female: true));
        Assert.Equal(UiSoundSlot.HealthReductionF, UiSoundRules.Pain(100, 89, 10, female: true));
        Assert.Equal(UiSoundSlot.HealthReductionM, UiSoundRules.Pain(100, 89, 10, female: false));
        Assert.Null(UiSoundRules.Pain(50, 80, 10, female: false));   // healing is silent
        Assert.Null(UiSoundRules.Money(0, 500, 50));                  // no balance known yet
        Assert.Equal(UiSoundSlot.MoneyChangeDown, UiSoundRules.Money(500, 400, 50));
    }

    // ---------------- which controls click ----------------

    [Fact]
    public void Buttons_click_on_release_menus_and_sliders_as_the_official_viewer_and_scroll_bars_text_and_links_stay_silent()
    {
        Assert.Equal((false, true), UiSoundRules.Clicks(UiControlKind.Button));
        Assert.Equal((false, true), UiSoundRules.Clicks(UiControlKind.Toggle));
        Assert.Equal((false, true), UiSoundRules.Clicks(UiControlKind.Choice));
        Assert.Equal((false, true), UiSoundRules.Clicks(UiControlKind.MenuItem));
        Assert.Equal((true, false), UiSoundRules.Clicks(UiControlKind.MenuBarTitle));
        Assert.Equal((true, true), UiSoundRules.Clicks(UiControlKind.Slider));
        Assert.Equal((false, false), UiSoundRules.Clicks(UiControlKind.ChoiceListItem));
        Assert.Equal((false, false), UiSoundRules.Clicks(UiControlKind.ScrollBar));
        Assert.Equal((false, false), UiSoundRules.Clicks(UiControlKind.TextField));
        Assert.Equal((false, false), UiSoundRules.Clicks(UiControlKind.Link));
        Assert.Equal((false, false), UiSoundRules.Clicks(UiControlKind.Silent));
    }

    [Fact]
    public void Login_dialogs_that_are_windows_open_as_windows_and_the_others_sound_as_alerts()
    {
        Assert.Equal(UiSoundSlot.WindowOpen, UiSoundRules.LoginDialog(LoginDialogKind.Terms));
        Assert.Equal(UiSoundSlot.WindowOpen, UiSoundRules.LoginDialog(LoginDialogKind.Critical));
        foreach (var k in new[] { LoginDialogKind.Update, LoginDialogKind.Token, LoginDialogKind.TrustCertificate, LoginDialogKind.CertificateError, LoginDialogKind.Unknown })
            Assert.Equal(UiSoundSlot.Alert, UiSoundRules.LoginDialog(k));
    }

    // ---------------- events ----------------

    private static IEnumerable<UiSoundSlot> Played(Rig r) => r.Out.Plays.Select(p => p.Slot);

    [Fact]
    public void Opening_a_closed_window_plays_window_open_once_and_closing_it_window_close_once()
    {
        var r = new Rig();
        r.ServeAll();
        var floaters = new FloaterManager();
        floaters.Register(new FloaterSpec("one", "t", new UiSize(100, 100), new UiSize(50, 50)));
        floaters.Register(new FloaterSpec("two", "t", new UiSize(100, 100), new UiSize(50, 50)));
        using var wiring = UiSoundWiring.Floaters(floaters, r.Sounds);
        floaters.Open("one");
        floaters.Open("two");
        floaters.Open("one");          // already open: brought to the front, no sound
        floaters.Focus("two");
        floaters.Move("one", 10, 10);
        Assert.Equal([UiSoundSlot.WindowOpen, UiSoundSlot.WindowOpen], Played(r));
        floaters.SetCloseGuard("two", () => false);
        floaters.Close("two");         // refused: still open, no sound
        floaters.Close("one");
        floaters.Close("one");         // not open: nothing
        Assert.Equal([UiSoundSlot.WindowOpen, UiSoundSlot.WindowOpen, UiSoundSlot.WindowClose], Played(r));
        floaters.Toggle("one");
        Assert.Equal(UiSoundSlot.WindowOpen, Played(r).Last());
    }

    [Fact]
    public void After_the_wiring_is_disposed_windows_play_nothing()
    {
        var r = new Rig();
        r.ServeAll();
        var floaters = new FloaterManager();
        floaters.Register(new FloaterSpec("one", "t", new UiSize(100, 100), new UiSize(50, 50)));
        var wiring = UiSoundWiring.Floaters(floaters, r.Sounds);
        floaters.Open("one");
        Assert.Equal([UiSoundSlot.WindowOpen], Played(r));
        wiring.Dispose();
        floaters.Close("one");
        floaters.Open("one");
        Assert.Single(r.Out.Plays);
    }

    private sealed class NoticeRig
    {
        public readonly Rig Sound = new();
        public double Now = 50;
        public DateTimeOffset Clock = new(2026, 3, 4, 13, 5, 0, TimeSpan.Zero);
        public readonly FakeTransport Transport = new();
        public readonly ActionPipeline Pipeline;
        public readonly NameCache Names = new();
        public readonly NotificationModel Model;
        public readonly FriendsModel Friends;
        public readonly TeleportModel Teleport;
        public readonly NoticeModel Notices;
        public readonly NoticeFeeder Feeder;
        public readonly InventoryOffers Offers;
        public readonly IDisposable Wiring;

        public NoticeRig()
        {
            Pipeline = new ActionPipeline(() => Transport, () => true, new CoreLog(new ListSink(), "test"));
            Model = new NotificationModel(() => Now);
            Friends = new FriendsModel(Self, Names, Pipeline.Submit, () => true);
            Teleport = new TeleportModel(Self, Names, Pipeline.Submit, () => Clock);
            Notices = new NoticeModel(() => Now);
            Feeder = new NoticeFeeder(Model, Friends, Teleport, Notices, id => "Ann Example");
            Offers = new InventoryOffers(Model, Pipeline.Submit);
            Sound.ServeAll();
            Wiring = UiSoundWiring.Notifications(Model, Notices, Sound.Sounds);
        }
    }

    [Fact]
    public void An_alert_plays_alert_a_tip_plays_nothing_and_offers_play_what_the_official_templates_name()
    {
        var n = new NoticeRig();
        n.Model.Post(new Notification { Kind = NotificationKind.Tip, Body = NoticeText.Raw("hello") });
        n.Model.Post(new Notification { Kind = NotificationKind.Alert, Body = NoticeText.Raw("stop") });
        Assert.Equal([UiSoundSlot.Alert], Played(n.Sound));
        n.Offers.Receive(new InventoryOffered(Ann, "Ann Example", false, Guid.NewGuid(), 7, Guid.NewGuid(), "a notecard"));   // from an agent
        n.Offers.Receive(new InventoryOffered(Guid.NewGuid(), "A box", true, Guid.NewGuid(), 7, Guid.NewGuid(), "a notecard"));   // from an object: silent
        n.Teleport.Receive(new TeleportOffered(Ann, "Ann Example", Guid.NewGuid(), new MessageBody("come", MessageChannel.InstantMessage)));
        n.Friends.Receive(new FriendshipOffered(Ann, "Ann Example", Guid.NewGuid(), true, new MessageBody("hi", MessageChannel.InstantMessage)));   // silent
        Assert.Equal([UiSoundSlot.Alert, UiSoundSlot.NewIncomingIMSession, UiSoundSlot.NewIncomingIMSession], Played(n.Sound));
    }

    [Fact]
    public void A_region_restart_alert_by_its_notification_id_or_OpenSims_restart_text_plays_restart_and_other_plain_text_does_not()
    {
        var n = new NoticeRig();
        n.Notices.Receive(new ServerAlert("The region you are in now is about to restart.", "RegionRestartMinutes", false));
        n.Notices.Receive(new ServerAlert("Region will restart in 2 minutes", "", false));
        n.Notices.Receive(new ServerAlert("x", "RegionRestartSeconds", false));
        n.Notices.Receive(new ServerAlert("The region will be busy later", "", false));
        Assert.Equal([UiSoundSlot.Restart, UiSoundSlot.Restart, UiSoundSlot.Restart], Played(n.Sound).Where(s => s == UiSoundSlot.Restart));
    }

    private sealed class ChatRig
    {
        public readonly Rig Sound = new();
        public DateTimeOffset Now = new(2026, 3, 4, 13, 5, 0, TimeSpan.Zero);
        public readonly FakeTransport Transport = new();
        public readonly ActionPipeline Pipeline;
        public readonly Conversations Conversations;
        public readonly NearbyChat Nearby;
        public readonly IDisposable Wiring;

        public ChatRig()
        {
            Pipeline = new ActionPipeline(() => Transport, () => true, new CoreLog(new ListSink(), "test"));
            Conversations = new Conversations(Self, new NameCache(), Pipeline.Submit, () => 100, () => true, () => Now, _ => { });
            Nearby = new NearbyChat(Self, Pipeline.Submit, () => 100, () => true, () => Now);
            Sound.ServeAll();
            Wiring = UiSoundWiring.Chat(Nearby, Conversations, Sound.Sounds);
        }

        public void Im(string text) => Conversations.Receive(new InstantMessageReceived(Ann, "Ann Example", Self, Guid.Empty, ImKind.Message, 0, false, null, false,
            new MessageBody(text, MessageChannel.InstantMessage)));
    }

    [Fact]
    public void A_new_incoming_conversation_plays_once_and_its_next_message_plays_nothing()
    {
        var c = new ChatRig();
        c.Im("hello");
        c.Im("again");
        Assert.Equal([UiSoundSlot.NewIncomingIMSession], Played(c.Sound));
    }

    [Fact]
    public void Starting_a_conversation_plays_start_im_each_time_the_user_starts_one()
    {
        var c = new ChatRig();
        c.Conversations.OpenWith(Ann);
        c.Conversations.OpenWith(Ann);
        c.Conversations.OpenGroup(Guid.NewGuid(), "A group");
        Assert.Equal([UiSoundSlot.StartIM, UiSoundSlot.StartIM, UiSoundSlot.StartIM], Played(c.Sound));
        c.Conversations.OpenWith(Self);   // not a conversation
        Assert.Equal(3, c.Sound.Out.Plays.Count);
    }

    [Fact]
    public void Starting_to_type_in_the_chat_bar_plays_typing_once_and_not_for_every_key()
    {
        var c = new ChatRig();
        c.Nearby.InputChanged("h");
        c.Nearby.InputChanged("he");
        c.Nearby.InputChanged("hel");
        c.Nearby.InputChanged("/command");   // a command is not typing
        c.Nearby.InputChanged("");
        c.Nearby.InputChanged("again");
        Assert.Equal([UiSoundSlot.Typing, UiSoundSlot.Typing], Played(c.Sound));
    }

    [Fact]
    public void A_teleport_plays_teleport_out_once_when_asked_and_once_when_the_grid_starts_one_unasked()
    {
        var r = new Rig();
        r.ServeAll();
        var transport = new FakeTransport();
        var pipeline = new ActionPipeline(() => transport, () => true, new CoreLog(new ListSink(), "test"));
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var tp = new TeleportModel(Self, new NameCache(), pipeline.Submit, () => now);
        using var wiring = UiSoundWiring.Travel(tp, null, r.Sounds);
        Assert.True(tp.ToRegion("Far Region", new System.Numerics.Vector3(128, 128, 30)));
        tp.Receive(new TeleportProgressed(TeleportPhase.Started, "", true));
        tp.Receive(new TeleportProgressed(TeleportPhase.Progress, "sending_dest", true));
        Assert.Equal([UiSoundSlot.TeleportOut], Played(r));
        tp.Receive(new TeleportProgressed(TeleportPhase.Failed, "nope", true));
        tp.Receive(new TeleportProgressed(TeleportPhase.Started, "", true));   // sent home by the grid
        Assert.Equal([UiSoundSlot.TeleportOut, UiSoundSlot.TeleportOut], Played(r));
    }

    [Fact]
    public void A_map_teleport_with_nothing_chosen_plays_invalid_op()
    {
        var r = new Rig();
        r.ServeAll();
        var map = new WorldMapModel(new MapLinks());
        var transport = new FakeTransport();
        var pipeline = new ActionPipeline(() => transport, () => true, new CoreLog(new ListSink(), "test"));
        var tp = new TeleportModel(Self, new NameCache(), pipeline.Submit, () => DateTimeOffset.UnixEpoch);
        using var wiring = UiSoundWiring.Travel(tp, map, r.Sounds);
        Assert.False(map.TeleportToSelected());
        Assert.Equal([UiSoundSlot.InvalidOp], Played(r));
    }
}
