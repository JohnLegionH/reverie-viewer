// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell.Sound;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The Sound Tuner's model: a recipe's numbers as sliders, changing one, saving the tuned recipes to the user folder and reading them
/// back, Reset, the viewer playing a tuned sound from the shared synthesizer, folding tuned recipes into the committed table, and the
/// committed table's source block being exactly what the fold writes. Each test that writes uses its own temp folder, so the class runs
/// in parallel.
/// </summary>
public class UiSoundTunerTests
{
    // two sounds the generator still makes: an FM voice and a noise band
    private const string Bell = "ui-health-reduction-m.wav", Noisy = "ui-typing.wav";

    private static SoundRecipe Committed(string file)
    {
        var r = UiSoundTuning.Committed(file);
        Assert.True(r is not null, "no committed recipe for " + file);
        return r!;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Viewer.sln")))
                return dir.FullName;
        throw new InvalidOperationException("no repository root above the test");
    }

    // a two-bell chime, fixed here (the committed sounds are fitted, and their numbers change when they are refitted)
    private static readonly SoundRecipe Chime = new(Bell, 390, 6000, 0.5, 40, 15,
    [
        new Layer(Wave.Fm, 0, 2, 260, 1, Hz: 784, Ratio: 2, Index: 1.2),
        new Layer(Wave.Fm, 90, 2, 280, 0.8, Hz: 1047, Ratio: 2, Index: 1),
    ], "a two-note chime");

    [Fact]
    public void A_bell_recipes_sliders_are_its_sound_numbers_then_each_voices_numbers_with_ranges_holding_the_values()
    {
        var p = UiSoundParams.Of(Chime);
        Assert.Equal(["Ms", "LowPassHz", "FadeInMs", "FadeOutMs"], p.Take(4).Select(x => x.Key).ToArray());
        Assert.Equal(390, p.Single(x => x.Key == "Ms").Value);
        Assert.Equal(1047, p.Single(x => x.Key == "1.Hz").Value);
        Assert.Equal(2, p.Single(x => x.Key == "0.Ratio").Value);
        Assert.Contains(p, x => x.Key == "0.Index");
        Assert.DoesNotContain(p, x => x.Key == "0.LowHz");   // a bell has no noise band
        Assert.All(p, x => Assert.InRange(x.Value, x.Min, x.Max));
        var noise = UiSoundParams.Of(Committed(Noisy));
        Assert.Contains(noise, x => x.Key == "0.LowHz");
        Assert.DoesNotContain(noise, x => x.Key == "0.Ratio");
    }

    [Fact]
    public void Moving_one_slider_changes_that_number_only_and_is_clamped_to_its_range()
    {
        var alert = Chime;
        var changed = UiSoundParams.With(alert, "1.Hz", 1200);
        Assert.Equal(1200, changed.Layers[1].Hz);
        Assert.Equal(alert.Layers[0], changed.Layers[0]);
        Assert.Equal(alert with { Layers = changed.Layers }, changed);
        Assert.Equal(alert.Ms, changed.Ms);
        var longer = UiSoundParams.With(alert, "Ms", 999_999);
        Assert.Equal(UiSoundParams.Of(alert).Single(x => x.Key == "Ms").Max, longer.Ms);
        Assert.NotEqual(UiSoundSynth.Wav(UiSoundSynth.Render(alert)), UiSoundSynth.Wav(UiSoundSynth.Render(changed)));
    }

    [Fact]
    public void The_viewer_plays_exactly_the_shipped_files_untuned_and_a_tuned_sound_from_the_shared_synthesizer()
    {
        var tuning = new UiSoundTuning(null);
        Assert.Equal(7, UiSoundSynth.Recipes.Count);   // the 6 generated interface sounds and our footstep
        foreach (var r in UiSoundSynth.Recipes)
        {
            var shipped = UiSoundFiles.Read(r.File);
            Assert.NotNull(shipped);
            Assert.Equal(shipped, tuning.WavFor(r.File));
            Assert.Equal(shipped, UiSoundSynth.Wav(UiSoundSynth.Render(r)));
        }
        // a fitted sound is brought to its loudness target, so a change of length, not of level, is what makes new bytes
        var louder = UiSoundParams.With(Committed(Noisy), "0.DecayMs", 20);
        string? changed = null;
        tuning.Changed += f => changed = f;
        tuning.Set(louder);
        Assert.Equal(Noisy, changed);
        Assert.True(tuning.IsTuned(Noisy));
        Assert.Equal(louder, tuning.Current(Noisy));
        Assert.Equal(UiSoundSynth.Wav(UiSoundSynth.Render(louder)), tuning.WavFor(Noisy));
        Assert.NotEqual(UiSoundFiles.Read(Noisy), tuning.WavFor(Noisy));
    }

