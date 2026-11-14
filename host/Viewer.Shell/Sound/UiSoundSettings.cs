// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Sound;

/// <summary>
/// The interface sounds' settings, kept in the settings file with the viewer's other preferences: the interface level, mute all, mute
/// while the window is minimized, the money and pain thresholds, and for each slot its source, its own level and its switch. The
/// viewer was silent before these existed, so no default changes anything it did.
/// </summary>
public static class UiSoundSettings
{
    private static double? Level(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : null;

    /// <summary>The level of every interface sound (0 to 1; 0.5, the official viewer's AudioLevelUI).</summary>
    public static readonly Setting<double> Volume = new("ui_sound_volume", 0.5, Level);

    /// <summary>Silences every interface sound (the official viewer's MuteUI).</summary>
    public static readonly Setting<bool> MuteAll = new("ui_sound_mute_all", false, v => v);

    /// <summary>Silences the interface sounds while the viewer's window is minimized (on by default).</summary>
    public static readonly Setting<bool> MuteWhenMinimized = new("ui_sound_mute_minimized", true, v => v);

    /// <summary>How much the balance must change for a money sound (the official UISndMoneyChangeThreshold, 50).</summary>
    public static readonly Setting<double> MoneyThreshold = new("ui_sound_money_threshold", 50, v => double.IsFinite(v) ? Math.Clamp(v, 0, 1_000_000) : null);

    /// <summary>How much health must drop for a pain sound (the official UISndHealthReductionThreshold, 10).</summary>
    public static readonly Setting<double> HealthThreshold = new("ui_sound_health_threshold", 10, v => double.IsFinite(v) ? Math.Clamp(v, 0, 100) : null);

    private const string Prefix = "ui_sound_";

    /// <summary>The slot's own level (0 to 1, full by default).</summary>
    public static Setting<double> VolumeOf(UiSoundSlot slot) => new(Prefix + UiSoundCatalog.Of(slot).KeyName + "_volume", 1.0, Level);

    /// <summary>Whether the slot's event plays it (the slot's Play button plays it either way).</summary>
    public static Setting<bool> EnabledOf(UiSoundSlot slot) => new(Prefix + UiSoundCatalog.Of(slot).KeyName + "_enabled", UiSoundCatalog.Of(slot).DefaultEnabled, v => v);

    /// <summary>The key of the slot's source (text: ours, grid or off).</summary>
    public static string SourceKey(UiSoundSlot slot) => Prefix + UiSoundCatalog.Of(slot).KeyName + "_source";

    /// <summary>The ids a source is stored as.</summary>
    private static readonly string[] Ids = ["ours", "grid", "off"];
    public static IReadOnlyList<string> SourceIds => Ids;

    public static string IdOf(UiSoundSource source) => SourceIds[(int)source];

    public static UiSoundSource? Parse(string? id) => Array.IndexOf(Ids, id ?? "") is var i and >= 0 ? (UiSoundSource)i : null;

    /// <summary>The slot's source now (a preview counts); the default when nothing, or something unknown, is stored.</summary>
    public static UiSoundSource Source(SettingsService settings, UiSoundSlot slot) =>
        Parse(settings.GetText(SourceKey(slot))) ?? UiSoundCatalog.Of(slot).DefaultSource;

    public static bool SetSource(SettingsService settings, UiSoundSlot slot, UiSoundSource source) => settings.SetText(SourceKey(slot), IdOf(source));

    /// <summary>Puts every slot on one source.</summary>
    public static void SetAll(SettingsService settings, UiSoundSource source)
    {
        foreach (var info in UiSoundCatalog.All)
            SetSource(settings, info.Slot, source);
    }

    /// <summary>Every key these settings use: the global ones, then each slot's.</summary>
    public static IReadOnlyList<string> GlobalKeys { get; } = [Volume.Key, MuteAll.Key, MuteWhenMinimized.Key, MoneyThreshold.Key, HealthThreshold.Key];

    public static IEnumerable<string> SlotKeys(UiSoundSlot slot) => [SourceKey(slot), VolumeOf(slot).Key, EnabledOf(slot).Key];

    public static IEnumerable<string> AllSlotKeys => UiSoundCatalog.All.SelectMany(i => SlotKeys(i.Slot));
}
