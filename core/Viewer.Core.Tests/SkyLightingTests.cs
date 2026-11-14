// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Replay;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The sky colours and the ambient light, against the official sky vertex shader's formula (haze weights, sun or moon light plus
/// ambient, haze distance, cloud shadow, the x2 and clamp of the sky fragment shader). The expected values were computed by an
/// independent implementation of that formula (a script, double precision) from the region frames of the v9 capture (the midnight and midday frames are also written out below,
/// so those two run without the capture); the
/// zenith is straight up, the horizon 2 degrees above it, both with the glow evaluated 90 degrees from the light (the engine
/// draws the sun's own hot spot). Tolerance: 1 % relative plus 0.0003 absolute: single-precision arithmetic against double,
/// and the frames' own float rounding, are well inside it; the old colours were off by factors of 2 to 50.
/// </summary>
public class SkyLightingTests
{
    private static readonly string Capture = "capture-region-a-v9.jsonl.gz";

    private static RegionEnvironment Region()
    {
        using var s = ReplayRunner.Load(CaptureFile.Read(LiveCaptures.PathOf(Capture)));
        var e = RegionEnvironment.FromLlsd(s.World.Region!.EnvironmentLlsd!, out var why);
        Assert.True(e is not null, why);
        return e!;
    }

    private static void Near(float[] want, Vector3 got, string what)
    {
        float[] g = [got.X, got.Y, got.Z];
        for (int i = 0; i < 3; i++)
            Assert.True(Math.Abs(g[i] - want[i]) <= 0.01f * Math.Abs(want[i]) + 0.0003f, $"{what}[{i}]: want {want[i]}, got {g[i]}");
    }

    private static readonly (SkyChoice Choice, float[] Zenith, float[] Horizon, float[] Ambient)[] Expected =
    [
        (SkyChoice.Midnight, [0.01598f, 0.01751f, 0.02669f], [0.02103f, 0.02276f, 0.03159f], [0.20405f, 0.24247f, 0.33f]),
        (SkyChoice.Sunrise, [0.285f, 0.43946f, 0.81537f], [0.73692f, 0.84709f, 1.22331f], [0.98143f, 0.88226f, 0.93f]),
        (SkyChoice.Midday, [0.24907f, 0.34595f, 0.60463f], [0.79449f, 0.92325f, 1.301f], [1f, 1f, 1f]),
        (SkyChoice.Sunset, [0.29448f, 0.4081f, 0.67841f], [0.78436f, 0.8765f, 1.13586f], [1f, 0.94239f, 0.94239f]),
    ];

    private static void Check(SkyFrame f, (SkyChoice Choice, float[] Zenith, float[] Horizon, float[] Ambient) e)
    {
        Near(e.Zenith, SkyLighting.Zenith(f), $"{e.Choice} zenith");
        Near(e.Horizon, SkyLighting.Horizon(f), $"{e.Choice} horizon");
        Near(e.Ambient, SkyLighting.Ambient(f), $"{e.Choice} ambient");
    }

    // The region's midnight and midday frames written out (ground-level sky track values of a standard EEP day cycle), so the
    // check runs without the local-only capture.
    private static SkyFrame Midnight => new(0f, -Vector3.UnitZ, new(0.34877f, 0.35574f, 0.66f), Vector3.UnitZ, 0.5f, new(0.20405f, 0.24247f, 0.33f),
        new(0.24f, 0.24f, 0.24f), new(0.45f, 0.45f, 0.45f), 0f, 4f, 0.0003f, 0.8f, 906.2f, 0.27f, new Vector3(5f, 0.001f, -0.48f));
    private static SkyFrame Midday => new(0.5f, Vector3.UnitZ, new(0.73421f, 0.78158f, 0.9f), -Vector3.UnitZ, 0.5f, new(1.05f, 1.05f, 1.05f),
        new(0.49548f, 0.49548f, 0.64f), new(0.24476f, 0.44872f, 0.76f), 0.19f, 0.7f, 0.00018f, 0.8f, 1605f, 0.27f, new Vector3(5f, 0.001f, -0.48f));

    [Fact] public void Midnight_frame_sky_and_ambient_match_the_official_formula() => Check(Midnight, Expected[0]);
    [Fact] public void Midday_frame_sky_and_ambient_match_the_official_formula() => Check(Midday, Expected[2]);

