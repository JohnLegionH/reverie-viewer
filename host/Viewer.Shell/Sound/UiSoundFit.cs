// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using System.Text.Json;

namespace Viewer.Shell.Sound;

/// <summary>
/// The measured numbers of one of the official viewer's interface sounds, the target a recipe is fitted to. Only numbers: its length,
/// where its pitch and strongest partials lie, its attack and decay, how bright it is (spectral centroid), how tonal, how loud, how far
/// its pitch moves, and in how many separate bursts it comes. <see cref="Reference"/> is false for a sound with no measurement of its
/// own, whose numbers were derived from a neighbour (see <see cref="UiSoundFit.Derive"/>).
/// </summary>
public sealed record SoundTarget(string Setting, double LengthS, double ActiveS, double? F0Hz, IReadOnlyList<double> PartialsHz,
    double AttackMs, double Decay20Ms, double Decay40Ms, double CentroidHz, double TonalPct, double Lufs, double PitchMoveSt, int Events,
    bool Reference = true, string Neighbour = "");

/// <summary>
/// Fits recipes to measured targets: two candidates per target, built differently, so the one closer to the target by measurement can be
/// chosen. A tonal target (tonal share 50 % or more) is made of synthesized partials, a noisy one of shaped noise; both keep the
/// target's length, attack, decay, brightness, burst count and pitch movement, and the loudness target goes into the recipe.
/// Candidate A: tonal, plain sines on the measured partials; noisy, noise in a fixed band topped at the measured brightness, with a
/// held low tone where the target has a low partial.
/// Candidate B: tonal, an FM bell on the lowest partial with the next as its ratio; noisy, noise in a band that glides the way the pitch
/// moves, with a low thump where the target has low partials. Everything is computed from the numbers, so the same targets give the
/// same recipes, and so the same bytes, every run.
/// </summary>
public static class UiSoundFit
{
    /// <summary>The highest frequency a gliding noise band may reach.</summary>
    public const double SweepCeilingHz = 7000;

    /// <summary>The setting ("UISndAlert") to our file ("ui-alert.wav").</summary>
    public static string FileOf(string setting) =>
        UiSoundCatalog.All.First(i => UiSoundCatalog.OfficialName(i.Slot) == setting).File;

