// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later


using Viewer.Shell.Sound;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Our interface sound files: the generator makes the file of every slot that is not a recording (and our footstep), the same bytes
/// each run, as 16-bit mono 44.1 kHz WAVs peaking at about -6 dBFS (or at their loudness target under -1 dBFS) with soft ends; every
/// slot ships a file of the length the interface calls for; the generated files shipped in the shell assembly are exactly its output;
/// and the shell reads them back. Plain .NET, nothing shared, so the class runs in parallel.
/// </summary>
public class UiSoundFileTests
{
    private static SoundRecipe Recipe(UiSoundSlot slot)
    {
        var recipe = UiSoundSynth.Recipes.FirstOrDefault(r => r.File == UiSoundCatalog.Of(slot).File);
        Assert.True(recipe is not null, "no recipe for " + slot);
        return recipe!;
    }

    private static short[] Samples(UiSoundSlot slot) => UiSoundSynth.Render(Recipe(slot));

    // the shipped file's length, whether the generator made it or it is a recording
    private static double ShippedMs(UiSoundSlot slot)
    {
        var wav = WavFile.Parse(UiSoundFiles.Read(UiSoundCatalog.Of(slot).File) ?? []);
        Assert.True(wav is not null, slot + " ships no WAV");
        return wav!.Seconds * 1000;
    }

    [Fact]
    public void The_generator_has_one_recipe_for_each_generated_slots_file_and_one_for_our_footstep()
    {
        string[] generated =
        [
            "ui-chat-ping.wav", "ui-health-reduction-f.wav", "ui-health-reduction-m.wav", "ui-restart.wav", "ui-snapshot.wav",
            "ui-typing.wav", "world-footstep.wav",
        ];
        Assert.Equal(generated.Order(), UiSoundSynth.Recipes.Select(r => r.File).Order());
    }

    [Fact]
    public void Two_runs_of_the_generator_give_byte_identical_files()
    {
        Assert.Equal(7, UiSoundSynth.Recipes.Count);
        foreach (var r in UiSoundSynth.Recipes)
        {
            var a = UiSoundSynth.Wav(UiSoundSynth.Render(r));
            var b = UiSoundSynth.Wav(UiSoundSynth.Render(r));
            Assert.NotEmpty(a);
            Assert.Equal(a, b);
        }
    }

    [Fact]
    public void Every_sound_without_a_loudness_target_peaks_at_about_minus_6_dbfs_and_every_sound_starts_and_ends_softly()
    {
        Assert.Equal(7, UiSoundSynth.Recipes.Count);
        foreach (var r in UiSoundSynth.Recipes)
        {
            var s = UiSoundSynth.Render(r);
            Assert.True(s.Length > 0, r.File);
            int peak = s.Max(v => Math.Abs((int)v));
            double dbfs = 20 * Math.Log10(peak / 32767.0);
            if (r.Lufs == 0)
                Assert.InRange(dbfs, -6.2, -5.8);
            else
                Assert.InRange(dbfs, -60, -0.95);
            Assert.True(Math.Abs((int)s[0]) < 100 && Math.Abs((int)s[^1]) < 100, r.File + " ends hard");
            Assert.True(s.Take(10).All(v => Math.Abs((int)v) < 2000), r.File + " starts with a jump");
            Assert.True(s.TakeLast(10).All(v => Math.Abs((int)v) < 2000), r.File + " stops with a jump");
        }
    }

    [Fact]
    public void Clicks_are_short_windows_under_250_ms_and_nothing_is_longer_than_the_official_teleport()
    {
        foreach (var slot in new[] { UiSoundSlot.Click, UiSoundSlot.ClickRelease })
            Assert.InRange(ShippedMs(slot), 10, 60);
        foreach (var slot in new[] { UiSoundSlot.WindowOpen, UiSoundSlot.WindowClose, UiSoundSlot.BadKeystroke })
            Assert.InRange(ShippedMs(slot), 30, 250);
        Assert.InRange(ShippedMs(UiSoundSlot.TeleportOut), 4000, 4100);   // the official teleport sound is about 4 s
        Assert.All(UiSoundCatalog.All, i => Assert.InRange(ShippedMs(i.Slot), 10, 4100));
        Assert.All(UiSoundSynth.Recipes, r => Assert.InRange(UiSoundSynth.Render(r).Length * 1000.0 / UiSoundSynth.Rate, 10, 4100));
    }

    [Fact]
    public void A_wav_is_16_bit_mono_at_44100_and_reads_back_to_the_same_samples()
    {
        var samples = Samples(UiSoundSlot.Typing);
        var wav = UiSoundSynth.Wav(samples);
        var parsed = WavFile.Parse(wav);
        Assert.NotNull(parsed);
        Assert.Equal((44100, 1, 16), (parsed!.Rate, parsed.Channels, parsed.Bits));
        Assert.Equal(samples.Length * 2, parsed.Pcm.Length);
        Assert.Equal(samples[100], BitConverter.ToInt16(parsed.Pcm, 200));
        Assert.Equal(samples.Length / 44100.0, parsed.Seconds, 6);
        Assert.Null(WavFile.Parse([1, 2, 3]));
    }

    [Fact]
    public void Every_slot_ships_a_file_and_the_generated_ones_are_exactly_the_generators_output()
    {
        Assert.Equal(23, UiSoundCatalog.All.Count);
        foreach (var info in UiSoundCatalog.All)
            Assert.True(UiSoundFiles.Read(info.File) is not null, info.File + " is not shipped");
        foreach (var r in UiSoundSynth.Recipes)
            Assert.Equal(UiSoundSynth.Wav(UiSoundSynth.Render(r)), UiSoundFiles.Read(r.File));
        Assert.Null(UiSoundFiles.Read("ui-nothing.wav"));
    }
}