    [Fact]
    public void Ambient_above_one_is_scaled_to_its_largest_channel_and_below_one_is_left_alone()
    {
        Assert.Equal(new Vector3(1f, 0.5f, 0.25f), SkyLighting.Ambient(Midday with { Ambient = new(2f, 1f, 0.5f) }));
        Assert.Equal(new Vector3(0.2f, 0.24f, 0.33f), SkyLighting.Ambient(Midnight with { Ambient = new(0.2f, 0.24f, 0.33f) }));
    }

    [Fact]
    public void A_night_sky_is_dark_and_a_day_sky_is_not_the_raw_tint()
    {
        var night = SkyLighting.Zenith(Midnight);
        Assert.True(Math.Max(night.X, Math.Max(night.Y, night.Z)) < 0.05f, $"night zenith {night}");
        var day = SkyLighting.Zenith(Midday);
        Assert.NotEqual(Midday.BlueDensity.Y, day.Y, 2);   // the frame's tint is weighted by the light and the haze, not used as a colour
    }

    // ---- The light a lit face receives (the official classic path: atmosphericsFuncs.glsl calcAtmosphericVars + softenLightF.glsl's
    // legacy branch, final scale 1.1, tone mapping off for skies without a probe ambiance, so the output is clamped). Transcribed
    // here in double precision, independently of SkyLighting, for a white face turned by an angle to the light.

    private static double S2L(double c) => c < 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    private static double L2S(double c) => c < 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(Math.Max(c, 0), 1 / 2.4) - 0.055;

    /// <summary>The official linear output (before the clamp) on a white face whose normal has dot product <paramref name="da"/> with the light.</summary>
    private static double[] OfficialWhiteFace(SkyFrame f, double da)
    {
        bool sunUp = f.SunDirection.Z >= 0;
        var light = sunUp ? f.SunColor : SkyLighting.MoonLight(f);
        double lighty = Math.Max(sunUp ? f.SunDirection.Z : f.MoonDirection.Z, -0.1);
        float[] sun = [light.X, light.Y, light.Z], amb = [f.Ambient.X, f.Ambient.Y, f.Ambient.Z], bd = [f.BlueDensity.X, f.BlueDensity.Y, f.BlueDensity.Z];
        var o = new double[3];
        for (int i = 0; i < 3; i++)
        {
            double atten = (bd[i] + f.HazeDensity * 0.25) * (f.DensityMultiplier * f.MaxY);
            double sunlit = sun[i] * Math.Exp(-atten / Math.Max(1e-6, lighty)) * 1.35;
            double tmp = amb[i] + (1 - amb[i]) * f.CloudShadow * 0.5;
            double amblit = Math.Pow(Math.Max(tmp, 0), 0.9) * 0.57 * (1 - Math.Pow(Math.Min(Math.Abs(da), 1) * 0.5, 2));
            double d = Math.Pow(Math.Clamp(da, 0, 1), 1.2);
            o[i] = 1.1 * S2L(amblit * 0.9 + L2S(d) * sunlit * 0.7);
        }
        return o;
    }

    /// <summary>What the renderer's lights give a white face at the same angle: ambient plus the sun's colour times the angle's cosine.</summary>
    private static double[] RenderedWhiteFace(SkyFrame f, double da)
    {
        var a = SkyLighting.SurfaceAmbient(f); var s = SkyLighting.SurfaceSun(f);
        return [a.X + s.X * da, a.Y + s.Y * da, a.Z + s.Z * da];
    }

    public static IEnumerable<object[]> LitFrames() { yield return [nameof(Midday)]; yield return [nameof(Midnight)]; }
    private static SkyFrame LitFrame(string n) => n == nameof(Midday) ? Midday : Midnight;

    [Theory, MemberData(nameof(LitFrames))]
    public void A_face_turned_from_the_light_gets_the_official_ambient_only(string frame)
    {
        var f = LitFrame(frame); var want = OfficialWhiteFace(f, 0); var got = RenderedWhiteFace(f, 0);
        for (int i = 0; i < 3; i++)
            Assert.True(Math.Abs(got[i] - want[i]) <= 0.01 * want[i] + 0.0005, $"{frame} ambient face [{i}]: want {want[i]:F4}, got {got[i]:F4}");
    }

