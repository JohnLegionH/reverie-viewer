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

    public static IReadOnlyList<UiSoundRecording> All { get; } = [];

    /// <summary>Whether a file is one of the recordings.</summary>
    public static bool IsRecorded(string file) => All.Any(r => r.File == file);
}
