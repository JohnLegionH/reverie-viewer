// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Assets;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Sound;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The fitted interface sounds and the per-grid choice: the generator fits two candidates to each measured target, the same every run;
/// the committed recipes are the chosen candidates of the committed targets, one per official UISnd* event, each at its target's
/// loudness with the peak no higher than -1 dBFS and the target's length; the loudness measure reads a known tone as BS.1770 does; and
/// on Second Life every slot plays the grid's sound by its official id, elsewhere our own. Reads two committed files under tools/ui-sounds;
/// nothing is written or shared, so the class runs in parallel.
/// </summary>
public class UiSoundFitTests
{
    private static string Tool(string name) => Path.Combine(TestPaths.RepoRoot, "tools", "ui-sounds", name);

    private static IReadOnlyList<SoundTarget> Targets()
    {
        Assert.True(File.Exists(Tool("sl-targets.json")), "the committed targets are missing");
        return UiSoundFit.ParseTargets(File.ReadAllText(Tool("sl-targets.json")));
    }

    private static readonly SoundTarget Bell = new("UISndAlert", 0.6, 0.58, 724, [7278, 8786, 12963], 0, 150, 515, 8155, 99, -17.5, -14, 1);
    private static readonly SoundTarget Hiss = new("UISndObjectRezIn", 1.8, 1.8, null, [6901, 11466], 135, 1185, 1640, 5790, 0, -23, 0, 3);

    [Fact]
    public void The_generator_gives_the_same_candidates_and_the_same_bytes_twice()
    {
        foreach (var t in new[] { Bell, Hiss })
        {
            var one = UiSoundFit.Candidates(t, 7);
            var two = UiSoundFit.Candidates(t, 7);
            Assert.Equal(one.A, two.A);
            Assert.Equal(one.B, two.B);
            Assert.NotEqual(one.A, one.B);
            Assert.Equal(UiSoundSynth.Wav(UiSoundSynth.Render(one.A)), UiSoundSynth.Wav(UiSoundSynth.Render(two.A)));
            Assert.Equal(UiSoundSynth.Wav(UiSoundSynth.Render(one.B)), UiSoundSynth.Wav(UiSoundSynth.Render(two.B)));
        }
    }

    [Fact]
    public void A_tonal_target_is_made_of_partials_and_a_noisy_one_of_noise()
    {
        var bell = UiSoundFit.Candidates(Bell, 1);
        Assert.All(bell.A.Layers, l => Assert.Equal(Wave.Sine, l.Wave));
        Assert.Contains(bell.B.Layers, l => l.Wave == Wave.Fm);
        var hiss = UiSoundFit.Candidates(Hiss, 1);
        Assert.All(hiss.A.Layers.Concat(hiss.B.Layers), l => Assert.Equal(Wave.Noise, l.Wave));
        Assert.Equal(3, hiss.A.Layers.Count);   // one burst for each measured burst
    }

    [Fact]
    public void A_one_kilohertz_tone_at_minus_20_dbfs_reads_about_minus_23_lufs()
    {
        var x = new double[44100 * 2];
        for (int i = 0; i < x.Length; i++)
            x[i] = 0.1 * Math.Sin(2 * Math.PI * 997 * i / 44100.0);
        Assert.InRange(Loudness.Integrated(x, 44100), -23.3, -22.7);
        Assert.Equal(double.NegativeInfinity, Loudness.Integrated(new double[44100], 44100));
    }

    [Fact]
    public void Every_event_but_the_silent_one_plays_a_sound_fitted_to_its_official_setting()
    {
        var targets = Targets();
        Assert.Equal(22, targets.Count);   // 15 measured settings and the 7 that were not cached
        Assert.Equal(7, targets.Count(t => !t.Reference));
        foreach (var info in UiSoundCatalog.All.Where(i => i.Slot != UiSoundSlot.ObjectRezOut))
        {
            string setting = UiSoundCatalog.OfficialName(info.Slot);
            var recipe = UiSoundSynth.Recipes.Single(r => r.File == info.File);
            Assert.Contains(setting, recipe.Note);
            Assert.True(recipe.Lufs < 0, info.File + " has no loudness target");
            Assert.Equal(targets.Single(t => t.Setting == setting).Lufs, recipe.Lufs);
        }
    }

    [Fact]
    public void The_committed_recipes_are_the_chosen_candidates_of_the_committed_targets()
    {
        Assert.True(File.Exists(Tool("sl-choices.txt")), "the committed choices are missing");
        var chosen = UiSoundFit.ParseChoices(File.ReadAllText(Tool("sl-choices.txt")));
        var targets = Targets();
        Assert.Equal(targets.Select(t => t.Setting).Order(), chosen.Keys.Order());
        foreach (var t in targets)
        {
            var (a, b) = UiSoundFit.Candidates(t, UiSoundFit.SeedOf(t.Setting));
            var want = chosen[t.Setting] == 'B' ? b : a;
            Assert.Equal(want, UiSoundSynth.Recipes.Single(r => r.File == want.File));
        }
    }