    [Theory, MemberData(nameof(LitFrames))]
    public void Lit_faces_stay_within_a_stated_tolerance_of_the_official_brightness_over_every_angle(string frame)
    {
        // NOT clamped: the engine multiplies the light by the surface colour before the display clips, as the official shader does, so a
        // light factor above 1 matters for every surface that is not white (a clamped fit made the sun about 1.5 times too strong for
        // grass and rock: factor 1.7 to 2.0 at the zenith against the official 1.14). 0.18 absolute covers the straight line the
        // renderer's light makes against the official's curved response (encoded sun term, ambient eased by the angle).
        var f = LitFrame(frame);
        for (int k = 0; k <= 20; k++)
        {
            double da = k / 20.0; var want = OfficialWhiteFace(f, da); var got = RenderedWhiteFace(f, da);
            for (int i = 0; i < 3; i++)
                Assert.True(Math.Abs(got[i] - want[i]) <= 0.18, $"{frame} da {da} [{i}]: want {want[i]:F4}, got {got[i]:F4}");
        }
    }

    [Theory, MemberData(nameof(LitFrames))]
    public void No_channel_clips_where_the_official_viewer_would_not(string frame)
    {
        var f = LitFrame(frame);
        for (int k = 0; k <= 20; k++)
        {
            double da = k / 20.0; var want = OfficialWhiteFace(f, da); var got = RenderedWhiteFace(f, da);
            for (int i = 0; i < 3; i++)
                if (want[i] <= 0.85) Assert.True(got[i] < 1.0, $"{frame} da {da} [{i}]: the official gives {want[i]:F4}, ours {got[i]:F4} clips");
        }
    }

    [CaptureFact("capture-region-a-v5.jsonl.gz")]
    public void The_v5_regions_lit_faces_match_the_official_brightness_at_the_goldens_time_of_day()
    {
        using var s = ReplayRunner.Load(CaptureFile.Read(LiveCaptures.PathOf("capture-region-a-v5.jsonl.gz")));
        var env = RegionEnvironment.FromLlsd(s.World.Region!.EnvironmentLlsd!, out var why);
        Assert.True(env is not null, why);
        var f = env!.SkyAt(0.3);
        var amb = RenderedWhiteFace(f, 0);
        for (int k = 0; k <= 20; k++)
        {
            double da = k / 20.0; var want = OfficialWhiteFace(f, da); var got = RenderedWhiteFace(f, da);
            for (int i = 0; i < 3; i++)
            {
                Assert.True(Math.Abs(got[i] - want[i]) <= 0.18, $"v5 da {da} [{i}]: want {want[i]:F4}, got {got[i]:F4}");
                if (want[i] <= 0.85) Assert.True(got[i] < 1.0, $"v5 da {da} [{i}]: the official gives {want[i]:F4}, ours {got[i]:F4} clips");
            }
        }
        Assert.True(amb[0] < 0.5 && amb[1] < 0.5 && amb[2] < 0.5, $"a face turned from the sun is {amb[0]:F3},{amb[1]:F3},{amb[2]:F3}: it must not be near white");
    }

