// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Sound;

/// <summary>What a layer is made of: a sine (with an optional pitch glide), an FM bell (a sine whose phase another sine moves, the
/// movement fading with the note, which gives the bright start and pure tail of a bell), or noise through a filter (a fixed band, or a
/// band that glides, for swishes and the teleport whoosh).</summary>
public enum Wave { Sine, Fm, Noise }

/// <summary>
/// One voice of a sound. Times in milliseconds, pitches in hertz.
/// </summary>
/// <param name="Wave">What the voice is.</param>
/// <param name="StartMs">When it begins, from the start of the sound.</param>
/// <param name="AttackMs">How long it takes to rise from silence (a soft curve, never a step).</param>
/// <param name="DecayMs">How long it takes from its peak down to -60 dB.</param>
/// <param name="Level">Its loudness against the other voices (the whole sound is brought to -6 dBFS afterwards).</param>
/// <param name="Hz">Sine and FM: the starting pitch.</param>
/// <param name="EndHz">Sine and FM: the pitch it glides to over its attack and decay (0: it stays).</param>
/// <param name="Ratio">FM: the modulator's pitch as a multiple of the carrier's.</param>
/// <param name="Index">FM: how far the modulator moves the carrier at the start (it fades with the note).</param>
/// <param name="LowHz">Noise: the low edge of its band (a gentle high-pass).</param>
/// <param name="HighHz">Noise: the high edge of its band (a gentle low-pass); for a gliding band, where the band ends.</param>
/// <param name="SweepFromHz">Noise: where a gliding band starts (0: a fixed band).</param>
public sealed record Layer(Wave Wave, double StartMs, double AttackMs, double DecayMs, double Level,
    double Hz = 0, double EndHz = 0, double Ratio = 0, double Index = 0, double LowHz = 0, double HighHz = 0, double SweepFromHz = 0);

/// <summary>One sound: its file, its length, the gentle low-pass over the whole of it, its fades at both ends, the seed of its noise,
/// its voices, and a short note on what it is.</summary>
public sealed record SoundRecipe(string File, double Ms, double LowPassHz, double FadeInMs, double FadeOutMs, int Seed, IReadOnlyList<Layer> Layers, string Note = "",
    double Lufs = 0)
{
    // a record's own equality compares the list by reference; two recipes are the same when their numbers are
    public bool Equals(SoundRecipe? other) => other is not null && File == other.File && Ms == other.Ms && LowPassHz == other.LowPassHz
        && FadeInMs == other.FadeInMs && FadeOutMs == other.FadeOutMs && Seed == other.Seed && Note == other.Note && Lufs == other.Lufs
        && Layers.SequenceEqual(other.Layers);

    public override int GetHashCode() => HashCode.Combine(File, Ms, LowPassHz, Seed, Layers.Count);
}

/// <summary>
/// Makes the viewer's generated sounds from the recipes below, entirely from code: nothing is sampled or traced from another sound.
/// These are our footstep and the interface sounds not shipped as recordings (<see cref="UiSoundRecordings"/>). They are written by the generator's fit (tools/ui-sounds, <c>--fit</c>) from measured numbers of the official
/// viewer's sounds (length, pitch and partials, attack and decay, brightness, loudness); only numbers are used, never the sounds
/// themselves. Each has fades at both ends and a gentle low-pass. A recipe with a loudness target (<see cref="SoundRecipe.Lufs"/>) is
/// brought to that integrated loudness, its peak never above -1 dBFS; one without is brought to a -6 dBFS peak. Written as 16-bit mono
/// 44.1 kHz WAV. The same recipes give the same bytes every run (the noise comes from a seeded generator, not the system's random source).
/// To change a sound, change its numbers in <see cref="Recipes"/> and run the generator again.
/// </summary>
public static class UiSoundSynth
{
    public const int Rate = 44100;
    public const double PeakDbfs = -6;

    /// <summary>The highest peak a recipe with a loudness target may reach.</summary>
    public const double CeilingDbfs = -1;

    private static Layer Sine(double startMs, double attackMs, double decayMs, double level, double hz, double endHz = 0) =>
        new(Wave.Sine, startMs, attackMs, decayMs, level, Hz: hz, EndHz: endHz);

    private static Layer Bell(double startMs, double attackMs, double decayMs, double level, double hz, double ratio, double index) =>
        new(Wave.Fm, startMs, attackMs, decayMs, level, Hz: hz, Ratio: ratio, Index: index);

