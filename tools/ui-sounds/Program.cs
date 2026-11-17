// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell.Sound;

namespace UiSoundGen;

/// <summary>
/// Writes every interface sound into a folder: by default the shell's sounds folder in this repository (found by walking up from the
/// program to the folder holding Viewer.sln), or the folder given as the last argument. It writes only the files its recipes name and
/// prints each one's length and peak.
/// With <c>--fold &lt;ui-sounds-tuned.json&gt;</c> (the file the viewer's Sound Tuner saves in the user folder) it first puts the tuned
/// recipes into the committed table, rewriting the recipe block of UiSoundSynth.cs, and then writes the sounds from the folded table, so
/// the tuned sounds become the shipped ones.
/// With <c>--fit &lt;targets.json&gt;</c> it fits recipes to measured targets (<see cref="UiSoundFit"/>): with
/// <c>--candidates &lt;folder&gt;</c> it writes both candidates of every target there (ui-alert.A.wav, ui-alert.B.wav) and nothing else;
/// with <c>--choose &lt;choices.txt&gt;</c> it puts the chosen candidate of each into the committed table, as --fold does, and writes the sounds.
/// </summary>
public static class Program
{
    public const string SoundsFolder = "host/Viewer.Shell/Assets/Sounds";
    public const string TableSource = "host/Viewer.Shell/Sound/UiSoundSynth.cs";

    public static int Main(string[] args)
    {
        IReadOnlyDictionary<string, SoundRecipe>? folding = null;
        string? fold = null, fit = null, candidates = null, choose = null;
        var rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--fold" && i + 1 < args.Length)
                fold = args[++i];
            else if (args[i] == "--fit" && i + 1 < args.Length)
                fit = args[++i];
            else if (args[i] == "--candidates" && i + 1 < args.Length)
                candidates = args[++i];
            else if (args[i] == "--choose" && i + 1 < args.Length)
                choose = args[++i];
            else
                rest.Add(args[i]);
        }
        if (fit is not null)
        {
            var targets = UiSoundFit.ParseTargets(File.ReadAllText(fit));
            if (candidates is not null)
            {
                Directory.CreateDirectory(candidates);
                // the targets as fitted (the unmeasured ones derived), for whoever compares the candidates with them
                File.WriteAllText(Path.Combine(candidates, "targets.json"),
                    System.Text.Json.JsonSerializer.Serialize(targets, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                foreach (var t in targets)
                {
                    var (a, b) = UiSoundFit.Candidates(t, UiSoundFit.SeedOf(t.Setting));
                    foreach (var (r, tag) in new[] { (a, "A"), (b, "B") })
                    {
                        var s = UiSoundSynth.Render(r);
                        File.WriteAllBytes(Path.Combine(candidates, r.File.Replace(".wav", "." + tag + ".wav")), UiSoundSynth.Wav(s));
                    }
                    Console.WriteLine(t.Setting + (t.Reference ? "" : ": no SL reference, in the style of " + t.Neighbour));
                }
                return 0;
            }
            if (choose is null)
            {
                Console.Error.WriteLine("usage: UiSoundGen --fit targets.json (--candidates folder | --choose choices.txt) [folder]");
                return 2;
            }
            var chosen = UiSoundFit.ParseChoices(File.ReadAllText(choose));
            var fitted = new Dictionary<string, SoundRecipe>();
            foreach (var t in targets)
            {
                if (!chosen.TryGetValue(t.Setting, out char c))
                    continue;
                var (a, b) = UiSoundFit.Candidates(t, UiSoundFit.SeedOf(t.Setting));
                fitted[a.File] = c == 'B' ? b : a;
            }
            fold = null;
            folding = fitted;
        }
        string? root = RepoRoot();
        string? folder = rest.Count switch
        {
            0 => root is not null ? Path.Combine(root, SoundsFolder) : null,
            1 => Path.GetFullPath(rest[0]),
            _ => null,
        };
        if (folder is null || ((fold is not null || folding is not null) && root is null))
        {
            Console.Error.WriteLine("usage: UiSoundGen [--fold ui-sounds-tuned.json] [folder]   (no folder: the shell's sounds folder in this repository)");
            return 2;
        }

        IReadOnlyList<SoundRecipe> recipes = UiSoundSynth.Recipes;
        if (fold is not null || folding is not null)
        {
            var tuned = folding ?? UiSoundTuning.Parse(File.Exists(fold) ? File.ReadAllText(fold) : null);
            if (tuned.Count == 0)
            {
                Console.Error.WriteLine("no tuned recipes in " + fold);
                return 2;
            }
            recipes = UiSoundTable.Fold(recipes, tuned);
            string source = Path.Combine(root!, TableSource);
            string text = File.ReadAllText(source);
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            if (UiSoundTable.ReplaceBlock(text, UiSoundTable.Format(recipes, nl)) is not { } rewritten)
            {
                Console.Error.WriteLine("the recipe block's markers are missing in " + source);
                return 2;
            }
            File.WriteAllText(source, rewritten);
            foreach (var file in tuned.Keys.Where(f => recipes.Any(r => r.File == f)).Order())
                Console.WriteLine("folded " + file);
        }

        Directory.CreateDirectory(folder);
        foreach (var recipe in recipes)
        {
            var samples = UiSoundSynth.Render(recipe);
            File.WriteAllBytes(Path.Combine(folder, recipe.File), UiSoundSynth.Wav(samples));
            int peak = samples.Max(s => Math.Abs((int)s));
            Console.WriteLine(FormattableString.Invariant($"{recipe.File}	{samples.Length * 1000.0 / UiSoundSynth.Rate:0.0} ms	peak {20 * Math.Log10(peak / 32767.0):0.00} dBFS"));
        }
        return 0;
    }

    private static string? RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Viewer.sln")))
                return dir.FullName;
        return null;
    }
}
