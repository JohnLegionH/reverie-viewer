// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// The declared L4 tolerance (the test strategy 3.3, appearance class). A pixel "differs" when any of its R, G, B channels is
/// more than <see cref="ChannelThreshold"/> (0-255) away from the golden; the image passes when at most
/// <see cref="MaxDifferingFraction"/> of its pixels differ AND the mean absolute channel error is at most
/// <see cref="MaxMeanAbsError"/>. Alpha is ignored (the render target is opaque).
/// </summary>
public sealed record DiffTolerance(int ChannelThreshold, double MaxDifferingFraction, double MaxMeanAbsError)
{
    /// <summary>Terrain goldens: allows driver-level rasterisation noise on edges, not a changed hill.</summary>
    public static DiffTolerance Terrain { get; } = new(ChannelThreshold: 16, MaxDifferingFraction: 0.002, MaxMeanAbsError: 0.5);
}

public sealed record DiffResult(bool Pass, int Width, int Height, int Differing, double DifferingFraction, double MeanAbsError, int MaxChannelError, string? Problem)
{
    public override string ToString() => Problem is not null ? $"FAIL: {Problem}"
        : $"{(Pass ? "PASS" : "FAIL")}: {Differing} of {Width * Height} pixels differ ({DifferingFraction:P3}), mean abs error {MeanAbsError:F3}, max channel error {MaxChannelError}";
}

/// <summary>Image diff over raw RGBA8 (or RGB8) buffers, as Godot's <c>Image.GetData</c> returns them.</summary>
public static class ImageDiff
{
    public static DiffResult Compare(byte[] actual, byte[] golden, int width, int height, int channels, DiffTolerance tol)
    {
        if (channels is not (3 or 4))
            return new(false, width, height, 0, 0, 0, 0, $"unsupported channel count {channels}");
        long n = (long)width * height;
        if (width <= 0 || height <= 0 || actual.Length != n * channels || golden.Length != n * channels)
            return new(false, width, height, 0, 0, 0, 0, $"size mismatch: actual {actual.Length} bytes, golden {golden.Length}, expected {n * channels}");
        int differing = 0, maxErr = 0;
        long sum = 0;
        for (long p = 0; p < n; p++)
        {
            long o = p * channels;
            int worst = 0;
            for (int c = 0; c < 3; c++)
            {
                int e = Math.Abs(actual[o + c] - golden[o + c]);
                sum += e;
                if (e > worst) worst = e;
            }
            if (worst > tol.ChannelThreshold) differing++;
            if (worst > maxErr) maxErr = worst;
        }
        double frac = (double)differing / n, mean = sum / (n * 3.0);
        bool pass = frac <= tol.MaxDifferingFraction && mean <= tol.MaxMeanAbsError;
        return new(pass, width, height, differing, frac, mean, maxErr, null);
    }
}