    private static Layer Noise(double startMs, double attackMs, double decayMs, double level, double lowHz, double highHz) =>
        new(Wave.Noise, startMs, attackMs, decayMs, level, LowHz: lowHz, HighHz: highHz);

    private static Layer Swish(double startMs, double attackMs, double decayMs, double level, double fromHz, double toHz) =>
        new(Wave.Noise, startMs, attackMs, decayMs, level, HighHz: toHz, SweepFromHz: fromHz);

    private static SoundRecipe R(string note, string file, double ms, double lowPassHz, double fadeInMs, double fadeOutMs, int seed, params Layer[] layers) =>
        new(file, ms, lowPassHz, fadeInMs, fadeOutMs, seed, layers, note);

    // a recipe with a loudness target (LUFS) instead of the -6 dBFS peak
    private static SoundRecipe L(string note, string file, double lufs, double ms, double lowPassHz, double fadeInMs, double fadeOutMs, int seed, params Layer[] layers) =>
        new(file, ms, lowPassHz, fadeInMs, fadeOutMs, seed, layers, note, lufs);

    /// <summary>
    /// The sounds. Each line: file, length (ms), low-pass (Hz), fade in and fade out (ms), noise seed, then the voices.
    /// Sine(start, attack, decay, level, Hz[, end Hz]); Bell(start, attack, decay, level, Hz, ratio, index);
    /// Noise(start, attack, decay, level, low Hz, high Hz); Swish(start, attack, decay, level, from Hz, to Hz).
    /// </summary>
    public static IReadOnlyList<SoundRecipe> Recipes { get; } =
    [
        // recipes:begin (this block is written by the generator's --fold; keep one recipe per R(...) entry)
        L("no SL reference; in the style of UISndNewIncomingIMSession, fitted to UISndChatPing, tonal, candidate A", "ui-chat-ping.wav", -21.538, 426, 16808, 1, 34.08, 103,
            Sine(0, 0.5, 257.4, 1, 5830, 1457.5),
            Sine(0, 0.5, 218.78999999999996, 0.8, 1017.4, 254.35),
            Sine(0, 0.5, 180.17999999999998, 0.6, 12613.1, 3153.275),
            Sine(0, 0.5, 141.57, 0.3999999999999999, 3149.2, 787.3),
            Noise(0, 0.5, 128.7, 0.27796000000000004, 2101, 8404),
            Sine(214.5, 0.5, 257.4, 0.85, 5830, 1457.5),
            Sine(214.5, 0.5, 218.78999999999996, 0.68, 1017.4, 254.35),
            Sine(214.5, 0.5, 180.17999999999998, 0.51, 12613.1, 3153.275),
            Sine(214.5, 0.5, 141.57, 0.3399999999999999, 3149.2, 787.3),
            Noise(214.5, 0.5, 128.7, 0.23626600000000003, 2101, 8404)),
        L("fitted to the measured UISndSnapshot, noisy, candidate B", "ui-snapshot.wav", -16.828, 973, 13687.392, 1, 77.84, 117,
            Swish(0, 245, 378, 1, 1711, 6844),
            Swish(315, 245, 378, 0.85, 1711, 6844),
            Swish(630, 245, 378, 0.7, 1711, 6844)),
        L("fitted to the measured UISndTyping, noisy, candidate A", "ui-typing.wav", -24.335, 1050, 2286.16, 1, 84, 120,
            Noise(0, 10, 242.39999999999998, 1, 143, 572),
            Sine(0, 10, 169.67999999999998, 0.12, 107.7),
            Noise(202, 10, 242.39999999999998, 0.85, 143, 572),
            Sine(202, 10, 169.67999999999998, 0.102, 107.7),
            Noise(404, 10, 242.39999999999998, 0.7, 143, 572),
            Sine(404, 10, 169.67999999999998, 0.08399999999999999, 107.7),
            Noise(606, 10, 242.39999999999998, 0.55, 143, 572),
            Sine(606, 10, 169.67999999999998, 0.066, 107.7),
            Noise(808, 10, 242.39999999999998, 0.4, 143, 572),
            Sine(808, 10, 169.67999999999998, 0.048, 107.7)),
        L("no SL reference; in the style of UISndInvalidOp, fitted to UISndRestart, tonal, candidate A", "ui-restart.wav", -4.895, 706, 2168, 1, 56.480000000000004, 116,
            Sine(0, 140, 492, 1, 306.9, 258.07110984136494),
            Sine(0, 140, 418.2, 0.8, 549.2, 461.82031125734005),
            Sine(0, 140, 344.4, 0.6, 4748.1, 3992.6602692661622),
            Sine(0, 140, 270.6, 0.3999999999999999, 1485.8, 1249.403893783969),
            Noise(0, 140, 246, 0.18161000000000002, 271, 1084)),
        L("no SL reference; in the style of UISndHealthReductionM, fitted to UISndHealthReductionF, tonal, candidate B", "ui-health-reduction-f.wav", -10.65, 557, 3440, 1, 44.56, 106,
            new Layer(Wave.Fm, 0, 200, 333, 1, Hz: 499.5, EndHz: 713.5764540760407, Ratio: 2.97, Index: 0.8608608608608609, LowHz: 0, HighHz: 0, SweepFromHz: 0),
            Noise(0, 200, 166.5, 0.29897999999999997, 430, 1720),
            new Layer(Wave.Fm, 277.5, 200, 333, 0.85, Hz: 499.5, EndHz: 713.5764540760407, Ratio: 2.97, Index: 0.8608608608608609, LowHz: 0, HighHz: 0, SweepFromHz: 0),
            Noise(277.5, 200, 166.5, 0.25413299999999994, 430, 1720)),
        L("fitted to the measured UISndHealthReductionM, tonal, candidate B", "ui-health-reduction-m.wav", -10.65, 557, 2149.364, 1, 44.56, 107,
            new Layer(Wave.Fm, 0, 200, 333, 1, Hz: 312.2, EndHz: 446.00314106614593, Ratio: 2.97, Index: 0.8605717488789238, LowHz: 0, HighHz: 0, SweepFromHz: 0),
            Noise(0, 200, 166.5, 0.29897999999999997, 269, 1075),
            new Layer(Wave.Fm, 277.5, 200, 333, 0.85, Hz: 312.2, EndHz: 446.00314106614593, Ratio: 2.97, Index: 0.8605717488789238, LowHz: 0, HighHz: 0, SweepFromHz: 0),
            Noise(277.5, 200, 166.5, 0.25413299999999994, 269, 1075)),
        R("a footstep, for a grid without the step sounds: a soft low thud with a short scuff of noise", "world-footstep.wav", 120, 2500, 1, 30, 34,
            Sine(0, 2, 60, 1, 110, 70),
            Noise(0, 3, 35, 0.6, 200, 1800)),
        // recipes:end
    ];