    /// <summary>Both candidates for a target.</summary>
    public static (SoundRecipe A, SoundRecipe B) Candidates(SoundTarget t, int seed)
    {
        bool tonal = t.TonalPct >= 50;
        string file = FileOf(t.Setting);
        double ms = Math.Round(t.LengthS * 1000);
        double attack = Math.Clamp(t.AttackMs, 0.5, ms * 0.6);
        // the measured -40 dB time, stretched to the recipe's -60 dB decay time (exponential decay: 60/40)
        double decay = Math.Max(5, Math.Max(t.Decay40Ms, t.Decay20Ms) * 1.5);
        int events = Math.Clamp(t.Events, 1, 6);
        double spacing = events > 1 ? t.ActiveS * 1000 / events : 0;
        double burstDecay = events > 1 ? Math.Min(decay, Math.Max(spacing * 1.2, 20)) : decay;
        double fadeIn = Math.Min(1, ms / 20), fadeOut = Math.Clamp(ms * 0.08, 3, 120);
        double lowPass = Math.Clamp(t.CentroidHz * 4, 2000, 18000);
        string note = (t.Reference ? "fitted to the measured " : "no SL reference; in the style of " + t.Neighbour + ", fitted to ")
                      + t.Setting + (tonal ? ", tonal" : ", noisy");

        var a = new List<Layer>();
        var b = new List<Layer>();
        var partials = t.PartialsHz.Where(p => p is > 30 and < 18000).Take(4).ToArray();
        double moveRatio = Math.Pow(2, Math.Clamp(t.PitchMoveSt, -24, 24) / 12);
        for (int e = 0; e < events; e++)
        {
            double start = e * spacing, level = 1 - 0.15 * e;
            if (tonal)
            {
                double f0 = t.F0Hz ?? partials.DefaultIfEmpty(440).Min();
                var tones = partials.Length > 0 ? partials : [f0];
                for (int i = 0; i < tones.Length; i++)
                {
                    double hz = tones[i];
                    a.Add(new Layer(Wave.Sine, start, attack, burstDecay * (1 - 0.15 * i), level * (1 - 0.2 * i), Hz: hz,
                        EndHz: moveRatio != 1 ? hz * moveRatio : 0));
                }
                double low = tones.Min();
                double ratio = tones.Length > 1 ? Math.Round(tones.Where(h => h > low).DefaultIfEmpty(low * 2).Min() / low, 2) : 2;
                double index = Math.Clamp(t.CentroidHz / low / 2, 0.3, 6);
                b.Add(new Layer(Wave.Fm, start, attack, burstDecay, level, Hz: low, EndHz: moveRatio != 1 ? low * moveRatio : 0, Ratio: ratio, Index: index));
            }
            else
            {
                double c = Math.Max(80, t.CentroidHz);
                // one-pole edges slope gently, so the band's top sits at the brightness to keep the centroid near it
                a.Add(new Layer(Wave.Noise, start, attack, burstDecay, level, LowHz: Math.Round(c / 4), HighHz: Math.Round(Math.Min(19000, c))));
                double from = moveRatio >= 1 ? c / 2 : c * 2, to = moveRatio >= 1 ? c * 2 : c / 2;
                if (moveRatio == 1)
                    (from, to) = (c * 1.5, c / 1.5);
                // the gliding band's filter (a state-variable filter) is stable only well under a sixth of the sample rate
                b.Add(new Layer(Wave.Noise, start, attack, burstDecay, level, HighHz: Math.Round(Math.Min(SweepCeilingHz, to)), SweepFromHz: Math.Round(Math.Min(SweepCeilingHz, from))));
                if (partials.FirstOrDefault(p => p < 400) is var thump and > 0)
                {
                    // a low partial under the noise: a short thump in B, a held low tone in A
                    b.Add(new Layer(Wave.Sine, start, Math.Min(attack, 5), Math.Min(burstDecay, 120), level * 0.6, Hz: thump, EndHz: Math.Round(thump * 0.7)));
                    a.Add(new Layer(Wave.Sine, start, attack, burstDecay * 0.7, level * 0.12, Hz: thump));
                }
            }
            // the share that is not tonal, as a quiet band of noise around the brightness
            if (tonal && t.TonalPct < 95)
            {
                double share = (100 - t.TonalPct) / 100;
                var n = new Layer(Wave.Noise, start, attack, burstDecay * 0.5, level * share,
                    LowHz: Math.Round(t.CentroidHz / 2), HighHz: Math.Round(Math.Min(19000, t.CentroidHz * 2)));
                a.Add(n);
                b.Add(n);
            }
        }
        return (new SoundRecipe(file, ms, lowPass, fadeIn, fadeOut, seed, a, note + ", candidate A", t.Lufs),
                new SoundRecipe(file, ms, lowPass, fadeIn, fadeOut, seed, b, note + ", candidate B", t.Lufs));
    }

    /// <summary>
    /// The sounds with no measurement of their own, each made in the style of its nearest measured neighbour: the neighbour's numbers
    /// with the pitch scaled, the length scaled, and the pitch movement replaced where the event calls for a direction.
    /// </summary>
    public static IReadOnlyList<(string Setting, string Neighbour, double PitchScale, double LengthScale, double? PitchMoveSt)> Unmeasured { get; } =
    [
        ("UISndChatMention", "UISndNewIncomingIMSession", 1.26, 0.8, null),   // a chime like the new conversation's, a third higher
        ("UISndChatPing", "UISndNewIncomingIMSession", 1.5, 0.6, null),       // smaller and higher
        ("UISndHealthReductionF", "UISndHealthReductionM", 1.6, 1.0, null),   // the same voice-like sound, higher
        ("UISndMoneyChangeUp", "UISndAlert", 1.0, 1.0, 5),                    // a bright bell, rising
        ("UISndMoneyChangeDown", "UISndAlert", 0.75, 1.0, -5),                // the same bell, lower and falling
        ("UISndObjectDelete", "UISndObjectCreate", 1.0, 0.5, null),          // the creation whoosh, shorter
        ("UISndRestart", "UISndInvalidOp", 1.5, 1.6, -3),                     // a longer, falling warning tone
    ];

