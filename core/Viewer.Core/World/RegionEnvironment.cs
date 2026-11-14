// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Xml;

namespace Viewer.Core.World;

/// <summary>One sky keyframe of the region's day cycle, reduced to what the first-look scene uses (spec 2).</summary>
public sealed record SkyFrame(float Key, Vector3 SunDirection, Vector3 SunColor, Vector3 MoonDirection, float MoonBrightness,
    Vector3 Ambient, Vector3 BlueHorizon, Vector3 BlueDensity, float HazeHorizon, float HazeDensity, float DensityMultiplier, float DistanceMultiplier,
    float MaxY = SkyFrame.DefaultMaxY, float CloudShadow = SkyFrame.DefaultCloudShadow, Vector3? GlowParameters = null, CloudSettings? Clouds = null, float ProbeAmbiance = 0f, Guid? SunTextureId = null, float SunScale = 1f)
{
    /// <summary>The frame's cloud layer settings, or the standard EEP defaults when it carries none.</summary>
    public CloudSettings Cloud => Clouds ?? CloudSettings.Default;

    /// <summary>The altitude (m) the sky dome is clamped to, when a frame carries none (the standard EEP default).</summary>
    public const float DefaultMaxY = 1605f;
    /// <summary>The cloud shadow fraction when a frame carries none (the standard EEP default).</summary>
    public const float DefaultCloudShadow = 0.2699f;
    /// <summary>The sun glow shape (width, minimum, falloff exponent) when a frame carries none (the standard EEP default).</summary>
    public static readonly Vector3 DefaultGlow = new(5f, 0.001f, -0.4799f);

    /// <summary>The frame's glow shape, or the standard default.</summary>
    public Vector3 Glow => GlowParameters ?? DefaultGlow;
}

/// <summary>
/// One sky keyframe's cloud layer: the colour, the pattern's scale, the two density/offset triples (x, y: offset of the large and small
/// texture; z: density weight), the scroll rate, the variance and the noise texture's id. Defaults are the standard EEP sky's.
/// </summary>
public sealed record CloudSettings(Vector3 Color, float Scale, Vector3 PosDensity1, Vector3 PosDensity2, Vector2 ScrollRate, float Variance, Guid TextureId)
{
    /// <summary>The standard default noise texture's id.</summary>
    public static readonly Guid DefaultTextureId = new("1dc1368f-e8fe-f02d-a08d-9d9f11c1af6b");

    /// <summary>The standard EEP defaults (colour 0.4099 grey, scale 0.4199, offsets (1, 0.526) and density 1, scroll (0.2, 0.01), no variance).</summary>
    public static CloudSettings Default { get; } = new(new(0.4099f), 0.4199f, new(1f, 0.5260f, 1f), new(1f, 0.5260f, 1f), new(0.2f, 0.01f), 0f, DefaultTextureId);
}

/// <summary>The region's water look (spec 3).</summary>
/// <summary>
/// The region's water: fog colour and density, the two wave directions, the fresnel scale and offset, and the official water
/// settings the shader reads (blur multiplier, normal scale, the refraction scale above and below the surface), each with the official
/// default when the region sends none.
/// </summary>
public sealed record WaterLook(Vector3 FogColor, float FogDensity, Vector2 Wave1, Vector2 Wave2, float FresnelScale, float FresnelOffset,
    float BlurMultiplier = 0.04f, Vector3? NormalScaleValue = null, float ScaleAbove = 0.0299f, float ScaleBelow = 0.2f, Guid? NormalMapId = null)
{
    /// <summary>How strongly the wave normals are tilted (the official default is 2 on every axis).</summary>
    public Vector3 NormalScale => NormalScaleValue ?? new Vector3(2f, 2f, 2f);
    /// <summary>The normal map the ripples are read from.</summary>
    public Guid NormalMap => NormalMapId is { } g && g != Guid.Empty ? g : DefaultNormalMap;

    /// <summary>The texture the official viewer asks for when the water settings name none (its DEFAULT_WATER_NORMAL, indra_constants.cpp:73).</summary>
    public static readonly Guid DefaultNormalMap = new("822ded49-9a6c-f61c-cb89-6df54f42cdf4");
}

