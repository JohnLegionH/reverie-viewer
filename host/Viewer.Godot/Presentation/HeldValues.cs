// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot.Presentation;

/// <summary>
/// Godot values made from managed data, held until the engine has taken its own copy.
/// GodotSharp 4.7.2 (read from its IL): a <see cref="Variant"/> of a type with native storage (a
/// string, an AABB, a packed array made from a managed array) owns that storage through a finalizable disposer, and the
/// setters of <c>Godot.Collections.Dictionary</c> and <c>Godot.Collections.Array</c> pass only the variant's native value
/// to the engine's copy. An implicit variant (<c>d["vertex_data"] = bytes</c>) is unreachable once its native value is
/// read, so a collection during the copy can free the storage while the engine takes its reference: the race
/// (<c>_ref !success</c> then 0xC0000005, or heap corruption, 0xC0000374). Every such value goes through <see cref="Add"/>:
/// the list keeps its disposer reachable while the holder is; the caller keeps the holder alive past the engine call
/// (<see cref="GC.KeepAlive"/>). <see cref="Dispose"/> then only lets go of them, and their finalizers free the native
/// storage, as before. Not <see cref="Variant.Dispose"/>: GodotSharp 4.7.2 passes the disposer's own field to
/// <c>godotsharp_variant_destroy</c> as an unpinned pointer (<c>CustomUnsafe.AsPointer</c>), so a collection on another
/// thread that moves the disposer during that call makes the engine read and write the old address (0xC0000005 in
/// the first version's <c>HeldValues.Dispose</c>, 2 of 40 surface stress runs, a collection in progress on another thread).
/// Values without native storage (int, long, float, bool, vectors, colours) need no holding.
/// Letting go for the finalizers moves the same unpinned destroy to the finalizer
/// thread (a Variant finalizer crashed the standalone reproduction, 0xC0000005 in <c>godotsharp_variant_destroy</c>), so
/// <see cref="Dispose"/> now disposes each value right after the call while its disposer is pinned
/// (<see cref="VariantDisposal"/>); this replaces the "let go for the finalizers". If the disposer cannot be reached
/// (another GodotSharp), the values are let go for the finalizers as before, and that is logged once.
/// </summary>
internal sealed class HeldValues : IDisposable
{
    private readonly List<(Variant Value, long Bytes)> _held = new(4);

    /// <summary>Holds the packed array made from <paramref name="a"/> and returns it, for the setter (its native size
    /// counted for the disposal queue's report).</summary>
    public Variant Add(Vector3[] a) => Hold(a, a.LongLength * 12);
    public Variant Add(Vector2[] a) => Hold(a, a.LongLength * 8);
    public Variant Add(Color[] a) => Hold(a, a.LongLength * 16);
    public Variant Add(int[] a) => Hold(a, a.LongLength * 4);
    public Variant Add(float[] a) => Hold(a, a.LongLength * 4);
    public Variant Add(byte[] a) => Hold(a, a.LongLength);
    public Variant Add(Aabb a) => Hold(a, 24);

    private Variant Hold(Variant value, long bytes)
    {
        _held.Add((value, bytes));
        return value;
    }

    /// <summary>After the engine call. (reworked): nothing is disposed here (this is inside the site's
    /// timed unit); the values go, still held, to the disposal queue, which disposes each one pinned after the frame's
    /// upload budget (<see cref="VariantDisposal"/>). Without the pinned disposal they are let go for their finalizers.</summary>
    public void Dispose()
    {
        foreach (var (v, bytes) in _held) VariantDisposal.Defer(v, bytes);
        _held.Clear();
    }
}

