// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Viewer.Shell.Sound;

/// <summary>One number of a sound's recipe as the Sound Tuner shows it: its key ("Ms", "1.Hz" for the second voice's pitch), a label,
/// its value and the slider's range.</summary>
public sealed record UiSoundParam(string Key, string Label, double Value, double Min, double Max);

/// <summary>
/// The numbers of a recipe as sliders: the sound's length, low-pass and fades, then each voice's numbers that its kind uses (a sine's
/// start, attack, decay, level and pitches; a bell's ratio and index as well; a noise band's edges; a gliding band's start and end).
/// </summary>
public static class UiSoundParams
{
    private sealed record Spec(string Name, string Label, double Min, double Max, Func<SoundRecipe, double>? OfRecipe = null,
        Func<SoundRecipe, double, SoundRecipe>? SetRecipe = null, Func<Layer, double>? OfLayer = null, Func<Layer, double, Layer>? SetLayer = null);

    private static readonly Spec[] RecipeSpecs =
    [
        new("Ms", "Length (ms)", 5, 3000, r => r.Ms, (r, v) => r with { Ms = v }),
        new("LowPassHz", "Low-pass (Hz)", 200, 20000, r => r.LowPassHz, (r, v) => r with { LowPassHz = v }),
        new("FadeInMs", "Fade in (ms)", 0, 500, r => r.FadeInMs, (r, v) => r with { FadeInMs = v }),
        new("FadeOutMs", "Fade out (ms)", 0, 1000, r => r.FadeOutMs, (r, v) => r with { FadeOutMs = v }),
    ];

    private static readonly Spec Start = new("StartMs", "start (ms)", 0, 2000, OfLayer: l => l.StartMs, SetLayer: (l, v) => l with { StartMs = v });
    private static readonly Spec Attack = new("AttackMs", "attack (ms)", 0.1, 1000, OfLayer: l => l.AttackMs, SetLayer: (l, v) => l with { AttackMs = v });
    private static readonly Spec Decay = new("DecayMs", "decay (ms)", 1, 3000, OfLayer: l => l.DecayMs, SetLayer: (l, v) => l with { DecayMs = v });
    private static readonly Spec Level = new("Level", "level", 0, 2, OfLayer: l => l.Level, SetLayer: (l, v) => l with { Level = v });
    private static readonly Spec Hz = new("Hz", "pitch (Hz)", 20, 12000, OfLayer: l => l.Hz, SetLayer: (l, v) => l with { Hz = v });
    private static readonly Spec EndHz = new("EndHz", "end pitch (Hz, 0 stays)", 0, 12000, OfLayer: l => l.EndHz, SetLayer: (l, v) => l with { EndHz = v });
    private static readonly Spec Ratio = new("Ratio", "bell ratio", 0, 10, OfLayer: l => l.Ratio, SetLayer: (l, v) => l with { Ratio = v });
    private static readonly Spec Index = new("Index", "bell brightness", 0, 10, OfLayer: l => l.Index, SetLayer: (l, v) => l with { Index = v });
    private static readonly Spec LowHz = new("LowHz", "band low (Hz)", 0, 12000, OfLayer: l => l.LowHz, SetLayer: (l, v) => l with { LowHz = v });
    private static readonly Spec HighHz = new("HighHz", "band high (Hz)", 20, 20000, OfLayer: l => l.HighHz, SetLayer: (l, v) => l with { HighHz = v });
    private static readonly Spec SweepFrom = new("SweepFromHz", "glide from (Hz)", 20, 20000, OfLayer: l => l.SweepFromHz, SetLayer: (l, v) => l with { SweepFromHz = v });

    // the numbers each kind of voice uses (the synthesizer ignores the others)
    private static Spec[] LayerSpecs(Layer l) => l.Wave switch
    {
        Wave.Sine => [Start, Attack, Decay, Level, Hz, EndHz],
        Wave.Fm => [Start, Attack, Decay, Level, Hz, EndHz, Ratio, Index],
        _ when l.SweepFromHz > 0 => [Start, Attack, Decay, Level, SweepFrom, HighHz],
        _ => [Start, Attack, Decay, Level, LowHz, HighHz],
    };

    private static string Kind(Layer l) => l.Wave switch { Wave.Sine => "sine", Wave.Fm => "bell", _ when l.SweepFromHz > 0 => "swish", _ => "noise" };

    /// <summary>The recipe's numbers, in order.</summary>
    public static IReadOnlyList<UiSoundParam> Of(SoundRecipe recipe)
    {
        var list = RecipeSpecs.Select(s => new UiSoundParam(s.Name, s.Label, s.OfRecipe!(recipe), s.Min, Math.Max(s.Max, s.OfRecipe!(recipe)))).ToList();
        for (int i = 0; i < recipe.Layers.Count; i++)
        {
            var layer = recipe.Layers[i];
            foreach (var s in LayerSpecs(layer))
            {
                double v = s.OfLayer!(layer);
                list.Add(new UiSoundParam($"{i}.{s.Name}", $"Voice {i + 1} ({Kind(layer)}) {s.Label}", v, Math.Min(s.Min, v), Math.Max(s.Max, v)));
            }
        }
        return list;
    }