/// <summary>
/// The region's environment (EEP), from the ExtEnvironment capability's LLSD, per Docs/ENVIRONMENT-SPEC.md.
/// Ground-level sky track and the water track only; higher-altitude tracks, clouds,
/// textures and the physical sky model are not used (first look).
/// </summary>
public sealed record RegionEnvironment(ImmutableArray<SkyFrame> Sky, WaterLook Water, int DayLength, int DayOffset, string Source)
{
    public bool Equals(RegionEnvironment? o) => o is not null && Sky.SequenceEqual(o.Sky) && Water == o.Water && DayLength == o.DayLength && DayOffset == o.DayOffset && Source == o.Source;
    public override int GetHashCode() => HashCode.Combine(Sky.Length, Water, DayLength, DayOffset);

    /// <summary>The day fraction (0..1) at a moment: (seconds since the Unix epoch + offset) modulo the day length (spec 1).</summary>
    public double DayFraction(double unixSeconds) => DayLength <= 0 ? 0 : ((unixSeconds + DayOffset) % DayLength + DayLength) % DayLength / DayLength;

    /// <summary>The sky at a day fraction: linear between the two keyframes around it, wrapping at 1 (spec 1).</summary>
    public SkyFrame SkyAt(double fraction)
    {
        if (Sky.Length == 1) return Sky[0];
        float f = (float)(((fraction % 1) + 1) % 1);
        int next = 0;
        while (next < Sky.Length && Sky[next].Key <= f) next++;
        var a = Sky[(next - 1 + Sky.Length) % Sky.Length];
        var b = Sky[next % Sky.Length];
        float span = b.Key - a.Key; if (span <= 0) span += 1;
        float into = f - a.Key; if (into < 0) into += 1;
        float t = span <= 0 ? 0 : Math.Clamp(into / span, 0, 1);
        static Vector3 L(Vector3 x, Vector3 y, float t) => Vector3.Lerp(x, y, t);
        static Vector3 D(Vector3 x, Vector3 y, float t) { var v = Vector3.Lerp(x, y, t); return v.LengthSquared() < 1e-8f ? y : Vector3.Normalize(v); }
        static float F(float x, float y, float t) => x + (y - x) * t;
        return new(f, D(a.SunDirection, b.SunDirection, t), L(a.SunColor, b.SunColor, t), D(a.MoonDirection, b.MoonDirection, t), F(a.MoonBrightness, b.MoonBrightness, t),
            L(a.Ambient, b.Ambient, t), L(a.BlueHorizon, b.BlueHorizon, t), L(a.BlueDensity, b.BlueDensity, t), F(a.HazeHorizon, b.HazeHorizon, t),
            F(a.HazeDensity, b.HazeDensity, t), F(a.DensityMultiplier, b.DensityMultiplier, t), F(a.DistanceMultiplier, b.DistanceMultiplier, t),
            F(a.MaxY, b.MaxY, t), F(a.CloudShadow, b.CloudShadow, t), L(a.Glow, b.Glow, t), CloudsAt(a.Cloud, b.Cloud, t), F(a.ProbeAmbiance, b.ProbeAmbiance, t), a.SunTextureId, F(a.SunScale, b.SunScale, t));
    }

    // the cloud settings between two keyframes: every number is interpolated; the noise picture is the first frame's (the official viewer blends two pictures, which this does not)
    private static CloudSettings CloudsAt(CloudSettings a, CloudSettings b, float t) =>
        new(Vector3.Lerp(a.Color, b.Color, t), a.Scale + (b.Scale - a.Scale) * t, Vector3.Lerp(a.PosDensity1, b.PosDensity1, t), Vector3.Lerp(a.PosDensity2, b.PosDensity2, t),
            Vector2.Lerp(a.ScrollRate, b.ScrollRate, t), a.Variance + (b.Variance - a.Variance) * t, a.TextureId);

    /// <summary>Spec 4: the environment when a region serves none: one midday-like sky and a blue-green water.</summary>
    public static RegionEnvironment Default { get; } = new(
        [new SkyFrame(0, Vector3.Normalize(new(0.5f, 0.2f, 0.84f)), new(0.73f, 0.78f, 0.9f), -Vector3.UnitZ, 0.5f, new(1.05f, 1.05f, 1.05f),
            new(0.5f, 0.5f, 0.64f), new(0.24f, 0.45f, 0.76f), 0.19f, 0.7f, 0.00018f, 0.8f)],
        new WaterLook(new(0.0157f, 0.149f, 0.251f), 16f, new(1.05f, -0.42f), new(1.11f, -1.16f), 0.4f, 0.5f), 14400, 57600, "default");

