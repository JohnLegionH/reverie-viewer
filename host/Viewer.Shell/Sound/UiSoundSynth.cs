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
public sealed record SoundRecipe(string File, double Ms, double LowPassHz, double FadeInMs, double FadeOutMs, int Seed, IReadOnlyList<Layer> Layers, string Note = "")
{
    // a record's own equality compares the list by reference; two recipes are the same when their numbers are
    public bool Equals(SoundRecipe? other) => other is not null && File == other.File && Ms == other.Ms && LowPassHz == other.LowPassHz
        && FadeInMs == other.FadeInMs && FadeOutMs == other.FadeOutMs && Seed == other.Seed && Note == other.Note && Layers.SequenceEqual(other.Layers);

    public override int GetHashCode() => HashCode.Combine(File, Ms, LowPassHz, Seed, Layers.Count);
}

/// <summary>
/// Makes the viewer's interface sounds from the recipes below, entirely from code: nothing is sampled or traced from another sound.
/// Short and soft: clicks under 60 ms, window and alert sounds under 400 ms, the teleport whoosh under 1.2 s, each with fades at both
/// ends, a gentle low-pass, and its peak brought to -6 dBFS. Written as 16-bit mono 44.1 kHz WAV. The same recipes give the same bytes
/// every run (the noise comes from a seeded generator, not the system's random source).
/// To change a sound, change its numbers in <see cref="Recipes"/> and run the generator again.
/// </summary>
public static class UiSoundSynth
{
    public const int Rate = 44100;
    public const double PeakDbfs = -6;

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