    [CaptureFact("capture-region-a-v9.jsonl.gz")]
    public void The_captured_regions_four_times_of_day_match_the_official_formula()
    {
        var env = Region();
        foreach (var e in Expected) Check(env.SkyFor(e.Choice, 0, out _), e);
    }
    // ---- The sky between the horizon and the zenith. The renderer draws the sky from a profile of the official colour at each
    // elevation (not a two-colour gradient). Transcribed here in double precision, independently of SkyLighting, from the sky vertex
    // shader's formula for the ray to the dome at an elevation, with the glow read 90 degrees from the light.
    private static double[] OfficialSky(SkyFrame f, double elevationDegrees)
    {
        bool sunUp = f.SunDirection.Z >= 0;
        double lightY = sunUp ? f.SunDirection.Z : f.MoonDirection.Z;
        float[] sun = sunUp ? [f.SunColor.X, f.SunColor.Y, f.SunColor.Z] : [f.SunColor.X * 0.7f, f.SunColor.Y * 0.7f, f.SunColor.Z * 0.7f];
        float[] amb = [f.Ambient.X, f.Ambient.Y, f.Ambient.Z], bh = [f.BlueHorizon.X, f.BlueHorizon.Y, f.BlueHorizon.Z], bd = [f.BlueDensity.X, f.BlueDensity.Y, f.BlueDensity.Z];
        double sinE = Math.Sin(elevationDegrees * Math.PI / 180), ray = f.MaxY / sinE;
        double glow = Math.Pow(1.0 * f.Glow.X, f.Glow.Z), gf = sunUp ? 1.0 : (f.MoonDirection.Z >= 0 ? f.MoonBrightness * 0.25 : 0);
        glow = gf < 1 ? 0 : gf * (glow + 0.25);
        var o = new double[3];
        for (int i = 0; i < 3; i++)
        {
            double ch = Math.Max(Math.Abs(bd[i]) + Math.Abs(f.HazeDensity), 1e-6), bw = bd[i] / ch, hw = f.HazeDensity / ch;
            double atten = (bd[i] + f.HazeDensity * 0.25) * (f.DensityMultiplier * f.MaxY);
            double sl = sun[i] * Math.Exp(-atten / Math.Max(1e-6, Math.Max(0, sinE) + lightY));
            double tr = Math.Exp(-ch * ray * f.DensityMultiplier);
            double c = (bh[i] * bw * (sl + amb[i]) + f.HazeHorizon * hw * (sl * glow + amb[i])) * (1 - tr);
            double camb = amb[i] + Math.Max(0, 1 - amb[i]) * f.CloudShadow * 0.5, sl2 = sl * Math.Max(0, 1 - f.CloudShadow);
            double below = bh[i] * bw * (sl2 + camb) + f.HazeHorizon * hw * (sl2 * glow + camb);
            c += (below - c) * (1 - Math.Sqrt(Math.Sqrt(tr)));
            o[i] = Math.Clamp(c * 2, 0, 5);
        }
        return o;
    }

    [Theory]
    [InlineData(nameof(Midday))]
    [InlineData(nameof(Midnight))]
    public void The_sky_profile_follows_the_official_colour_at_every_elevation(string frame)
    {
        var f = LitFrame(frame);
        var profile = SkyLighting.Profile(f);
        Assert.Equal(SkyLighting.ProfileSamples, profile.Length);
        Assert.Equal(90f, SkyLighting.ProfileElevation(profile.Length - 1, profile.Length), 3);
        Assert.InRange(SkyLighting.ProfileElevation(0, profile.Length), 0.1f, 1f);
        for (int k = 0; k < profile.Length; k++)
        {
            float e = SkyLighting.ProfileElevation(k, profile.Length);
            if (k > 0) Assert.True(e > SkyLighting.ProfileElevation(k - 1, profile.Length), "the elevations rise");
            var want = OfficialSky(f, e);
            float[] got = [profile[k].X, profile[k].Y, profile[k].Z];
            for (int i = 0; i < 3; i++)
            {
                double w = Math.Min(1, want[i]);   // shown colours clamp at 1
                Assert.True(Math.Abs(got[i] - w) <= 0.01 * w + 0.0005, $"{frame} elevation {e:F2} [{i}]: want {w:F4}, got {got[i]:F4}");
            }
        }
    }

    [Fact]
    public void The_midday_sky_between_horizon_and_zenith_is_the_official_one_not_a_straight_blend()
    {
        // sampled between profile entries, as the renderer's linear filter does: 10, 20 and 40 degrees up against the official formula
        var f = Midday; var p = SkyLighting.Profile(f);
        float At(double deg, int ch)
        {
            for (int k = 1; k < p.Length; k++)
            {
                float a = SkyLighting.ProfileElevation(k - 1, p.Length), b = SkyLighting.ProfileElevation(k, p.Length);
                if (deg <= b) { float t = (float)((deg - a) / (b - a)); var v = Vector3.Lerp(p[k - 1], p[k], t); return ch == 0 ? v.X : ch == 1 ? v.Y : v.Z; }
            }
            return 0;
        }
        foreach (var deg in new[] { 10.0, 20.0, 40.0 })
        {
            var want = OfficialSky(f, deg);
            for (int ch = 0; ch < 3; ch++) Assert.True(Math.Abs(At(deg, ch) - Math.Min(1, want[ch])) <= 0.02, $"{deg} degrees [{ch}]: want {Math.Min(1, want[ch]):F3}, got {At(deg, ch):F3}");
        }
    }