    /// <summary>The sky for a time-of-day choice at a moment: <see cref="SkyChoice.Region"/> follows the region's clock;
    /// a fixed choice evaluates this environment's day cycle at that point of the day. A region that served no day cycle
    /// (Source "default") uses <see cref="DefaultCycle"/> for a fixed choice, so Midnight is dark there too.</summary>
    public SkyFrame SkyFor(SkyChoice choice, double unixSeconds, out double fraction)
    {
        fraction = SkyChoices.Fraction(choice) ?? DayFraction(unixSeconds);
        var cycle = choice != SkyChoice.Region && Source == "default" ? DefaultCycle : this;
        return cycle.SkyAt(fraction);
    }

    /// <summary>The day cycle a fixed time-of-day choice uses when the region served none: four keyframes (midnight, sunrise,
    /// midday, sunset). Its midday frame is <see cref="Default"/>'s sky; <see cref="Default"/> itself is unchanged.</summary>
    public static RegionEnvironment DefaultCycle { get; } = Default with
    {
        Sky =
        [
            Night(0f), Dawn(0.25f), Default.Sky[0] with { Key = 0.5f }, Dusk(0.75f),
        ],
    };

    private static SkyFrame Night(float key) => new(key, Vector3.Normalize(new(-0.2f, -0.1f, -0.97f)), new(0.1f, 0.1f, 0.15f), Vector3.Normalize(new(0.2f, 0.1f, 0.97f)), 0.5f,
        new(0.08f, 0.1f, 0.18f), new(0.04f, 0.06f, 0.14f), new(0.02f, 0.03f, 0.08f), 0.05f, 0.7f, 0.00018f, 0.8f);
    private static SkyFrame Dawn(float key) => new(key, Vector3.Normalize(new(0.98f, 0.1f, 0.15f)), new(0.9f, 0.6f, 0.4f), -Vector3.UnitZ, 0.5f,
        new(0.35f, 0.3f, 0.4f), new(0.35f, 0.4f, 0.6f), new(0.2f, 0.3f, 0.55f), 0.19f, 0.7f, 0.00018f, 0.8f);
    private static SkyFrame Dusk(float key) => new(key, Vector3.Normalize(new(-0.98f, 0.1f, 0.15f)), new(0.9f, 0.5f, 0.3f), -Vector3.UnitZ, 0.5f,
        new(0.35f, 0.28f, 0.35f), new(0.4f, 0.35f, 0.5f), new(0.22f, 0.28f, 0.5f), 0.19f, 0.7f, 0.00018f, 0.8f);

    /// <summary>The rotation of the +X axis by a quaternion given as [x, y, z, w] (spec 2).</summary>
    public static Vector3 RotateX(Quaternion q) => Vector3.Transform(Vector3.UnitX, q);

    /// <summary>Parses the ExtEnvironment answer. Null (with <paramref name="why"/>) when it is not a usable EEP environment.</summary>
    public static RegionEnvironment? FromLlsd(string llsd, out string? why)
    {
        why = null;
        object? root;
        try { root = Llsd.Parse(llsd); }
        catch (Exception e) when (e is XmlException or FormatException or InvalidDataException or OverflowException) { why = "not LLSD: " + e.GetType().Name; return null; }
        if (root is not Dictionary<string, object?> top || top.GetValueOrDefault("environment") is not Dictionary<string, object?> env
            || env.GetValueOrDefault("day_cycle") is not Dictionary<string, object?> dc
            || dc.GetValueOrDefault("frames") is not Dictionary<string, object?> frames || dc.GetValueOrDefault("tracks") is not List<object?> tracks || tracks.Count < 2)
        { why = "no EEP day cycle"; return null; }
        var sky = new List<SkyFrame>();
        foreach (var k in (tracks[1] as List<object?>) ?? [])
            if (k is Dictionary<string, object?> kf && frames.GetValueOrDefault(Str(kf, "key_name")) is Dictionary<string, object?> f && Str(f, "type") == "sky")
                sky.Add(SkyOf(Num(kf, "key_keyframe"), f));
        WaterLook? water = null;
        foreach (var k in (tracks[0] as List<object?>) ?? [])
            if (k is Dictionary<string, object?> kf && frames.GetValueOrDefault(Str(kf, "key_name")) is Dictionary<string, object?> f && Str(f, "type") == "water")
            { water = WaterOf(f); break; }
        if (sky.Count == 0) { why = "no ground-level sky frames"; return null; }
        sky.Sort((a, b) => a.Key.CompareTo(b.Key));
        return new([.. sky], water ?? Default.Water, (int)Num(env, "day_length", 14400), (int)Num(env, "day_offset", 57600), "region");
    }