    /// <summary>A target for an unmeasured sound from its neighbour's.</summary>
    public static SoundTarget Derive(SoundTarget neighbour, string setting, double pitchScale, double lengthScale, double? pitchMoveSt) =>
        neighbour with
        {
            Setting = setting,
            LengthS = Math.Round(neighbour.LengthS * lengthScale, 3),
            ActiveS = Math.Round(neighbour.ActiveS * lengthScale, 3),
            F0Hz = neighbour.F0Hz * pitchScale,
            PartialsHz = neighbour.PartialsHz.Select(p => Math.Round(p * pitchScale, 1)).ToArray(),
            AttackMs = neighbour.AttackMs * Math.Min(1, lengthScale),
            Decay20Ms = neighbour.Decay20Ms * lengthScale,
            Decay40Ms = neighbour.Decay40Ms * lengthScale,
            CentroidHz = Math.Round(neighbour.CentroidHz * pitchScale),
            PitchMoveSt = pitchMoveSt ?? neighbour.PitchMoveSt,
            Reference = false,
            Neighbour = neighbour.Setting,
        };

    /// <summary>
    /// Reads the targets file: an object keyed by setting, each value holding the measured numbers under the names the measuring
    /// script writes (length_s, active_s, f0_hz, partials_hz, attack_ms, decay20_ms, decay40_ms, centroid_hz, tonal_pct, lufs,
    /// pitch_move_st, events). The unmeasured sounds are added from their neighbours. Ordered as the catalog is.
    /// </summary>
    public static IReadOnlyList<SoundTarget> ParseTargets(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var measured = new Dictionary<string, SoundTarget>();
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            var v = p.Value;
            double D(string k) => v.GetProperty(k).GetDouble();
            measured[p.Name] = new SoundTarget(p.Name, D("length_s"), D("active_s"),
                v.GetProperty("f0_hz").ValueKind == JsonValueKind.Number ? D("f0_hz") : null,
                v.GetProperty("partials_hz").EnumerateArray().Select(e => e.GetDouble()).ToArray(),
                D("attack_ms"), D("decay20_ms"), D("decay40_ms"), D("centroid_hz"), D("tonal_pct"), D("lufs"), D("pitch_move_st"),
                v.GetProperty("events").GetInt32());
        }
        foreach (var u in Unmeasured)
            if (!measured.ContainsKey(u.Setting) && measured.TryGetValue(u.Neighbour, out var n))
                measured[u.Setting] = Derive(n, u.Setting, u.PitchScale, u.LengthScale, u.PitchMoveSt);
        return UiSoundCatalog.All.Select(i => UiSoundCatalog.OfficialName(i.Slot)).Where(measured.ContainsKey).Select(s => measured[s]).ToArray();
    }

    /// <summary>The noise seed of a slot's fitted recipe (fixed per slot, so a refit does not change other sounds).</summary>
    public static int SeedOf(string setting) => 100 + UiSoundCatalog.All.ToList().FindIndex(i => UiSoundCatalog.OfficialName(i.Slot) == setting);

    /// <summary>The chosen candidate of every fitted slot: the file "UISndAlert=A" lines (# starts a comment).</summary>
    public static IReadOnlyDictionary<string, char> ParseChoices(string text) =>
        text.Split('\n').Select(l => l.Split('#')[0].Trim()).Where(l => l.Contains('='))
            .Select(l => l.Split('=', 2)).ToDictionary(p => p[0].Trim(), p => char.ToUpper(p[1].Trim()[0], CultureInfo.InvariantCulture));
}