    /// <summary>The sound's samples, peak at <see cref="PeakDbfs"/>.</summary>
    public static short[] Render(SoundRecipe recipe)
    {
        int n = (int)Math.Round(recipe.Ms * Rate / 1000.0);
        var mix = new double[n];
        var noise = new Noise64((ulong)recipe.Seed);
        foreach (var layer in recipe.Layers)
            AddLayer(mix, layer, noise);

        // the gentle low-pass over the whole sound (one pole, 6 dB an octave)
        double a = 1 - Math.Exp(-2 * Math.PI * recipe.LowPassHz / Rate), y = 0;
        for (int i = 0; i < n; i++)
            mix[i] = y += a * (mix[i] - y);

        // soft ends: raised-cosine fades, so the first and the last sample are silence
        int fadeIn = Math.Max(1, (int)Math.Round(recipe.FadeInMs * Rate / 1000.0));
        int fadeOut = Math.Max(1, (int)Math.Round(recipe.FadeOutMs * Rate / 1000.0));
        for (int i = 0; i < n; i++)
        {
            double g = 1;
            if (i < fadeIn)
                g *= 0.5 - 0.5 * Math.Cos(Math.PI * i / fadeIn);
            int fromEnd = n - 1 - i;
            if (fromEnd < fadeOut)
                g *= 0.5 - 0.5 * Math.Cos(Math.PI * fromEnd / fadeOut);
            mix[i] *= g;
        }

        double peak = mix.Max(Math.Abs);
        double scale = peak > 0 ? Math.Pow(10, PeakDbfs / 20) / peak : 0;
        if (recipe.Lufs != 0 && peak > 0)
        {
            // to the target loudness, but never past the ceiling; measured with the peak at full scale first, so the absolute gate
            // (-70 LUFS) does not silence a quiet mix
            for (int i = 0; i < n; i++)
                mix[i] /= peak;
            peak = 1;
            double now = Loudness.Integrated(mix, Rate);
            scale = double.IsFinite(now) ? Math.Pow(10, (recipe.Lufs - now) / 20) : 0;
            scale = Math.Min(scale, Math.Pow(10, CeilingDbfs / 20) / peak);
        }
        var samples = new short[n];
        for (int i = 0; i < n; i++)
            samples[i] = (short)Math.Round(mix[i] * scale * 32767, MidpointRounding.AwayFromZero);
        return samples;
    }