    [Fact]
    public void Fitted_sounds_reach_their_loudness_or_the_minus_1_dbfs_ceiling_and_keep_their_targets_length()
    {
        var targets = Targets();
        Assert.Equal(22, UiSoundSynth.Recipes.Count(r => r.Lufs != 0));
        foreach (var r in UiSoundSynth.Recipes.Where(r => r.Lufs != 0))
        {
            var s = UiSoundSynth.Render(r);
            double peak = 20 * Math.Log10(s.Max(v => Math.Abs((int)v)) / 32767.0);
            double lufs = Loudness.Integrated(s.Select(v => v / 32767.0).ToArray(), UiSoundSynth.Rate);
            Assert.True(peak <= -0.95, r.File + " peaks at " + peak);
            Assert.True(Math.Abs(lufs - r.Lufs) < 0.3 || peak > -1.1, r.File + ": " + lufs + " LUFS against " + r.Lufs);
            var t = targets.Single(t => UiSoundFit.FileOf(t.Setting) == r.File);
            Assert.InRange(s.Length * 1000.0 / UiSoundSynth.Rate, t.LengthS * 1000 - 1, t.LengthS * 1000 + 1);
        }
    }

    [Fact]
    public void A_very_quiet_voice_with_a_loudness_target_is_still_brought_to_that_loudness()
    {
        var quiet = new SoundRecipe("ui-click.wav", 300, 8000, 1, 10, 3, [new Layer(Wave.Sine, 0, 2, 250, 0.0005, Hz: 1000)], "quiet", -20);
        var s = UiSoundSynth.Render(quiet);
        double lufs = Loudness.Integrated(s.Select(v => v / 32767.0).ToArray(), UiSoundSynth.Rate);
        Assert.InRange(lufs, -20.5, -19.5);
    }

    // ---------------- the per-grid choice ----------------

    private sealed class MemoryStore : ISettingsStore
    {
        private string? _text;
        public string? Read() => _text;
        public bool Write(string text) { _text = text; return true; }
    }

    private sealed class Output : IUiSoundOutput
    {
        public readonly List<string> Plays = new();
        public void PlayOurs(UiSoundSlot slot, float gain) => Plays.Add("ours " + slot);
        public bool PlayGrid(UiSoundSlot slot, Guid id, byte[] ogg, float gain) { Plays.Add("grid " + slot); return true; }
    }

    [Fact]
    public void Our_own_interface_sounds_are_declared_off_on_second_life_and_on_everywhere_else()
    {
        Assert.Equal(FeatureReach.OpenSimOnly, GridPolicy.ReachOf(ViewerFeature.OwnInterfaceSounds));
        var settings = new SettingsService(new MemoryStore());
        foreach (var info in UiSoundCatalog.All)
        {
            Assert.Equal(UiSoundSource.Grid, UiSoundSettings.SourceOn(settings, info.Slot, GridKind.SecondLife));
            Assert.Equal(UiSoundSource.Ours, UiSoundSettings.SourceOn(settings, info.Slot, GridKind.OpenSim));
        }
        // a slot switched off stays off on either grid; one set to ours plays the grid's on Second Life
        UiSoundSettings.SetSource(settings, UiSoundSlot.Click, UiSoundSource.Off);
        UiSoundSettings.SetSource(settings, UiSoundSlot.Alert, UiSoundSource.Ours);
        Assert.Equal(UiSoundSource.Off, UiSoundSettings.SourceOn(settings, UiSoundSlot.Click, GridKind.SecondLife));
        Assert.Equal(UiSoundSource.Grid, UiSoundSettings.SourceOn(settings, UiSoundSlot.Alert, GridKind.SecondLife));
        Assert.Equal(UiSoundSource.Ours, UiSoundSettings.SourceOn(settings, UiSoundSlot.Alert, GridKind.OpenSim));
    }

    [Fact]
    public void On_second_life_an_event_asks_the_grid_for_the_official_sound_id_and_elsewhere_plays_ours()
    {
        var fetched = new List<Guid>();
        var output = new Output();
        var settings = new SettingsService(new MemoryStore());
        var grid = new GridSounds((id, ct) =>
        {
            fetched.Add(id);
            return Task.FromResult(AssetFetchResult.Of(AssetFetchStatus.NotFound));
        });
        var sounds = new UiSounds(settings, output, grid, () => false, () => 0) { InWorld = true, Grid = GridKind.SecondLife };
        Assert.Equal(UiSoundOutcome.Fetching, sounds.Play(UiSoundSlot.WindowOpen));
        Assert.Equal([UiSoundCatalog.Of(UiSoundSlot.WindowOpen).GridId], fetched);
        Assert.Equal(UiSoundOutcome.NoGridSound, sounds.Play(UiSoundSlot.ObjectRezOut));   // the official viewer plays nothing here
        Assert.Empty(output.Plays);

        sounds.Grid = GridKind.OpenSim;
        Assert.Equal(UiSoundOutcome.PlayedOurs, sounds.Play(UiSoundSlot.WindowOpen));
        Assert.Equal(UiSoundOutcome.PlayedOurs, sounds.Play(UiSoundSlot.HealthReductionM));
        Assert.Equal(["ours WindowOpen", "ours HealthReductionM"], output.Plays);
    }
}