    /// <summary>The sky frame (key 0) a sky settings asset's map holds; the keys are those of a day cycle's sky frame.</summary>
    internal static SkyFrame SkyFromSettings(Dictionary<string, object?> sky) => SkyOf(0, sky);

    private static SkyFrame SkyOf(float key, Dictionary<string, object?> f)
    {
        var lh = f.GetValueOrDefault("legacy_haze") as Dictionary<string, object?> ?? [];
        return new(key, RotateX(Quat(f, "sun_rotation")), Vec(f, "sunlight_color"), RotateX(Quat(f, "moon_rotation")), Num(f, "moon_brightness", 0.5f),
            Vec(lh, "ambient"), Vec(lh, "blue_horizon"), Vec(lh, "blue_density"), Num(lh, "haze_horizon", 0.19f), Num(lh, "haze_density", 0.7f),
            Num(lh, "density_multiplier", 0.00018f), Num(lh, "distance_multiplier", 0.8f),
            Num(f, "max_y", SkyFrame.DefaultMaxY), Num(f, "cloud_shadow", SkyFrame.DefaultCloudShadow),
            f.ContainsKey("glow") ? Vec(f, "glow") : null, CloudsOf(f), Num(f, "reflection_probe_ambiance", 0f),
            f.GetValueOrDefault("sun_id") is Guid sun && sun != Guid.Empty ? sun : null, Num(f, "sun_scale", 1f));
    }

    private static CloudSettings? CloudsOf(Dictionary<string, object?> f)
    {
        var d = CloudSettings.Default;
        if (!(f.ContainsKey("cloud_color") || f.ContainsKey("cloud_scale") || f.ContainsKey("cloud_pos_density1") || f.ContainsKey("cloud_pos_density2")
              || f.ContainsKey("cloud_scroll_rate") || f.ContainsKey("cloud_variance") || f.ContainsKey("cloud_id"))) return null;
        return new(f.ContainsKey("cloud_color") ? Vec(f, "cloud_color") : d.Color, Num(f, "cloud_scale", d.Scale),
            f.ContainsKey("cloud_pos_density1") ? Vec(f, "cloud_pos_density1") : d.PosDensity1, f.ContainsKey("cloud_pos_density2") ? Vec(f, "cloud_pos_density2") : d.PosDensity2,
            f.ContainsKey("cloud_scroll_rate") ? Vec2(f, "cloud_scroll_rate") : d.ScrollRate, Num(f, "cloud_variance", d.Variance),
            f.GetValueOrDefault("cloud_id") is Guid g && g != Guid.Empty ? g : d.TextureId);
    }

    private static WaterLook WaterOf(Dictionary<string, object?> f) =>
        new(Vec(f, "water_fog_color"), Num(f, "water_fog_density", 16f), Vec2(f, "wave1_direction"), Vec2(f, "wave2_direction"), Num(f, "fresnel_scale", 0.4f), Num(f, "fresnel_offset", 0.5f),
        Num(f, "blur_multiplier", 0.04f), f.ContainsKey("normal_scale") ? Vec(f, "normal_scale") : null, Num(f, "scale_above", 0.0299f), Num(f, "scale_below", 0.2f),
        f.GetValueOrDefault("normal_map") is Guid nm && nm != Guid.Empty ? nm : null);

    private static string Str(Dictionary<string, object?> m, string k) => m.GetValueOrDefault(k) as string ?? "";
    // a finite double can still overflow a float (1e300 -> Infinity); such a value takes the field's default,
    // so nothing non-finite ever reaches the scene (the reader already refuses NaN and Infinity themselves).
    private static float Fin(float v, float d) => float.IsFinite(v) ? v : d;
    private static float Num(Dictionary<string, object?> m, string k, float d = 0) => m.GetValueOrDefault(k) switch { double v => Fin((float)v, d), long v => Fin(v, d), _ => d };
    private static List<object?> Arr(Dictionary<string, object?> m, string k) => m.GetValueOrDefault(k) as List<object?> ?? [];
    private static float At(List<object?> a, int i) => i < a.Count ? a[i] switch { double v => Fin((float)v, 0), long v => Fin(v, 0), _ => 0 } : 0;
    private static Vector3 Vec(Dictionary<string, object?> m, string k) { var a = Arr(m, k); return new(At(a, 0), At(a, 1), At(a, 2)); }
    private static Vector2 Vec2(Dictionary<string, object?> m, string k) { var a = Arr(m, k); return new(At(a, 0), At(a, 1)); }
    private static Quaternion Quat(Dictionary<string, object?> m, string k)
    {
        var a = Arr(m, k);
        var q = new Quaternion(At(a, 0), At(a, 1), At(a, 2), a.Count > 3 ? At(a, 3) : 1);
        return q.LengthSquared() < 1e-8f ? Quaternion.Identity : Quaternion.Normalize(q);
    }
}