/// <summary>
/// A <see cref="Variant"/> with native storage owns it through a private,
/// finalizable disposer object, whose own field GodotSharp 4.7.2 hands to <c>godotsharp_variant_destroy</c> as an unpinned
/// pointer, from <c>Dispose</c> and from the finalizer alike. <see cref="DisposePinned"/> disposes the variant while that
/// object is pinned (<see cref="KeptCollections.Pin"/> under <c>fixed</c>): the pointer then stays valid for the whole
/// call, and <c>Dispose</c> suppresses the finalizer, so no finalizer ever destroys it. The disposer is reached by a
/// compiled getter of GodotSharp 4.7.2's private field <c>Variant._disposer</c>; with another GodotSharp, or if the field
/// is not there, nothing is disposed (the values are let go for their finalizers, holding pattern), and that is
/// logged once. It never throws. Main thread only.
/// The getter is built at startup (<see cref="Prepare"/>, before the
/// first frame), and no value is disposed inside a site's call: <see cref="Defer"/> queues it, still held, and
/// <see cref="DrainFrame"/> disposes a bounded amount after the frame's upload budget (<see cref="DeferredDisposals{T}"/>:
/// a 0.5 ms slice, 0.9 ms when behind, so a frame's drain stays under 1 ms). A region change, the presenter's dispose and
/// shutdown drain it all (<see cref="DrainAll"/>), so nothing is ever left to a finalizer. Without the getter the queue
/// is not used and the values are let go for their finalizers, logged once at startup.
/// </summary>
internal static class VariantDisposal
{
    public const string ExpectedGodotSharp = "4.7.2";
    private static readonly Func<Variant, object?>? DisposerOf = MakeGetter(out _unavailable);
    private static readonly string? _unavailable;
    private static bool _logged;
    private static DeferredDisposals<Variant>? _queue;

    /// <summary>Disposals made anywhere but a drain (the harness check wants 0), values that owned no
    /// native storage, values let go for their finalizers (only without the getter), and the drains by reason.</summary>
    public static long DisposalsOutsideDrain { get; private set; }
    public static long NoStorage { get; private set; }
    public static long LetGo { get; private set; }
    private static bool Draining;
    private static readonly Dictionary<string, (int Drains, long Values, double MaxMs)> FullDrains = new();

    /// <summary>The queue (null before <see cref="Prepare"/>, and when the pinned disposal is unavailable).</summary>
    public static DeferredDisposals<Variant>? Queue => _queue;
    public static int Queued => _queue?.Count ?? 0;

    /// <summary>At startup, main thread: builds the getter (the static initializer), makes the queue, or logs once why
    /// the pinned disposal is unavailable.</summary>
    public static void Prepare()
    {
        if (DisposerOf is null) { LogFallbackOnce(); return; }
        if (_queue is not null) return;
        _queue = new DeferredDisposals<Variant>(DisposePinned);
        // the disposal's first run (its compilation, the getter's first call) paid here, not in a frame's drain
        _queue.Add(new byte[1], 1);
        Draining = true;
        try { _queue.DrainAll(); }
        finally { Draining = false; }
    }

    /// <summary>After an engine call: <paramref name="value"/> queued (held) for the drain; without the getter, let go.</summary>
    public static void Defer(Variant value, long bytes)
    {
        if (DisposerOf is null) { LogFallbackOnce(); LetGo++; return; }
        (_queue ??= new DeferredDisposals<Variant>(DisposePinned)).Add(value, bytes);
    }

    /// <summary>
    /// Sets a shader parameter to an engine object (a texture). The variant that carries it holds its own reference to
    /// the object; an implicit one is never disposed, so that reference went only when a collection found it and the
    /// finalizer thread destroyed it, keeping the texture and its video memory alive past the region that made it. Here
    /// the variant is made explicitly and queued like every other held value: the frame's drain or the region change's
    /// full drain disposes it pinned, on the main thread. Main thread only.
    /// </summary>
    public static void SetShaderParameter(ShaderMaterial material, StringName name, GodotObject value)
    {
        Variant v = value;
        material.SetShaderParameter(name, v);
        Defer(v, 0);
    }

    /// <summary>Once per frame, after the frame's upload budget (<see cref="WorldPresenter.Frame"/>): the bounded drain.</summary>
    public static DisposalDrain DrainFrame()
    {
        if (_queue is null || _queue.Count == 0) return default;
        Draining = true;
        try { return _queue.DrainFrame(); }
        finally { Draining = false; }
    }

    /// <summary>A region change, the presenter's dispose, shutdown (and test-render flushes): every held value disposed.</summary>
    public static DisposalDrain DrainAll(string reason)
    {
        LoggedErrorReleases.DrainAll();   // the engine errors' backtraces go at every full drain too
        if (_queue is null || _queue.Count == 0) return default;
        Draining = true;
        DisposalDrain d;
        try { d = _queue.DrainAll(); }
        finally { Draining = false; }
        var (n, values, max) = FullDrains.GetValueOrDefault(reason);
        FullDrains[reason] = (n + 1, values + d.Values, Math.Max(max, d.Ms));
        return d;
    }

