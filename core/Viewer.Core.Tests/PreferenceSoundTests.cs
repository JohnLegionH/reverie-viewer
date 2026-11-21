// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Viewer.Shell.Sound;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Preferences > Sound: the interface level, mute all and mute when minimized on the page, and each slot's source, level and switch
/// edited as previews that Save writes, Cancel puts back and Reset brings to the defaults, with Set all to ours or to the grid's.
/// Plain .NET; every member builds its own objects, so the class runs in parallel.
/// </summary>
public class PreferenceSoundTests
{
    private sealed class MemoryStore(string? text = null) : ISettingsStore
    {
        public string? Text = text;
        public string? Read() => Text;
        public bool Write(string text) { Text = text; return true; }
    }

    [Fact]
    public void The_sound_page_holds_the_master_sounds_and_ambient_levels_and_mutes_then_the_interface_level_the_two_mutes_and_the_two_thresholds()
    {
        Assert.Equal(["audio_level_master", "audio_mute_master", "audio_mono", "audio_level_sounds", "audio_mute_sounds", "audio_level_ambient", "audio_mute_ambient",
            "audio_collision_sounds", "audio_gesture_sounds", "ui_sound_volume", "ui_sound_mute_all", "ui_sound_mute_minimized", "ui_sound_money_threshold", "ui_sound_health_threshold"],
            PreferenceCatalog.OfPage(PrefPage.Sound).Select(d => d.Key));
        Assert.All(PreferenceCatalog.OfPage(PrefPage.Sound), d => Assert.Equal(PrefApply.Live, d.Apply));
    }

    [Fact]
    public void A_slot_edit_is_a_preview_until_save_and_cancel_puts_the_saved_values_back()
    {
        var store = new MemoryStore();
        var settings = new SettingsService(store);
        var model = new PreferencesModel(settings, null);
        Assert.True(model.EditSlotSource(UiSoundSlot.Click, UiSoundSource.Off));
        Assert.True(model.EditSlotVolume(UiSoundSlot.Click, 0.4));
        Assert.True(model.EditSlotEnabled(UiSoundSlot.Alert, false));
        Assert.Equal(UiSoundSource.Off, model.SlotSource(UiSoundSlot.Click));    // in force at once (the sounds read it)
        Assert.Equal(0.4, model.SlotVolume(UiSoundSlot.Click));
        Assert.False(model.SlotEnabled(UiSoundSlot.Alert));
        Assert.True(model.IsChanged(PrefPage.Sound));
        Assert.DoesNotContain("ui_sound_click_source", store.Text ?? "");          // not written yet
        model.Cancel(PrefPage.Sound);
        Assert.Equal(UiSoundSource.Grid, model.SlotSource(UiSoundSlot.Click));
        Assert.Equal(1.0, model.SlotVolume(UiSoundSlot.Click));
        Assert.True(model.SlotEnabled(UiSoundSlot.Alert));
        Assert.False(model.IsChanged(PrefPage.Sound));
    }

    [Fact]
    public void Save_writes_the_slots_and_a_new_start_reads_them()
    {
        var store = new MemoryStore();
        var model = new PreferencesModel(new SettingsService(store), null);
        model.EditSlotSource(UiSoundSlot.WindowOpen, UiSoundSource.Off);
        model.EditSlotVolume(UiSoundSlot.WindowOpen, 0.3);
        Assert.True(model.Save(PrefPage.Sound));
        var again = new SettingsService(new MemoryStore(store.Text));
        Assert.Equal(UiSoundSource.Off, UiSoundSettings.Source(again, UiSoundSlot.WindowOpen));
        Assert.Equal(0.3, again.Get(UiSoundSettings.VolumeOf(UiSoundSlot.WindowOpen)));
    }

    [Fact]
    public void Set_all_previews_every_slot_and_reset_brings_back_the_defaults()
    {
        var model = new PreferencesModel(new SettingsService(new MemoryStore()), null);
        model.EditAllSlotSources(UiSoundSource.Grid);
        Assert.Equal(23, UiSoundCatalog.All.Count(i => model.SlotSource(i.Slot) == UiSoundSource.Grid));
        model.EditSlotVolume(UiSoundSlot.Typing, 0.1);
        model.Edit(PreferenceCatalog.Find("ui_sound_volume")!, 0.9);
        model.Reset(PrefPage.Sound);
        Assert.All(UiSoundCatalog.All, i => Assert.Equal(UiSoundSource.Grid, model.SlotSource(i.Slot)));
        Assert.Equal(1.0, model.SlotVolume(UiSoundSlot.Typing));
        Assert.Equal(0.5, model.Value(PreferenceCatalog.Find("ui_sound_volume")!));
        Assert.False(model.SlotEnabled(UiSoundSlot.ChatMention));
    }

    [Fact]
    public void The_page_has_its_words_and_every_slot_its_name()
    {
        var strings = StringTable.Load("en");
        Assert.True(strings.Has("prefs.category.sound"));
        foreach (var info in UiSoundCatalog.All)
            Assert.True(strings.Has("prefs.sound.slot." + info.KeyName), info.KeyName);
        foreach (var key in new[] { "prefs.sound.play", "prefs.sound.source.grid", "prefs.sound.source.off",
                     "prefs.sound.all_grid", "prefs.sound.status.not_on_grid", "prefs.sound.status.no_grid_sound",
                     "prefs.sound.status.fetching", "prefs.sound.heading", "prefs.sound.enabled", "prefs.sound.volume", "prefs.sound.note.grid" })
            Assert.True(strings.Has(key), key);
    }

    [Fact]
    public void A_grid_sound_is_asked_for_from_the_regions_asset_capability_as_a_sound()
    {
        var id = Guid.Parse("4c8c3c77-de8d-bde2-b9b8-32635e0fd4a6");
        Assert.Equal("https://grid.example.org/CAPS/abc/?sound_id=" + id.ToString("D"),
            Viewer.Core.Protocol.LmvMaterials.SoundUri(new Uri("https://grid.example.org/CAPS/abc/"), id).ToString());
        Assert.EndsWith("?x=1&sound_id=" + id.ToString("D"), Viewer.Core.Protocol.LmvMaterials.SoundUri(new Uri("https://grid.example.org/c?x=1"), id).ToString());
    }
}
