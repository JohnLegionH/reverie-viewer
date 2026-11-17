// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Sound;

/// <summary>
/// One interface sound we ship as a recording rather than from the generator: its slot, and where it came from. A sound from a
/// public-domain (CC0 1.0) pack names the pack as its page titles it, the author the page names, and the page; a sound made with
/// ChatGPT has no pack.
/// </summary>
public sealed record UiSoundRecording(UiSoundSlot Slot, string? Pack, string? Author, string? Link)
{
    /// <summary>Whether the sound was made with ChatGPT (no pack).</summary>
    public bool MadeWithChatGpt => Pack is null;

    /// <summary>Our file for the slot.</summary>
    public string File => UiSoundCatalog.Of(Slot).File;
}

/// <summary>
/// The interface sounds shipped as recordings. Each one is resampled to the shipped format (16-bit mono 44.1 kHz) and brought to the
/// integrated loudness the official viewer's sound for the same event measures, with its peak no higher than -1 dBFS (the peak wins
/// where both cannot hold). The other slots play the generator's sound (<see cref="UiSoundSynth"/>).
/// </summary>
public static class UiSoundRecordings
{
    /// <summary>How the notices credit a sound made with ChatGPT.</summary>
    public const string ChatGptCredit = "made with ChatGPT for Reverie by Legion Builds";

    /// <summary>The recordings in the official order; a sound with no pack was made with ChatGPT.</summary>
    public static IReadOnlyList<UiSoundRecording> All { get; } =
    [
        new(UiSoundSlot.Alert, "100 CC0 SFX", "rubberduck", "https://opengameart.org/content/100-cc0-sfx"),
        new(UiSoundSlot.BadKeystroke, "54 Casino sound effects (cards, dice, chips)", "Kenney", "https://opengameart.org/content/54-casino-sound-effects-cards-dice-chips"),
        new(UiSoundSlot.ChatMention, "100 CC0 SFX", "rubberduck", "https://opengameart.org/content/100-cc0-sfx"),
        new(UiSoundSlot.Click, "75 CC0 breaking / falling / hit sfx", "rubberduck", "https://opengameart.org/content/75-cc0-breaking-falling-hit-sfx"),
        new(UiSoundSlot.ClickRelease, "75 CC0 breaking / falling / hit sfx", "rubberduck", "https://opengameart.org/content/75-cc0-breaking-falling-hit-sfx"),
        new(UiSoundSlot.InvalidOp, "100 CC0 SFX", "rubberduck", "https://opengameart.org/content/100-cc0-sfx"),
        new(UiSoundSlot.MoneyChangeDown, "coin sounds", "syncopika", "https://opengameart.org/content/coin-sounds"),
        new(UiSoundSlot.MoneyChangeUp, "12 Coin Sound Effects", "StarNinjas", "https://opengameart.org/content/12-coin-sound-effects"),
        new(UiSoundSlot.NewIncomingIMSession, null, null, null),
        new(UiSoundSlot.ObjectCreate, null, null, null),
        new(UiSoundSlot.ObjectDelete, null, null, null),
        new(UiSoundSlot.ObjectRezIn, "100 CC0 SFX #2", "rubberduck", "https://opengameart.org/content/100-cc0-sfx-2"),
        new(UiSoundSlot.ObjectRezOut, null, null, null),
        new(UiSoundSlot.StartIM, "63 Digital sound effects (lasers, phasers, space etc.)", "Kenney", "https://opengameart.org/content/63-digital-sound-effects-lasers-phasers-space-etc"),
        new(UiSoundSlot.TeleportOut, null, null, null),
        new(UiSoundSlot.WindowClose, "50 CC0 retro / synth SFX", "rubberduck", "https://opengameart.org/content/50-cc0-retro-synth-sfx"),
        new(UiSoundSlot.WindowOpen, null, null, null),
    ];

    /// <summary>Whether a file is one of the recordings.</summary>
    public static bool IsRecorded(string file) => All.Any(r => r.File == file);
}
