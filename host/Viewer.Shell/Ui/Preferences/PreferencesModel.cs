// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell.Sound;

namespace Viewer.Shell;

/// <summary>
/// The ordinary Preferences pages without the engine: the value each control shows (a preview counts), an edit, and Save, Cancel and
/// Reset to defaults for one page at a time. An edit changes the value in memory at once, so everything that listens to the settings
/// service restyles or re-applies live; Save writes the page's keys to the file; Cancel puts the saved values back. Choosing a graphics
/// preset sets its values; editing one of them afterwards makes the preset Custom.
/// </summary>
public sealed class PreferencesModel(SettingsService settings, string? account)
{
    /// <summary>The settings service the page edits.</summary>
    public SettingsService Service => settings;

    /// <summary>The account the per-account settings belong to (null before a login).</summary>
    public string? Account => account;

    // ---- the Sound page's list: each interface sound's source, level and switch (previews, saved with the page) ----

    public UiSoundSource SlotSource(UiSoundSlot slot) => UiSoundSettings.Source(settings, slot);
    public double SlotVolume(UiSoundSlot slot) => settings.Get(UiSoundSettings.VolumeOf(slot));
    public bool SlotEnabled(UiSoundSlot slot) => settings.Get(UiSoundSettings.EnabledOf(slot));
    public bool EditSlotSource(UiSoundSlot slot, UiSoundSource source) => settings.SetTextPreview(UiSoundSettings.SourceKey(slot), UiSoundSettings.IdOf(source));
    public bool EditSlotVolume(UiSoundSlot slot, double volume) => settings.SetPreview(UiSoundSettings.VolumeOf(slot), volume);
    public bool EditSlotEnabled(UiSoundSlot slot, bool enabled) => settings.SetPreview(UiSoundSettings.EnabledOf(slot), enabled);

    /// <summary>Set all to the grid's (or off): every slot's source as a preview.</summary>
    public void EditAllSlotSources(UiSoundSource source)
    {
        foreach (var info in UiSoundCatalog.All)
            EditSlotSource(info.Slot, source);
    }

    /// <summary>How many interface sounds play now: the slot takes the grid's sound and its switch is on (the Sound page's summary line).</summary>
    public int InterfaceSoundsOn => UiSoundCatalog.All.Count(i => SlotSource(i.Slot) == UiSoundSource.Grid && SlotEnabled(i.Slot));

    /// <summary>How many interface sounds there are.</summary>
    public static int InterfaceSoundCount => UiSoundCatalog.All.Count;

    private bool Usable(PrefDef def) => def.Scope == PrefScope.Global || !string.IsNullOrEmpty(account);

    /// <summary>The value the control shows.</summary>
    public object? Value(PrefDef def) =>
        def.Key == Settings.GraphicsPresetKey ? Preset : def.Read(settings, account);

    /// <summary>Whether the control can be used now (a per-account setting needs an account).</summary>
    public bool Enabled(PrefDef def) => Usable(def);

    /// <summary>Sets a value as a preview. False when the setting refuses it.</summary>
    public bool Edit(PrefDef def, object value)
    {
        if (!Usable(def))
            return false;
        if (def.Key == Settings.GraphicsPresetKey)
            return value is string id && SelectPreset(id);
        if (!def.Write(settings, account, value, preview: true))
            return false;
        if (IsQualityValue(def.Key))
            ShowMatchingPreset();
        return true;
    }

    /// <summary>The graphics preset the values stand for: a preset id or custom.</summary>
    public string Preset
    {
        get
        {
            var (draw, texture) = QualityValues();
            // levels that share a draw distance and texture size differ only in the level-of-detail factor: the stored level is shown while its values hold
            foreach (var candidate in new[] { _chosen, settings.GetText(Settings.GraphicsPresetKey), GraphicsPresets.Default })
                if (candidate is not null && GraphicsPresets.Values(candidate) is { } v && v.DrawDistance == draw && v.MaxTextureResolution == texture)
                    return candidate;
            return GraphicsPresets.Match(draw, texture);
        }
    }

    // the level last chosen on this page, so that editing a value away and back shows it again
    private string? _chosen;

    /// <summary>Chooses a preset: its values become the previewed values. False for Custom or an unknown id.</summary>
    public bool SelectPreset(string id)
    {
        if (GraphicsPresets.Values(id) is not { } v)
            return false;
        settings.SetPreview(Settings.DrawDistance, v.DrawDistance);
        settings.SetPreview(Settings.MaxTextureResolution, v.MaxTextureResolution);
        _chosen = id;
        settings.SetTextPreview(Settings.GraphicsPresetKey, id);
        return true;
    }

    /// <summary>Whether the page has previewed values that are not saved.</summary>
    public bool IsChanged(PrefPage page) => Pending(page).Any();

    /// <summary>Whether a previewed value on the page only takes effect after a restart.</summary>
    public bool NeedsRestart(PrefPage page) => PendingDefs(page).Any(d => d.Apply == PrefApply.Restart);

    /// <summary>Writes the page's previewed values. False when the file could not be written.</summary>
    public bool Save(PrefPage page) => settings.Commit(KeysOf(page));

    /// <summary>Whether any page has previewed values that are not saved.</summary>
    public bool AnyChanged => Enum.GetValues<PrefPage>().Any(IsChanged);

    /// <summary>Writes every page's previewed values. False when the file could not be written.</summary>
    public bool SaveAll() => settings.Commit(Enum.GetValues<PrefPage>().SelectMany(KeysOf));

    /// <summary>Puts back the saved values of every page.</summary>
    public void CancelAll() => settings.Rollback(Enum.GetValues<PrefPage>().SelectMany(KeysOf));

    /// <summary>Puts back the page's saved values.</summary>
    public void Cancel(PrefPage page) => settings.Rollback(KeysOf(page));

    /// <summary>Previews every setting of the page at its default.</summary>
    public void Reset(PrefPage page)
    {
        foreach (var def in PreferenceCatalog.OfPage(page).Where(Usable))
            def.Write(settings, account, def.Default, preview: true);
        if (page == PrefPage.Graphics)
            ShowMatchingPreset();
        if (page == PrefPage.Sound)
            foreach (var info in UiSoundCatalog.All)
            {
                EditSlotSource(info.Slot, UiSoundSource.Grid);
                EditSlotVolume(info.Slot, UiSoundSettings.VolumeOf(info.Slot).Default);
                EditSlotEnabled(info.Slot, info.DefaultEnabled);
            }
    }

    private IEnumerable<string> KeysOf(PrefPage page) => PreferenceCatalog.OfPage(page).Where(Usable).Select(d => d.StoredKey(account))
        .Concat(page == PrefPage.Sound ? UiSoundSettings.AllSlotKeys : []).ToArray();

    private IEnumerable<string> Pending(PrefPage page) => KeysOf(page).Intersect(settings.PendingKeys).ToArray();

    private IEnumerable<PrefDef> PendingDefs(PrefPage page)
    {
        var pending = settings.PendingKeys;
        return PreferenceCatalog.OfPage(page).Where(d => Usable(d) && pending.Contains(d.StoredKey(account))).ToArray();
    }

    private static bool IsQualityValue(string key) =>
        key == Settings.DrawDistance.Key || key == Settings.MaxTextureResolution.Key;

    private (int Draw, int Texture) QualityValues() =>
        (settings.Get(Settings.DrawDistance), settings.Get(Settings.MaxTextureResolution));

    private void ShowMatchingPreset() => settings.SetTextPreview(Settings.GraphicsPresetKey, Preset);
}