    /// <summary>The recipe with one number changed (clamped to its range); the same recipe for an unknown key.</summary>
    public static SoundRecipe With(SoundRecipe recipe, string key, double value)
    {
        if (!double.IsFinite(value))
            return recipe;
        var range = Of(recipe).FirstOrDefault(p => p.Key == key);
        if (range is null)
            return recipe;
        value = Math.Clamp(value, range.Min, range.Max);
        if (RecipeSpecs.FirstOrDefault(s => s.Name == key) is { } rs)
            return rs.SetRecipe!(recipe, value);
        int dot = key.IndexOf('.');
        int i = int.Parse(key[..dot], CultureInfo.InvariantCulture);
        var layer = recipe.Layers[i];
        var spec = LayerSpecs(layer).First(s => s.Name == key[(dot + 1)..]);
        var layers = recipe.Layers.ToArray();
        layers[i] = spec.SetLayer!(layer, value);
        return recipe with { Layers = layers };
    }
}

/// <summary>
/// The committed table of recipes as source text, and folding tuned recipes into it: the generator's --fold rewrites the block between
/// the "recipes:begin" and "recipes:end" lines of UiSoundSynth.cs with <see cref="Format"/>, so tuned values become the shipped sounds.
/// </summary>
public static class UiSoundTable
{
    private const string Begin = "// recipes:begin", End = "// recipes:end";

    /// <summary>The committed recipes with the tuned ones in their places (by file); tuned files the table does not have are ignored.</summary>
    public static IReadOnlyList<SoundRecipe> Fold(IReadOnlyList<SoundRecipe> committed, IReadOnlyDictionary<string, SoundRecipe> tuned) =>
        committed.Select(r => tuned.TryGetValue(r.File, out var t) ? t with { File = r.File } : r).ToArray();

    /// <summary>The block's lines for these recipes (between the markers, markers excluded).</summary>
    public static string Format(IReadOnlyList<SoundRecipe> recipes, string newline = "\n")
    {
        var sb = new StringBuilder();
        foreach (var r in recipes)
        {
            sb.Append("        R(").Append(Text(r.Note)).Append(", ").Append(Text(r.File)).Append(", ")
                .Append(string.Join(", ", N(r.Ms), N(r.LowPassHz), N(r.FadeInMs), N(r.FadeOutMs), r.Seed.ToString(CultureInfo.InvariantCulture))).Append(',').Append(newline);
            for (int i = 0; i < r.Layers.Count; i++)
                sb.Append("            ").Append(Call(r.Layers[i])).Append(i == r.Layers.Count - 1 ? ")," : ",").Append(newline);
        }
        return sb.ToString();
    }

    // a voice as the table's helper that makes it, or the full constructor when no helper fits
    private static string Call(Layer l)
    {
        string head = string.Join(", ", N(l.StartMs), N(l.AttackMs), N(l.DecayMs), N(l.Level));
        bool none(params double[] v) => v.All(x => x == 0);
        return l.Wave switch
        {
            Wave.Sine when none(l.Ratio, l.Index, l.LowHz, l.HighHz, l.SweepFromHz) =>
                $"Sine({head}, {N(l.Hz)}{(l.EndHz != 0 ? ", " + N(l.EndHz) : "")})",
            Wave.Fm when none(l.EndHz, l.LowHz, l.HighHz, l.SweepFromHz) => $"Bell({head}, {N(l.Hz)}, {N(l.Ratio)}, {N(l.Index)})",
            Wave.Noise when l.SweepFromHz == 0 && none(l.Hz, l.EndHz, l.Ratio, l.Index) => $"Noise({head}, {N(l.LowHz)}, {N(l.HighHz)})",
            Wave.Noise when l.SweepFromHz > 0 && none(l.Hz, l.EndHz, l.Ratio, l.Index, l.LowHz) => $"Swish({head}, {N(l.SweepFromHz)}, {N(l.HighHz)})",
            _ => $"new Layer(Wave.{l.Wave}, {head}, Hz: {N(l.Hz)}, EndHz: {N(l.EndHz)}, Ratio: {N(l.Ratio)}, Index: {N(l.Index)}, " +
                 $"LowHz: {N(l.LowHz)}, HighHz: {N(l.HighHz)}, SweepFromHz: {N(l.SweepFromHz)})",
        };
    }