    /// <summary>What the queue did (test renders' sidecars; the live check).</summary>
    public static object Report() => new
    {
        pinned = DisposerOf is not null,
        unavailable = _unavailable,
        queued = _queue?.Queued ?? 0,
        disposed = _queue?.Disposed ?? 0,
        no_storage = NoStorage,
        let_go = LetGo,
        errors = _queue?.Errors ?? 0,
        last_error = _queue?.LastError,
        disposals_outside_drain = DisposalsOutsideDrain,
        waiting = _queue?.Count ?? 0,
        waiting_bytes = _queue?.HeldBytes ?? 0,
        peak_values = _queue?.PeakCount ?? 0,
        peak_bytes = _queue?.PeakBytes ?? 0,
        bytes_disposed = _queue?.BytesDisposed ?? 0,
        drain_frames = _queue?.DrainFrames ?? 0,
        frames_behind = _queue?.FramesBehind ?? 0,
        forced_drains = _queue?.ForcedDrains ?? 0,
        frame_ms_max = Math.Round(_queue?.MaxFrameMs ?? 0, 4),
        frame_ms_p95 = Math.Round(_queue?.FrameMsP95 ?? 0, 4),
        value_ms_max = Math.Round(_queue?.MaxValueMs ?? 0, 4),
        frame_value_ms_max = Math.Round(_queue?.MaxFrameValueMs ?? 0, 4),
        late_starts = _queue?.LateStarts ?? 0,
        stalled_frames = _queue?.StalledFrames ?? 0,
        drains_with_collection = _queue?.DrainsWithCollection ?? 0,
        full_drain_ms_max = Math.Round(_queue?.MaxFullDrainMs ?? 0, 4),
        full_drains = FullDrains.ToDictionary(kv => kv.Key, kv => new { drains = kv.Value.Drains, values = kv.Value.Values, max_ms = Math.Round(kv.Value.MaxMs, 4) }),
    };

    private static void LogFallbackOnce()
    {
        if (_logged) return;
        _logged = true;
        Main.Log.Warn("godot.variant_disposal_fallback", Viewer.Core.Diagnostics.CoreLog.F("reason", _unavailable));
    }

    /// <summary>Why the pinned disposal is not in use (null: it is).</summary>
    public static string? Unavailable => _unavailable;

    private static Func<Variant, object?>? MakeGetter(out string? unavailable)
    {
        try
        {
            string version = typeof(Variant).Assembly.GetName().Version?.ToString() ?? "?";
            if (!version.StartsWith(ExpectedGodotSharp + ".", StringComparison.Ordinal))
            {
                unavailable = $"GodotSharp {version}, not {ExpectedGodotSharp}";
                return null;
            }
            var field = typeof(Variant).GetField("_disposer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field is null || field.FieldType.IsValueType)
            {
                unavailable = "Godot.Variant._disposer not found";
                return null;
            }
            var p = System.Linq.Expressions.Expression.Parameter(typeof(Variant), "v");
            var getter = System.Linq.Expressions.Expression.Lambda<Func<Variant, object?>>(
                System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Field(p, field), typeof(object)), p).Compile();
            unavailable = null;
            return getter;
        }
        catch (Exception e)
        {
            unavailable = $"{e.GetType().Name}: {e.Message}";
            return null;
        }
    }

    /// <summary>Disposes <paramref name="value"/> while its disposer is pinned (the queue's disposal; only with the getter).</summary>
    private static unsafe void DisposePinned(Variant value)
    {
        if (!Draining) DisposalsOutsideDrain++;
        object? disposer = DisposerOf!(value);
        if (disposer is null) { NoStorage++; return; }   // no native storage owned: nothing to destroy
        fixed (byte* p = &KeptCollections.Pin(disposer))
            value.Dispose();
    }
}

/// <summary>
/// The names the viewer gives the engine over and over (the surface dictionary's keys, shader
/// parameter names), made once and held for the life of the process, so no temporary string variant or
/// <see cref="StringName"/> is made at each call and left to a finalizer while the engine reads it.
/// </summary>
internal static class EngineNames
{
    // the surface dictionary of RenderingServer.MeshAddSurface (handover)
    public static readonly Variant Primitive = "primitive";
    public static readonly Variant Format = "format";
    public static readonly Variant VertexData = "vertex_data";
    public static readonly Variant VertexCount = "vertex_count";
    public static readonly Variant Aabb = "aabb";
    public static readonly Variant AttributeData = "attribute_data";
    public static readonly Variant IndexData = "index_data";
    public static readonly Variant IndexCount = "index_count";
    public static readonly Variant SkinData = "skin_data";   // skinned surfaces (AvatarLayer)

