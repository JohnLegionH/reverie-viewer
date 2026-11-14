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
    private const string Alert = "ui-alert.wav", Click = "ui-click.wav";

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

    [Fact]
    public void A_bell_recipes_sliders_are_its_sound_numbers_then_each_voices_numbers_with_ranges_holding_the_values()
    {
        var p = UiSoundParams.Of(Committed(Alert));
        Assert.Equal(["Ms", "LowPassHz", "FadeInMs", "FadeOutMs"], p.Take(4).Select(x => x.Key).ToArray());
        Assert.Equal(390, p.Single(x => x.Key == "Ms").Value);
        Assert.Equal(1047, p.Single(x => x.Key == "1.Hz").Value);
        Assert.Equal(2, p.Single(x => x.Key == "0.Ratio").Value);
        Assert.Contains(p, x => x.Key == "0.Index");
        Assert.DoesNotContain(p, x => x.Key == "0.LowHz");   // a bell has no noise band
        Assert.All(p, x => Assert.InRange(x.Value, x.Min, x.Max));
        var noise = UiSoundParams.Of(Committed(Click));
        Assert.Contains(noise, x => x.Key == "0.LowHz");
        Assert.DoesNotContain(noise, x => x.Key == "0.Ratio");
    }

    [Fact]
    public void Moving_one_slider_changes_that_number_only_and_is_clamped_to_its_range()
    {
        var alert = Committed(Alert);
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
        Assert.Equal(24, UiSoundSynth.Recipes.Count);   // the 23 interface sounds and our footstep
        foreach (var r in UiSoundSynth.Recipes)
        {
            var shipped = UiSoundFiles.Read(r.File);
            Assert.NotNull(shipped);
            Assert.Equal(shipped, tuning.WavFor(r.File));
            Assert.Equal(shipped, UiSoundSynth.Wav(UiSoundSynth.Render(r)));
        }
        var louder = UiSoundParams.With(Committed(Click), "0.Level", 0.9);
        string? changed = null;
        tuning.Changed += f => changed = f;
        tuning.Set(louder);
        Assert.Equal(Click, changed);
        Assert.True(tuning.IsTuned(Click));
        Assert.Equal(louder, tuning.Current(Click));
        Assert.Equal(UiSoundSynth.Wav(UiSoundSynth.Render(louder)), tuning.WavFor(Click));
        Assert.NotEqual(UiSoundFiles.Read(Click), tuning.WavFor(Click));
    }

    [Fact]
    public void Save_writes_the_user_file_a_new_tuning_reads_it_back_and_reset_returns_the_committed_values()
    {
        var folder = TestTemp.NewFolder("tuner");
        var path = Path.Combine(folder, UiSoundTuning.FileName);
        var tuning = new UiSoundTuning(path);
        var tuned = UiSoundParams.With(Committed(Alert), "0.Hz", 700);
        tuning.Set(tuned);
        Assert.True(tuning.Save());
        Assert.True(File.Exists(path));

        var again = new UiSoundTuning(path);
        Assert.True(again.IsTuned(Alert));
        Assert.Equal(tuned, again.Current(Alert));
        Assert.False(again.IsTuned(Click));

        again.Reset(Alert);
        Assert.False(again.IsTuned(Alert));
        Assert.Equal(Committed(Alert), again.Current(Alert));
        Assert.Equal(UiSoundFiles.Read(Alert), again.WavFor(Alert));
        Assert.False(new UiSoundTuning(path).IsTuned(Alert));   // the reset was saved
        TestTemp.Delete(folder);
    }

    [Fact]
    public void A_tuned_file_that_is_not_a_list_of_recipes_counts_as_no_tuning_and_unknown_files_are_ignored()
    {
        Assert.Empty(UiSoundTuning.Parse("not json"));
        Assert.Empty(UiSoundTuning.Parse(null));
        var json = UiSoundTuning.ToJson([UiSoundParams.With(Committed(Click), "Ms", 45), Committed(Alert) with { File = "ui-nothing.wav" }]);
        var parsed = UiSoundTuning.Parse(json);
        Assert.Equal([Click, "ui-nothing.wav"], parsed.Keys.Order().ToArray());
        Assert.Equal(45, parsed[Click].Ms);
        var folded = UiSoundTable.Fold(UiSoundSynth.Recipes, parsed);
        Assert.Equal(24, folded.Count);   // the 23 interface sounds and our footstep
        Assert.Equal(45, folded.Single(r => r.File == Click).Ms);
        Assert.Equal(Committed(Alert), folded.Single(r => r.File == Alert));
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

        var tuned = new Dictionary<string, SoundRecipe> { [Click] = UiSoundParams.With(Committed(Click), "Ms", 45) };
        var rewritten = UiSoundTable.ReplaceBlock(source, UiSoundTable.Format(UiSoundTable.Fold(UiSoundSynth.Recipes, tuned), nl));
        Assert.NotNull(rewritten);
        Assert.Contains("\"ui-click.wav\", 45, ", rewritten);
        Assert.Equal(source.Length - block!.Length, rewritten!.Length - UiSoundTable.BlockOf(rewritten)!.Length);
        Assert.Null(UiSoundTable.ReplaceBlock("no markers here", "x"));
    }
}