    // ---- The dome the renderer draws: the sky the official viewer shows. Three independent checks:
    //  (1) numbers worked out by a script (Python, from the official shader text) for the frames below at the elevations 0, 4, 10, 20, 37, 60 and 90
    //      degrees and the angles 0, 90 and 180 degrees from the light: the shown value (no sRGB curve on the shader number, the classic-lighting 1.1,
    //      the +50 m lift, the real angle to the light);
    //  (2) a transcription in this file that builds the dome's real vertex (sphere of radius 15000, the camera 14400 up, the vertex found by bisection on the
    //      cap's angle) and applies the shader to it, over a grid, for four frames;
    //  (3) the profile the renderer samples (bilinear in the profile's own spacing, as the GPU filters it) against the exact value.
    private static double L2Sd(double c) => c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;

    private static SkyFrame DefaultMidday => RegionEnvironment.Default.Sky[0];
    private static SkyFrame DefaultSunset => RegionEnvironment.DefaultCycle.Sky[3];

    private static readonly (float Elev, float Az, float R, float G, float B)[] PinnedDefaultMidday =
    [
        (0f, 0f, 0.84016f, 0.9731f, 1.0f),
        (4f, 0f, 0.87496f, 1.0f, 1.0f),
        (10f, 0f, 0.76929f, 0.94525f, 1.0f),
        (20f, 0f, 0.59148f, 0.75935f, 1.0f),
        (37f, 0f, 0.48528f, 0.61962f, 0.96254f),
        (60f, 0f, 0.6925f, 0.80141f, 1.0f),
        (90f, 0f, 0.29509f, 0.39925f, 0.66556f),
        (0f, 90f, 0.81104f, 0.94943f, 1.0f),
        (4f, 90f, 0.8362f, 0.98547f, 1.0f),
        (10f, 90f, 0.7249f, 0.90664f, 1.0f),
        (20f, 90f, 0.5396f, 0.71119f, 1.0f),
        (37f, 90f, 0.38582f, 0.52328f, 0.86617f),
        (60f, 90f, 0.31236f, 0.42542f, 0.7124f),
        (0f, 180f, 0.7991f, 0.93973f, 1.0f),
        (4f, 180f, 0.82138f, 0.97348f, 1.0f),
        (10f, 180f, 0.70982f, 0.89352f, 1.0f),
        (20f, 180f, 0.5258f, 0.6984f, 1.0f),
        (37f, 180f, 0.37231f, 0.5102f, 0.85309f),
        (60f, 180f, 0.29742f, 0.41064f, 0.69717f),
    ];
    private static readonly (float Elev, float Az, float R, float G, float B)[] PinnedDefaultSunset =
    [
        (0f, 0f, 0.72979f, 0.42902f, 0.3904f),
        (4f, 0f, 1.0f, 0.81651f, 0.5397f),
        (10f, 0f, 1.0f, 1.0f, 0.69478f),
        (20f, 0f, 0.53985f, 0.32313f, 0.31941f),
        (37f, 0f, 0.26543f, 0.16983f, 0.20739f),
        (60f, 0f, 0.17629f, 0.11677f, 0.15699f),
        (90f, 0f, 0.14631f, 0.09878f, 0.13938f),
        (0f, 90f, 0.3556f, 0.25369f, 0.33235f),
        (4f, 90f, 0.3888f, 0.26198f, 0.33496f),
        (10f, 90f, 0.35734f, 0.23815f, 0.31205f),
        (20f, 90f, 0.27838f, 0.18628f, 0.25329f),
        (37f, 90f, 0.20264f, 0.13627f, 0.18989f),
        (60f, 90f, 0.16057f, 0.10829f, 0.15241f),
        (0f, 180f, 0.34285f, 0.24772f, 0.33037f),
        (4f, 180f, 0.37055f, 0.25315f, 0.3317f),
        (10f, 180f, 0.33824f, 0.22848f, 0.30791f),
        (20f, 180f, 0.26263f, 0.17804f, 0.24931f),
        (37f, 180f, 0.19162f, 0.13038f, 0.18682f),
        (60f, 180f, 0.15401f, 0.10475f, 0.1505f),
    ];
    private static readonly (float Elev, float Az, float R, float G, float B)[] PinnedCaptureMidday =
    [
        (0f, 0f, 0.81712f, 0.95276f, 1.0f),
        (4f, 0f, 0.84314f, 0.98895f, 1.0f),
        (10f, 0f, 0.73242f, 0.90932f, 1.0f),
        (20f, 0f, 0.54696f, 0.71347f, 1.0f),
        (37f, 0f, 0.39544f, 0.52842f, 0.87844f),
        (60f, 0f, 0.33946f, 0.44857f, 0.74137f),
        (90f, 0f, 0.93453f, 1.0f, 1.0f),
        (0f, 90f, 0.81712f, 0.95276f, 1.0f),
        (4f, 90f, 0.84314f, 0.98895f, 1.0f),
        (10f, 90f, 0.73242f, 0.90932f, 1.0f),
        (20f, 90f, 0.54696f, 0.71347f, 1.0f),
        (37f, 90f, 0.39544f, 0.52842f, 0.87844f),
        (60f, 90f, 0.33946f, 0.44857f, 0.74137f),
        (0f, 180f, 0.81712f, 0.95276f, 1.0f),
        (4f, 180f, 0.84314f, 0.98895f, 1.0f),
        (10f, 180f, 0.73242f, 0.90932f, 1.0f),
        (20f, 180f, 0.54696f, 0.71347f, 1.0f),
        (37f, 180f, 0.39544f, 0.52842f, 0.87844f),
        (60f, 180f, 0.33946f, 0.44857f, 0.74137f),
    ];