    /// <summary>
    /// The sounds. Each line: file, length (ms), low-pass (Hz), fade in and fade out (ms), noise seed, then the voices.
    /// Sine(start, attack, decay, level, Hz[, end Hz]); Bell(start, attack, decay, level, Hz, ratio, index);
    /// Noise(start, attack, decay, level, low Hz, high Hz); Swish(start, attack, decay, level, from Hz, to Hz).
    /// </summary>
    public static IReadOnlyList<SoundRecipe> Recipes { get; } =
    [
        // recipes:begin (this block is written by the generator's --fold; keep one recipe per R(...) entry)
        R("a soft tick: a short band of noise and a quick high blip", "ui-click.wav", 40, 7000, 1, 4, 11,
            Noise(0, 0.8, 12, 0.45, 1500, 6000),
            Sine(0, 0.8, 26, 0.8, 2200, 1800)),
        R("the release: the same idea, a little lower and softer", "ui-click-release.wav", 36, 6000, 1, 4, 12,
            Noise(0, 0.8, 10, 0.35, 1200, 5000),
            Sine(0, 0.8, 22, 0.7, 1700, 1500)),
        R("a refused key: a dull low knock", "ui-bad-keystroke.wav", 140, 2500, 0.5, 15, 13,
            Sine(0, 2, 110, 1, 180, 150),
            Sine(0, 2, 80, 0.4, 270, 225),
            Noise(0, 1, 20, 0.2, 200, 1500)),
        R("not allowed: two soft falling tones", "ui-invalid-op.wav", 270, 3000, 0.5, 25, 14,
            Sine(0, 3, 110, 1, 466),
            Sine(110, 3, 150, 1, 349)),
        R("an alert: a soft two-note chime", "ui-alert.wav", 390, 6000, 0.5, 40, 15,
            Bell(0, 2, 260, 1, 784, 2, 1.2),
            Bell(90, 2, 280, 0.8, 1047, 2, 1)),
        R("a window opening: a short rising swish under a rising tone", "ui-window-open.wav", 230, 5000, 1, 30, 16,
            Swish(0, 60, 160, 0.5, 500, 3500),
            Sine(0, 30, 180, 0.5, 520, 780)),
        R("a window closing: the same, falling", "ui-window-close.wav", 210, 5000, 1, 30, 17,
            Swish(0, 30, 150, 0.5, 3000, 600),
            Sine(0, 20, 160, 0.5, 700, 470)),
        R("a mention: a clear bell ding", "ui-chat-mention.wav", 380, 7000, 0.5, 40, 18,
            Bell(0, 1.5, 330, 1, 1175, 3.5, 1.5),
            Sine(0, 1, 120, 0.15, 2350)),
        R("a ping: a smaller, higher ding", "ui-chat-ping.wav", 320, 7000, 0.5, 40, 19,
            Bell(0, 1.5, 280, 1, 1397, 2.76, 1)),
        R("a new conversation: a soft bell ding with an upper partial", "ui-new-incoming-im-session.wav", 390, 6500, 0.5, 40, 20,
            Bell(0, 2, 340, 1, 880, 3.5, 2),
            Bell(0, 2, 250, 0.5, 1320, 3.5, 1.2)),
        R("starting a conversation: a short ding", "ui-start-im.wav", 280, 7000, 0.5, 35, 21,
            Bell(0, 2, 230, 1, 1047, 3.5, 1.4)),
        R("money in: light coin chimes going up", "ui-money-change-up.wav", 440, 8000, 0.5, 40, 22,
            Bell(0, 1, 250, 0.8, 1568, 3.5, 1.2),
            Bell(60, 1, 250, 0.8, 2093, 3.5, 1.2),
            Bell(120, 1, 280, 0.9, 2637, 3.5, 1.2)),
        R("money out: the coin chimes going down", "ui-money-change-down.wav", 440, 8000, 0.5, 40, 23,
            Bell(0, 1, 250, 0.9, 2093, 3.5, 1.2),
            Bell(60, 1, 250, 0.8, 1568, 3.5, 1.2),
            Bell(120, 1, 280, 0.8, 1175, 3.5, 1.2)),
        R("a camera shutter: two clicks of noise with a little body", "ui-snapshot.wav", 190, 8000, 1, 20, 24,
            Noise(0, 0.8, 18, 1, 1500, 7000),
            Noise(65, 0.8, 30, 0.9, 800, 5000),
            Sine(0, 1, 40, 0.3, 120, 90)),
        R("typing: a short soft patter of keys", "ui-typing.wav", 330, 6000, 1, 20, 25,
            Noise(0, 0.8, 14, 0.7, 1200, 5000),
            Sine(0, 0.5, 12, 0.3, 1100),
            Noise(75, 0.8, 14, 0.5, 1200, 5000),
            Sine(75, 0.5, 12, 0.2, 1000),
            Noise(140, 0.8, 14, 0.8, 1200, 5000),
            Sine(140, 0.5, 12, 0.3, 1150),
            Noise(230, 0.8, 14, 0.6, 1200, 5000),
            Sine(230, 0.5, 12, 0.25, 1050)),
        R("an object made: a small soft pop", "ui-object-create.wav", 150, 4000, 0.5, 20, 26,
            Sine(0, 1, 90, 1, 700, 220),
            Noise(0, 0.5, 15, 0.25, 300, 3000)),
        R("an object deleted: a lower pop with a puff of noise", "ui-object-delete.wav", 180, 3000, 0.5, 25, 27,
            Sine(0, 1, 130, 1, 380, 110),
            Noise(0, 1, 40, 0.35, 150, 1500)),
        R("an object rezzed: a small pop", "ui-object-rez-in.wav", 150, 4000, 0.5, 20, 28,
            Sine(0, 1, 100, 1, 520, 180),
            Noise(0, 0.5, 15, 0.25, 300, 3000)),
        R("an object taken: the pop turned upwards", "ui-object-rez-out.wav", 150, 4000, 1, 20, 29,
            Sine(0, 3, 100, 1, 200, 600)),
        R("a region restart warning: two calm falling notes", "ui-restart.wav", 400, 4500, 0.5, 40, 30,
            Bell(0, 3, 160, 1, 523, 1, 0.8),
            Bell(180, 3, 200, 1, 392, 1, 0.8)),
        R("a teleport: a whoosh of noise rising through its band, with a rising tone under it", "ui-teleport-out.wav", 1100, 6000, 2, 120, 31,
            Swish(0, 350, 700, 1, 300, 4000),
            Sine(0, 300, 750, 0.35, 180, 900)),
        R("pain: our file is a soft knock, not a voice (the grid's voice is the default for these two)", "ui-health-reduction-f.wav", 190, 3000, 0.5, 25, 32,
            Sine(0, 3, 140, 1, 330, 260),
            Sine(0, 3, 80, 0.3, 660, 520)),
        R("pain, the lower one: a lower soft knock", "ui-health-reduction-m.wav", 190, 3000, 0.5, 25, 33,
            Sine(0, 3, 140, 1, 200, 160),
            Sine(0, 3, 80, 0.3, 400, 320)),
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
