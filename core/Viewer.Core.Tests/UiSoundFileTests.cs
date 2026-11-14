// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later


using Viewer.Shell.Sound;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Our interface sound files: the generator makes every slot's file, the same bytes each run, as 16-bit mono 44.1 kHz WAVs peaking at
/// about -6 dBFS with soft ends and the lengths the interface calls for; the files shipped in the shell assembly are exactly its output;
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

    private static double Ms(short[] s) => s.Length * 1000.0 / UiSoundSynth.Rate;

    [Fact]
    public void The_generator_has_one_recipe_for_each_slots_file_and_one_for_our_footstep()
    {
        Assert.Equal(UiSoundCatalog.All.Select(i => i.File).Append("world-footstep.wav").Order(), UiSoundSynth.Recipes.Select(r => r.File).Order());
    }

    [Fact]
    public void Two_runs_of_the_generator_give_byte_identical_files()
    {
        Assert.Equal(24, UiSoundSynth.Recipes.Count);
        foreach (var r in UiSoundSynth.Recipes)
        {
            var a = UiSoundSynth.Wav(UiSoundSynth.Render(r));
            var b = UiSoundSynth.Wav(UiSoundSynth.Render(r));
            Assert.NotEmpty(a);
            Assert.Equal(a, b);
        }
    }

    [Fact]
    public void Every_sound_peaks_at_about_minus_6_dbfs_without_clipping_and_starts_and_ends_softly()
    {
        Assert.Equal(24, UiSoundSynth.Recipes.Count);
        foreach (var r in UiSoundSynth.Recipes)
        {
            var s = UiSoundSynth.Render(r);
            Assert.True(s.Length > 0, r.File);
            int peak = s.Max(v => Math.Abs((int)v));
            double dbfs = 20 * Math.Log10(peak / 32767.0);
            Assert.InRange(dbfs, -6.2, -5.8);
            Assert.True(Math.Abs((int)s[0]) < 100 && Math.Abs((int)s[^1]) < 100, r.File + " ends hard");
            Assert.True(s.Take(10).All(v => Math.Abs((int)v) < 2000), r.File + " starts with a jump");
            Assert.True(s.TakeLast(10).All(v => Math.Abs((int)v) < 2000), r.File + " stops with a jump");
        }
    }

    [Fact]
    public void Clicks_are_short_windows_and_alerts_are_under_400_ms_and_the_teleport_under_1200_ms()
    {
        foreach (var slot in new[] { UiSoundSlot.Click, UiSoundSlot.ClickRelease })
            Assert.InRange(Ms(Samples(slot)), 10, 60);
        foreach (var slot in new[] { UiSoundSlot.WindowOpen, UiSoundSlot.WindowClose, UiSoundSlot.Alert, UiSoundSlot.InvalidOp, UiSoundSlot.BadKeystroke,
                     UiSoundSlot.ChatMention, UiSoundSlot.ChatPing, UiSoundSlot.NewIncomingIMSession, UiSoundSlot.StartIM, UiSoundSlot.Restart })
            Assert.InRange(Ms(Samples(slot)), 30, 400);
        Assert.InRange(Ms(Samples(UiSoundSlot.TeleportOut)), 600, 1200);
        Assert.All(UiSoundSynth.Recipes, r => Assert.InRange(Ms(UiSoundSynth.Render(r)), 10, 1200));
    }

    [Fact]
    public void A_wav_is_16_bit_mono_at_44100_and_reads_back_to_the_same_samples()
    {
        var samples = Samples(UiSoundSlot.Alert);
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
    public void The_files_the_viewer_ships_are_exactly_the_generators_output()
    {
        Assert.Equal(23, UiSoundCatalog.All.Count);
        foreach (var info in UiSoundCatalog.All)
        {
            var shipped = UiSoundFiles.Read(info.File);
            Assert.True(shipped is not null, info.File + " is not shipped");
            Assert.Equal(UiSoundSynth.Wav(Samples(info.Slot)), shipped);
        }
        Assert.Null(UiSoundFiles.Read("ui-nothing.wav"));
    }
}