    private static void PinnedCheck(SkyFrame f, (float Elev, float Az, float R, float G, float B)[] rows, string what)
    {
        foreach (var r in rows)
        {
            var got = SkyLighting.DomeShown(f, r.Elev, r.Az);
            float[] g = [got.X, got.Y, got.Z], w = [r.R, r.G, r.B];
            for (int i = 0; i < 3; i++)
                Assert.True(Math.Abs(g[i] - w[i]) <= 0.002f, $"{what} elevation {r.Elev} angle from light {r.Az} [{i}]: official {w[i]:F4}, got {g[i]:F4}");
        }
    }

    [Fact] public void The_default_midday_sky_is_the_official_shown_value_at_every_pinned_elevation_and_angle() => PinnedCheck(DefaultMidday, PinnedDefaultMidday, "default midday");
    [Fact] public void The_default_sunset_sky_is_the_official_shown_value_at_every_pinned_elevation_and_angle() => PinnedCheck(DefaultSunset, PinnedDefaultSunset, "default sunset");
    [Fact] public void The_captured_regions_midday_sky_is_the_official_shown_value_at_every_pinned_elevation_and_angle() => PinnedCheck(Midday, PinnedCaptureMidday, "captured midday");

    /// <summary>The shown value by an independent route: the dome's real vertex (found on the cap by bisection), the shader's lines one by one, the display steps.</summary>
    private static double[] OfficialDomeShown(SkyFrame f, double elevDeg, double azDeg)
    {
        const double R = 15000, camH = 0.96 * 15000;
        double target = elevDeg * Math.PI / 180, lo = 0, hi = Math.PI / 2;   // phi from the pole: the elevation of the vertex seen from the camera falls as phi grows
        for (int it = 0; it < 200; it++)
        {
            double phi = (lo + hi) / 2, el = Math.Atan2(R * Math.Cos(phi) - camH, R * Math.Sin(phi));
            if (el > target) lo = phi; else hi = phi;
        }
        double ph = (lo + hi) / 2, th = azDeg * Math.PI / 180;
        double[] pos = [R * Math.Sin(ph) * Math.Cos(th), R * Math.Cos(ph), R * Math.Sin(ph) * Math.Sin(th)];
        double[] rel = [pos[0], pos[1] - camH + 50, pos[2]];
        if (rel[1] > 0) { double k = f.MaxY / rel[1]; for (int i = 0; i < 3; i++) rel[i] *= k; }
        if (rel[1] < 0) { double k = -32000 / rel[1]; for (int i = 0; i < 3; i++) rel[i] *= k; }
        double len = Math.Sqrt(rel[0] * rel[0] + rel[1] * rel[1] + rel[2] * rel[2]);
        double[] nrm = [rel[0] / len, rel[1] / len, rel[2] / len];
        bool sunUp = f.SunDirection.Z >= 0, moonUp = f.MoonDirection.Z >= 0;
        var ld = sunUp ? f.SunDirection : moonUp ? f.MoonDirection : new Vector3(0, 0, -1);
        double lz = Math.Max(ld.Z, -0.1), lx = Math.Sqrt((double)ld.X * ld.X + (double)ld.Y * ld.Y);
        double dot = nrm[0] * lx + nrm[1] * lz;
        var light = sunUp ? f.SunColor : f.SunColor * 0.7f;
        double[] sun0 = [light.X, light.Y, light.Z], amb = [f.Ambient.X, f.Ambient.Y, f.Ambient.Z], bh = [f.BlueHorizon.X, f.BlueHorizon.Y, f.BlueHorizon.Z], bd = [f.BlueDensity.X, f.BlueDensity.Y, f.BlueDensity.Z];
        double glow = Math.Pow(Math.Max(1 - dot, 0.001) * f.Glow.X, f.Glow.Z);
        double gf = sunUp ? 1 : moonUp ? f.MoonBrightness * 0.25 : 0;
        glow = gf < 1 ? 0 : gf * (glow + 0.25);
        var o = new double[3];
        for (int i = 0; i < 3; i++)
        {
            double ch = Math.Max(Math.Abs(bd[i]) + Math.Abs(f.HazeDensity), 1e-6), bw = bd[i] / ch, hw = f.HazeDensity / ch;
            double atten = (bd[i] + f.HazeDensity * 0.25) * (f.DensityMultiplier * f.MaxY);
            double sl = sun0[i] * Math.Exp(-atten / Math.Max(1e-6, Math.Max(0, nrm[1]) + lz));
            double tr = Math.Exp(-ch * (len * f.DensityMultiplier));
            double c = (bh[i] * bw * (sl + amb[i]) + f.HazeHorizon * hw * (sl * glow + amb[i])) * (1 - tr);
            double camb = amb[i] + Math.Max(0, 1 - amb[i]) * f.CloudShadow * 0.5, sl2 = sl * Math.Max(0, 1 - f.CloudShadow);
            double below = bh[i] * bw * (sl2 + camb) + f.HazeHorizon * hw * (sl2 * glow + camb);
            c += (below - c) * (1 - Math.Sqrt(Math.Sqrt(tr)));
            double v = Math.Clamp(Math.Clamp(c * 2, 0, 5), 0, 1);          // fragment times two, 8-bit buffer
            o[i] = L2Sd(Math.Clamp(S2L(v) * 1.1, 0, 1));                  // soften light (linear, final scale 1.1), gamma pass
        }
        return o;
    }

