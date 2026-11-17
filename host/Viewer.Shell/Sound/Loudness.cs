// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Sound;

/// <summary>
/// Integrated loudness in LUFS, as ITU-R BS.1770-4 defines it: the K-weighting filter (a high shelf, then a high-pass), mean square
/// over 400 ms blocks that overlap by 75 %, an absolute gate at -70 LUFS and a relative gate 10 LU under the gated mean. The two
/// filters' coefficients are worked out for any sample rate from their analog prototypes (centre frequency, gain and Q), the method
/// pyloudnorm and libebur128 use. A sound shorter than one block is padded with silence to 400 ms, so a short click still has a value.
/// Mono only.
/// </summary>
public static class Loudness
{
    /// <summary>The integrated loudness of mono samples (-1 to 1) at <paramref name="rate"/>; negative infinity for silence.</summary>
    public static double Integrated(ReadOnlySpan<double> samples, int rate)
    {
        int block = (int)Math.Round(0.4 * rate);
        var x = new double[Math.Max(samples.Length, block)];
        samples.CopyTo(x);
        KWeight(x, rate);

        int hop = block / 4;
        var z = new List<double>();
        for (int s = 0; s + block <= x.Length; s += hop)
        {
            double sum = 0;
            for (int i = s; i < s + block; i++)
                sum += x[i] * x[i];
            z.Add(sum / block);
        }
        static double L(double ms) => -0.691 + 10 * Math.Log10(ms);
        var kept = z.Where(v => v > 0 && L(v) > -70).ToList();
        if (kept.Count == 0)
            return double.NegativeInfinity;
        double relative = L(kept.Average()) - 10;
        kept = kept.Where(v => L(v) > relative).ToList();
        return kept.Count == 0 ? double.NegativeInfinity : L(kept.Average());
    }

    // the two K-weighting stages, in place
    private static void KWeight(double[] x, int rate)
    {
        // stage 1: the high shelf (+4 dB above about 1.7 kHz), the head's effect
        double f0 = 1681.974450955533, g = 3.999843853973347, q = 0.7071752369554196;
        double k = Math.Tan(Math.PI * f0 / rate);
        double vh = Math.Pow(10, g / 20), vb = Math.Pow(vh, 0.4996667741545416);
        double a0 = 1 + k / q + k * k;
        Biquad(x, (vh + vb * k / q + k * k) / a0, 2 * (k * k - vh) / a0, (vh - vb * k / q + k * k) / a0,
            2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);
        // stage 2: the RLB high-pass at about 38 Hz
        f0 = 38.13547087602444;
        q = 0.5003270373238773;
        k = Math.Tan(Math.PI * f0 / rate);
        double d = 1 + k / q + k * k;
        Biquad(x, 1, -2, 1, 2 * (k * k - 1) / d, (1 - k / q + k * k) / d);
    }

    private static void Biquad(double[] x, double b0, double b1, double b2, double a1, double a2)
    {
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double y = b0 * x[i] + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1;
            x1 = x[i];
            y2 = y1;
            y1 = y;
            x[i] = y;
        }
    }
}