    // shader parameters: prim faces (FaceShader), water (EnvironmentLayer), terrain (TerrainView)
    public static readonly StringName ObjScale = "obj_scale";
    public static readonly StringName WaterClip = "water_clip";   // a blended face's water cut (FaceShader.WaterCut)
    public static readonly StringName GlowW = "glow_w";   // a face's glow weight (FaceShader)
    // the skinning added to a worn rigged mesh's face shader (FaceShader.Skin): the two textures and the two rows an instance picks
    public static readonly StringName SkinBones = "skin_bones";
    public static readonly StringName SkinAtlas = "skin_atlas";
    public static readonly StringName AvatarRow = "avatar_row";
    public static readonly StringName SkinRow = "skin_row";
    public static readonly StringName Tex = "tex";
    public static readonly StringName Tint = "tint";
    public static readonly StringName Rep = "rep";
    public static readonly StringName Off = "off";
    public static readonly StringName Rot = "rot";
    public static readonly StringName Planar = "planar";
    public static readonly StringName FogColor = "fog_color";
    public static readonly StringName FogDensity = "fog_density";
    public static readonly StringName FogSlant = "fog_slant";
    public static readonly StringName FresnelScale = "fresnel_scale";
    public static readonly StringName FresnelOffset = "fresnel_offset";
    public static readonly StringName Profile = "profile";
    public static readonly StringName LightDir = "light_dir";
    public static readonly StringName Wave1 = "wave1";
    public static readonly StringName Wave2 = "wave2";
    public static readonly StringName TimeS = "time_s";
    public static readonly StringName Bump = "bump";
    public static readonly StringName NormalScale = "normal_scale";
    public static readonly StringName RefScale = "ref_scale";
    public static readonly StringName SpecColor = "spec_color";
    public static readonly StringName Sunlit = "sunlit";
    public static readonly StringName HazeComb = "haze_comb";
    public static readonly StringName HazeK = "haze_k";
    public static readonly StringName FlatWater = "flat_water";
    public static readonly StringName UnderLevel = "under_level";
    public static readonly StringName UnderDensity = "under_density";
    public static readonly StringName ScaleBelow = "scale_below";
    public static readonly StringName GlobalUnderLevel = "water_under_level", GlobalUnderColor = "water_under_color", GlobalUnderDensity = "water_under_density", GlobalUnderSlant = "water_under_slant";
    public static readonly StringName AirMode = "air_mode", AirFogColor = "air_fog_color", AirFogEnergy = "air_fog_energy", AirFogDensity = "air_fog_density", AirConstant = "air_constant", AirGlow = "air_glow", AirExponent = "air_exponent", AirLightClamped = "air_light_clamped", AirLightRaw = "air_light_raw";
    public static readonly StringName OriginOffset = "origin_offset";
    public static readonly StringName Ramp = "ramp";
    public static readonly StringName[] Detail = ["detail0", "detail1", "detail2", "detail3"];
    // the PBR face shader (FaceShader.CreatePbr): per slot (base colour, normal, metallic-roughness, emissive) its
    // texture, its transform's scale and rotation, and its offset
    public static readonly StringName[] PbrTex = ["base_tex", "nrm_tex", "orm_tex", "emis_tex"];
    public static readonly StringName[] PbrXf = ["xf_base", "xf_nrm", "xf_orm", "xf_emis"];
    public static readonly StringName[] PbrOff = ["off_base", "off_nrm", "off_orm", "off_emis"];
    public static readonly StringName BaseFactor = "base_factor";
    public static readonly StringName EmisFactor = "emis_factor";
    public static readonly StringName Metallic = "metallic";
    public static readonly StringName Roughness = "roughness";
    public static readonly StringName UseNormal = "use_normal";
    public static readonly StringName AlphaCutoff = "alpha_cutoff";
    // the legacy material shader (FaceShader.CreateLegacy): its normal and specular maps, their placements, and the specular values
    public static readonly StringName NrmTex = "nrm_tex";
    public static readonly StringName SpecTex = "spec_tex";
    public static readonly StringName NrmXf = "nrm_xf";
    public static readonly StringName SpecXf = "spec_xf";
    public static readonly StringName NrmRot = "nrm_rot";
    public static readonly StringName SpecRot = "spec_rot";
    public static readonly StringName UseSpec = "use_spec";
    public static readonly StringName SpecCol = "spec_col";
    public static readonly StringName Gloss = "gloss";
}