    public static IEnumerable<object[]> DomeFrames() { yield return [nameof(Midday)]; yield return [nameof(Midnight)]; yield return [nameof(DefaultMidday)]; yield return [nameof(DefaultSunset)]; }
    private static SkyFrame DomeFrame(string n) => n switch { nameof(Midday) => Midday, nameof(Midnight) => Midnight, nameof(DefaultMidday) => DefaultMidday, _ => DefaultSunset };

    [Theory, MemberData(nameof(DomeFrames))]
    public void The_dome_equals_the_official_shaders_on_the_real_dome_over_a_grid(string frame)
    {
        var f = DomeFrame(frame);
        foreach (double e in new[] { -5.0, -2.0, 0.0, 1.0, 4.0, 10.0, 20.0, 37.0, 60.0, 80.0, 90.0 })
            foreach (double az in new[] { 0.0, 3.0, 20.0, 90.0, 150.0, 180.0 })
            {
                var want = OfficialDomeShown(f, e, az); var got = SkyLighting.DomeShown(f, (float)e, (float)az);
                float[] g = [got.X, got.Y, got.Z];
                for (int i = 0; i < 3; i++) Assert.True(Math.Abs(g[i] - want[i]) <= 0.002, $"{frame} elevation {e} angle {az} [{i}]: want {want[i]:F4}, got {g[i]:F4}");
            }
    }