    // the shortest text that reads back to the same number, so a folded table makes the same bytes the tuner played
    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string Text(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static bool Span(string source, out int start, out int end)
    {
        start = end = -1;
        int b = source.IndexOf(Begin, StringComparison.Ordinal);
        int e = source.IndexOf(End, StringComparison.Ordinal);
        if (b < 0 || e < b)
            return false;
        start = source.IndexOf('\n', b) + 1;                // the line after the begin marker
        end = source.LastIndexOf('\n', e) + 1;              // the start of the end marker's line
        return start > 0 && end >= start;
    }

    /// <summary>The source text with its block replaced; null when the markers are missing.</summary>
    public static string? ReplaceBlock(string source, string block) =>
        Span(source, out int start, out int end) ? source[..start] + block + source[end..] : null;

    /// <summary>The block now in a source text (null without markers).</summary>
    public static string? BlockOf(string source) => Span(source, out int start, out int end) ? source[start..end] : null;
}

/// <summary>
/// The Sound Tuner's saved values: tuned recipes in the viewer's user folder (ui-sounds-tuned.json), by file. A tuned sound plays
/// from its tuned recipe, made by the same synthesizer the generator uses; any other plays its shipped file. Save writes the file;
/// Reset puts one sound back to its committed recipe and saves. A file that cannot be read counts as no tuning. Main thread only.
/// </summary>
public sealed class UiSoundTuning
{
    /// <summary>The file's name in the user folder.</summary>
    public const string FileName = "ui-sounds-tuned.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.Strict,
    };

    private readonly string? _path;
    private readonly Dictionary<string, SoundRecipe> _tuned = new();
    private readonly Dictionary<string, byte[]> _wavs = new();

    /// <summary>Reads the tuned values at <paramref name="path"/> (null: nothing is ever read or saved).</summary>
    public UiSoundTuning(string? path)
    {
        _path = path;
        string? text = null;
        try
        {
            if (path is not null && File.Exists(path))
                text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            text = null;
        }
        foreach (var (file, recipe) in Parse(text))
            if (Committed(file) is not null)
                _tuned[file] = recipe;
    }

    /// <summary>Raised with a sound's file when what it plays changed (tuned, saved, reset).</summary>
    public event Action<string>? Changed;

    /// <summary>The committed recipe for a file (null: not one of ours).</summary>
    public static SoundRecipe? Committed(string file) => UiSoundSynth.Recipes.FirstOrDefault(r => r.File == file);

    /// <summary>The recipe a file plays now: the tuned one, else the committed one.</summary>
    public SoundRecipe? Current(string file) => _tuned.TryGetValue(file, out var t) ? t : Committed(file);

    /// <summary>Whether a file plays a tuned recipe.</summary>
    public bool IsTuned(string file) => _tuned.ContainsKey(file);

    /// <summary>The tuned recipes, by file.</summary>
    public IReadOnlyDictionary<string, SoundRecipe> Tuned => _tuned;

    /// <summary>Tunes a sound (in memory until <see cref="Save"/>).</summary>
    public void Set(SoundRecipe recipe)
    {
        if (Committed(recipe.File) is not { } committed)
            return;
        if (recipe.Equals(committed))
            _tuned.Remove(recipe.File);
        else
            _tuned[recipe.File] = recipe;
        _wavs.Remove(recipe.File);
        Changed?.Invoke(recipe.File);
    }

    /// <summary>Writes every tuned recipe to the file. False when it could not be written (or there is no file).</summary>
    public bool Save()
    {
        if (_path is null)
            return false;
        try
        {
            if (Path.GetDirectoryName(_path) is { Length: > 0 } dir)
                Directory.CreateDirectory(dir);
            var temp = _path + ".new";
            File.WriteAllText(temp, ToJson(_tuned.Values.OrderBy(r => r.File, StringComparer.Ordinal)));
            File.Move(temp, _path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Puts a sound back to its committed recipe and saves.</summary>
    public void Reset(string file)
    {
        if (!_tuned.Remove(file))
            return;
        _wavs.Remove(file);
        Save();
        Changed?.Invoke(file);
    }

    /// <summary>The WAV bytes a file plays now: made from its tuned recipe, or the shipped file.</summary>
    public byte[]? WavFor(string file)
    {
        if (!_tuned.TryGetValue(file, out var recipe))
            return UiSoundFiles.Read(file);
        if (!_wavs.TryGetValue(file, out var wav))
            _wavs[file] = wav = UiSoundSynth.Wav(UiSoundSynth.Render(recipe));
        return wav;
    }

    /// <summary>Tuned recipes from the file's text (an empty set for text that is not a list of recipes).</summary>
    public static IReadOnlyDictionary<string, SoundRecipe> Parse(string? json)
    {
        var found = new Dictionary<string, SoundRecipe>();
        if (string.IsNullOrWhiteSpace(json))
            return found;
        try
        {
            if (JsonSerializer.Deserialize<List<SoundRecipe>>(json, Json) is { } list)
                foreach (var r in list)
                    if (r is { File.Length: > 0, Layers: not null } && r.Layers.All(l => l is not null))
                        found[r.File] = r with { Note = r.Note ?? "" };
        }
        catch (JsonException)
        {
            found.Clear();
        }
        return found;
    }

    /// <summary>The file's text for these tuned recipes.</summary>
    public static string ToJson(IEnumerable<SoundRecipe> recipes) => JsonSerializer.Serialize(recipes.ToList(), Json);
}