/// <summary>
/// A minimal LLSD XML reader (map, array, real, integer, string, uuid, boolean, date, uri, binary, undef) into
/// plain .NET values: Dictionary&lt;string, object?&gt;, List&lt;object?&gt;, double, long, string, bool, Guid, byte[].
/// Every limit is enforced here and proved by its own test (EnvironmentTests.Llsd_limit_*):
/// DTD processing prohibited; no XmlResolver (nothing external is ever fetched); at most <see cref="MaxBytes"/> UTF-8 bytes
/// (counted before parsing); at most <see cref="MaxDepth"/> levels; at most <see cref="MaxElements"/> values (an
/// environment is ~2 000); numbers in the invariant culture; non-finite reals (NaN, Infinity, overflow) and overflowing
/// integers are refused. Every refusal is an <see cref="InvalidDataException"/> or <see cref="XmlException"/>.
/// </summary>
public static class Llsd
{
    public const int MaxBytes = 512 * 1024;
    public const int MaxDepth = 64;
    public const int MaxElements = 50_000;

    public static object? Parse(string xml) => Parse(xml, ReaderSettings());

    /// <summary>The reader's settings. <paramref name="dtd"/> other than Prohibit is for the XmlResolver test only.</summary>
    internal static XmlReaderSettings ReaderSettings(DtdProcessing dtd = DtdProcessing.Prohibit) => new()
    {
        DtdProcessing = dtd, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true, MaxCharactersInDocument = MaxBytes,
        MaxCharactersFromEntities = 1024,
    };

    internal static object? Parse(string xml, XmlReaderSettings settings)
    {
        // bytes, not characters: the cap is the same 512 KiB the fetch enforces on the wire
        if (System.Text.Encoding.UTF8.GetByteCount(xml) > MaxBytes)
            throw new InvalidDataException($"llsd larger than {MaxBytes} bytes");
        using var r = XmlReader.Create(new StringReader(xml), settings);
        r.MoveToContent();
        if (r.Name != "llsd") throw new InvalidDataException("not an llsd document");
        if (r.IsEmptyElement) return null;
        r.Read();
        int budget = MaxElements;
        var v = Value(r, 0, ref budget);
        return v;
    }

    private static double Real(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) throw new InvalidDataException("llsd real not a number");
        if (!double.IsFinite(d)) throw new InvalidDataException("llsd real not finite");
        return d;
    }

    private static long Integer(string text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : throw new InvalidDataException("llsd integer not a 64-bit integer");

    private static object? Value(XmlReader r, int depth, ref int budget)
    {
        if (depth > MaxDepth || --budget < 0) throw new InvalidDataException("llsd too deep or too large");
        while (r.NodeType != XmlNodeType.Element) { if (!r.Read()) return null; }
        string name = r.Name;
        if (r.IsEmptyElement)
        {
            r.Read();
            return name switch { "map" => new Dictionary<string, object?>(), "array" => new List<object?>(), "string" => "", "real" => 0.0, "integer" => 0L, "boolean" => false, "uuid" => Guid.Empty, _ => null };
        }
        switch (name)
        {
            case "map":
            {
                var m = new Dictionary<string, object?>();
                r.Read();
                while (r.NodeType == XmlNodeType.Element && r.Name == "key")
                {
                    string key = r.ReadElementContentAsString();
                    m[key] = Value(r, depth + 1, ref budget);
                }
                if (r.NodeType == XmlNodeType.EndElement) r.Read();
                return m;
            }
            case "array":
            {
                var a = new List<object?>();
                r.Read();
                while (r.NodeType == XmlNodeType.Element) a.Add(Value(r, depth + 1, ref budget));
                if (r.NodeType == XmlNodeType.EndElement) r.Read();
                return a;
            }
            default:
            {
                string text = r.ReadElementContentAsString();
                return name switch
                {
                    "real" => Real(text),
                    "integer" => Integer(text),
                    "boolean" => text is "1" or "true",
                    "uuid" => Guid.TryParse(text, out var g) ? g : Guid.Empty,
                    "binary" => Convert.FromBase64String(text),
                    "undef" => null,
                    _ => text,
                };
            }
        }
    }
}