    [Fact]
    public void The_dome_shows_the_shader_number_as_it_is_with_only_the_one_point_one_in_linear_light()
    {
        // a mid-range value: the shown value is the shader number (times two, clamped) with 1.1 in linear light, not that number put through the sRGB curve
        var f = DefaultMidday;
        var v = SkyLighting.DomeVertexColour(f, 37f, 90f) * 2f;
        var shown = SkyLighting.DomeShown(f, 37f, 90f);
        Assert.InRange(v.X, 0.2f, 0.9f);
        Assert.True(Math.Abs(shown.X - L2Sd(Math.Min(1.0, S2L(v.X) * 1.1))) <= 0.001, $"shader number {v.X:F4}, shown {shown.X:F4}");
        Assert.True(Math.Abs(shown.X - L2Sd(v.X)) > 0.1, "the number must not be put through the sRGB curve");
    }

    [Fact]
    public void The_sunset_sky_is_brighter_towards_the_sun_than_away_from_it()
    {
        var f = DefaultSunset;
        var towards = SkyLighting.DomeShown(f, 10f, 0f); var away = SkyLighting.DomeShown(f, 10f, 180f);
        Assert.True(towards.X > away.X + 0.3f && towards.Y > away.Y + 0.3f, $"towards {towards}, away {away}");
    }

    [Theory, MemberData(nameof(DomeFrames))]
    public void The_profile_the_renderer_samples_stays_within_a_stated_distance_of_the_exact_dome(string frame)
    {
        var f = DomeFrame(frame);
        int n = SkyLighting.DomeElevations, m = SkyLighting.DomeAzimuths;
        var p = SkyLighting.DomeProfile(f);
        Assert.Equal(n * m, p.Length);
        Assert.Equal(90f, SkyLighting.DomeElevation(n - 1, n), 3);
        Assert.Equal(SkyLighting.DomeFloorDegrees, SkyLighting.DomeElevation(0, n), 3);
        Assert.Equal(180f, SkyLighting.DomeAzimuth(m - 1, m), 3);
        Vector3 Sample(double e, double az)
        {
            double ue = Math.Sqrt(Math.Max(e - SkyLighting.DomeFloorDegrees, 0) / (90 - SkyLighting.DomeFloorDegrees)) * (n - 1);
            double ua = Math.Sqrt(Math.Clamp(az / 180, 0, 1)) * (m - 1);
            int k0 = Math.Min((int)ue, n - 2), j0 = Math.Min((int)ua, m - 2);
            float tk = (float)(ue - k0), tj = (float)(ua - j0);
            var a = Vector3.Lerp(p[j0 * n + k0], p[j0 * n + k0 + 1], tk); var b = Vector3.Lerp(p[(j0 + 1) * n + k0], p[(j0 + 1) * n + k0 + 1], tk);
            return Vector3.Lerp(a, b, tj);
        }
        foreach (double e in new[] { -4.0, 0.3, 1.7, 3.0, 5.5, 8.0, 12.5, 17.0, 24.0, 31.0, 45.0, 66.0, 85.0 })
            foreach (double az in new[] { 4.0, 7.5, 13.0, 25.0, 40.0, 66.0, 91.0, 123.0, 155.0, 178.0 })
            {
                // within 8 degrees of the light the glow is sharp and the profile is coarser (up to about 0.02 at 5 degrees); the light's disc is drawn there
                var ld = f.SunDirection.Z >= 0 ? f.SunDirection : f.MoonDirection; double le = Math.Asin(Math.Clamp(ld.Z, -1f, 1f)), er = e * Math.PI / 180;
                double toLight = Math.Acos(Math.Clamp(Math.Sin(er) * Math.Sin(le) + Math.Cos(er) * Math.Cos(le) * Math.Cos(az * Math.PI / 180), -1, 1)) * 180 / Math.PI;
                if (toLight < 8) continue;
                var got = Sample(e, az); var want = SkyLighting.DomeShown(f, (float)e, (float)az);
                double d = Math.Max(Math.Abs(L2Sd(got.X) - want.X), Math.Max(Math.Abs(L2Sd(got.Y) - want.Y), Math.Abs(L2Sd(got.Z) - want.Z)));
                Assert.True(d <= 0.004, $"{frame} elevation {e} angle {az}: profile {L2Sd(got.X):F4},{L2Sd(got.Y):F4},{L2Sd(got.Z):F4} exact {want.X:F4},{want.Y:F4},{want.Z:F4}");
            }
    }
}