    private static void AddLayer(double[] mix, Layer l, Noise64 noise)
    {
        int start = (int)Math.Round(l.StartMs * Rate / 1000.0);
        double attack = Math.Max(l.AttackMs, 0.01) / 1000.0, decay = Math.Max(l.DecayMs, 0.01) / 1000.0;
        double tau = decay / 6.907755;   // -60 dB at the decay time
        double phase = 0, modPhase = 0;
        double hp = 0, lastIn = 0, lp = 0;         // a fixed band: one-pole high-pass, then one-pole low-pass
        double svLow = 0, svBand = 0;              // a gliding band: a state-variable band-pass
        for (int i = start; i < mix.Length; i++)
        {
            double t = (i - start) / (double)Rate;
            double env = t < attack ? 0.5 - 0.5 * Math.Cos(Math.PI * t / attack) : Math.Exp(-(t - attack) / tau);
            if (t > attack && env < 1e-5)
                break;
            double progress = Math.Min(1, t / (attack + decay));
            double v;
            switch (l.Wave)
            {
                case Wave.Sine:
                {
                    double f = l.EndHz > 0 ? l.Hz * Math.Pow(l.EndHz / l.Hz, progress) : l.Hz;
                    phase += 2 * Math.PI * f / Rate;
                    v = Math.Sin(phase);
                    break;
                }
                case Wave.Fm:
                {
                    double f = l.EndHz > 0 ? l.Hz * Math.Pow(l.EndHz / l.Hz, progress) : l.Hz;
                    phase += 2 * Math.PI * f / Rate;
                    modPhase += 2 * Math.PI * f * l.Ratio / Rate;
                    v = Math.Sin(phase + l.Index * env * Math.Sin(modPhase));
                    break;
                }
                default:
                {
                    double x = noise.Next();
                    if (l.SweepFromHz > 0)
                    {
                        double fc = l.SweepFromHz * Math.Pow(l.HighHz / l.SweepFromHz, progress);
                        double k = 2 * Math.Sin(Math.PI * fc / Rate);
                        svLow += k * svBand;
                        double high = x - svLow - svBand;
                        svBand += k * high;
                        v = svBand;
                    }
                    else
                    {
                        double ah = Math.Exp(-2 * Math.PI * l.LowHz / Rate);
                        hp = ah * (hp + x - lastIn);
                        lastIn = x;
                        double al = 1 - Math.Exp(-2 * Math.PI * l.HighHz / Rate);
                        lp += al * (hp - lp);
                        v = lp;
                    }
                    break;
                }
            }
            mix[i] += l.Level * env * v;
        }
    }

    /// <summary>A 16-bit mono PCM WAV file of the samples at <see cref="Rate"/>.</summary>
    public static byte[] Wav(short[] samples)
    {
        int data = samples.Length * 2;
        using var ms = new MemoryStream(44 + data);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + data);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);          // PCM
        w.Write((short)1);          // mono
        w.Write(Rate);
        w.Write(Rate * 2);          // bytes a second
        w.Write((short)2);          // bytes a frame
        w.Write((short)16);         // bits a sample
        w.Write("data"u8);
        w.Write(data);
        foreach (var s in samples)
            w.Write(s);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>A seeded noise source (xorshift64*): the same seed gives the same noise on every machine and every run.</summary>
    private sealed class Noise64(ulong seed)
    {
        private ulong _s = seed * 0x9E3779B97F4A7C15UL + 1;

        /// <summary>The next value, spread evenly over -1 to 1.</summary>
        public double Next()
        {
            _s ^= _s >> 12;
            _s ^= _s << 25;
            _s ^= _s >> 27;
            ulong r = _s * 0x2545F4914F6CDD1DUL;
            return (r >> 11) * (1.0 / (1UL << 53)) * 2 - 1;
        }
    }
}