    [Fact]
    public void Save_writes_the_user_file_a_new_tuning_reads_it_back_and_reset_returns_the_committed_values()
    {
        var folder = TestTemp.NewFolder("tuner");
        var path = Path.Combine(folder, UiSoundTuning.FileName);
        var tuning = new UiSoundTuning(path);
        var tuned = UiSoundParams.With(Committed(Bell), "0.Hz", 700);
        tuning.Set(tuned);
        Assert.True(tuning.Save());
        Assert.True(File.Exists(path));

        var again = new UiSoundTuning(path);
        Assert.True(again.IsTuned(Bell));
        Assert.Equal(tuned, again.Current(Bell));
        Assert.False(again.IsTuned(Noisy));

        again.Reset(Bell);
        Assert.False(again.IsTuned(Bell));
        Assert.Equal(Committed(Bell), again.Current(Bell));
        Assert.Equal(UiSoundFiles.Read(Bell), again.WavFor(Bell));
        Assert.False(new UiSoundTuning(path).IsTuned(Bell));   // the reset was saved
        TestTemp.Delete(folder);
    }

    [Fact]
    public void A_saved_tuning_of_a_sound_that_is_now_a_recording_is_ignored_and_the_recording_plays()
    {
        var folder = TestTemp.NewFolder("tuner-recorded");
        var path = Path.Combine(folder, UiSoundTuning.FileName);
        // a user file saved when the click was still generated
        var oldClick = new SoundRecipe("ui-click.wav", 46, 12000, 1, 3.68, 104, [new Layer(Wave.Noise, 0, 0.5, 67.5, 1, LowHz: 805, HighHz: 3218)], "old click");
        File.WriteAllText(path, UiSoundTuning.ToJson([oldClick]));
        var tuning = new UiSoundTuning(path);
        Assert.True(UiSoundRecordings.IsRecorded("ui-click.wav"));
        Assert.False(tuning.IsTuned("ui-click.wav"));
        Assert.Equal(UiSoundFiles.Read("ui-click.wav"), tuning.WavFor("ui-click.wav"));
        TestTemp.Delete(folder);
    }

    [Fact]
    public void A_tuned_file_that_is_not_a_list_of_recipes_counts_as_no_tuning_and_unknown_files_are_ignored()
    {
        Assert.Empty(UiSoundTuning.Parse("not json"));
        Assert.Empty(UiSoundTuning.Parse(null));
        var json = UiSoundTuning.ToJson([UiSoundParams.With(Committed(Noisy), "Ms", 45), Committed(Bell) with { File = "ui-nothing.wav" }]);
        var parsed = UiSoundTuning.Parse(json);
        Assert.Equal(["ui-nothing.wav", Noisy], parsed.Keys.Order().ToArray());
        Assert.Equal(45, parsed[Noisy].Ms);
        var folded = UiSoundTable.Fold(UiSoundSynth.Recipes, parsed);
        Assert.Equal(7, folded.Count);   // the 6 generated interface sounds and our footstep
        Assert.Equal(45, folded.Single(r => r.File == Noisy).Ms);
        Assert.Equal(Committed(Bell), folded.Single(r => r.File == Bell));
        Assert.DoesNotContain(folded, r => r.File == "ui-nothing.wav");
    }

    [Fact]
    public void The_committed_tables_source_block_is_exactly_what_the_fold_writes_and_a_fold_rewrites_only_the_block()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "host", "Viewer.Shell", "Sound", "UiSoundSynth.cs"));
        string nl = source.Contains("\r\n") ? "\r\n" : "\n";
        var block = UiSoundTable.BlockOf(source);
        Assert.NotNull(block);
        Assert.Equal(block, UiSoundTable.Format(UiSoundSynth.Recipes, nl));

        var tuned = new Dictionary<string, SoundRecipe> { [Noisy] = UiSoundParams.With(Committed(Noisy), "Ms", 45) };
        var rewritten = UiSoundTable.ReplaceBlock(source, UiSoundTable.Format(UiSoundTable.Fold(UiSoundSynth.Recipes, tuned), nl));
        Assert.NotNull(rewritten);
        string lufs = Committed(Noisy).Lufs.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains("\"" + Noisy + "\", " + (Committed(Noisy).Lufs != 0 ? lufs + ", " : "") + "45, ", rewritten);
        Assert.Equal(source.Length - block!.Length, rewritten!.Length - UiSoundTable.BlockOf(rewritten)!.Length);
        Assert.Null(UiSoundTable.ReplaceBlock("no markers here", "x"));
    }
}
