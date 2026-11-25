// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using Viewer.Core.Meshing;
using Viewer.Core.World;
using Viewer.Shell;

namespace Viewer.Godot.Presentation;

/// <summary>
/// The viewer's shaders, made once for the process at start-up and never freed (as
/// <see cref="KeptCollections"/>). The engine compiles a shader and its pipelines per shader object: with the shader cache
/// off (until) a new shader with the same code is compiled again, and a standard material's shader lives only
/// while a material with its features does. So the face shader's four variants, the terrain and water shaders, and one
/// standard material per feature set the viewer uses are kept here; every layer's materials take their shader from here.
/// Main thread only.
/// </summary>
public static class KeptShaders
{
    private static readonly Dictionary<(bool Fullbright, bool Blend), Shader> s_face = new();
    private static readonly Dictionary<bool, Shader> s_faceMasked = new();   // the face shader drawing a cut-out (lit, fullbright)
    private static readonly Dictionary<(bool DoubleSided, FaceAlpha Alpha), Shader> s_pbr = new();
    private static readonly Dictionary<(bool Fullbright, FaceAlpha Alpha), Shader> s_legacy = new();
    // the same two shaders skinning a worn rigged mesh (FaceShader.Skin)
    private static readonly Dictionary<(bool Fullbright, bool Blend), Shader> s_faceSkinned = new();
    private static readonly Dictionary<bool, Shader> s_faceTinted = new();   // the merged cells' face shader (vertex colour as tint)
    private static readonly Dictionary<(bool Fullbright, FaceAlpha Alpha), Shader> s_legacySkinned = new();

    /// <summary>The alpha modes the PBR shader is made in (glTF: opaque, blend, mask).</summary>
    public static readonly FaceAlpha[] PbrAlphas = [FaceAlpha.Opaque, FaceAlpha.Blend, FaceAlpha.Mask];

    /// <summary>The legacy material shader's variants: lit in every alpha mode, fullbright in all but emissive (a
    /// fullbright face is all unlit already, so its emissive mask changes nothing and it uses the opaque variant).</summary>
    public static IEnumerable<(bool Fullbright, FaceAlpha Alpha)> LegacyVariants()
    {
        foreach (var a in new[] { FaceAlpha.Opaque, FaceAlpha.Blend, FaceAlpha.Mask, FaceAlpha.Emissive }) yield return (false, a);
        foreach (var a in new[] { FaceAlpha.Opaque, FaceAlpha.Blend, FaceAlpha.Mask }) yield return (true, a);
    }
    private static Shader? s_terrain, s_water, s_waterHaze, s_skip, s_sky;
    private static ShaderMaterial? s_skipMaterial;
    private static readonly List<StandardMaterial3D> s_standard = new();

    /// <summary>The face shader variant (<see cref="FaceShader"/>): lit or fullbright, opaque or alpha-blended.</summary>
    public static Shader Face(bool fullbright, bool blend)
    {
        if (!s_face.TryGetValue((fullbright, blend), out var s)) s_face[(fullbright, blend)] = s = FaceShader.Create(fullbright, blend);
        return s;
    }

    /// <summary>The face shader variant that draws a face as a cut-out (<see cref="FaceShader.CreateMasked"/>): lit or fullbright.</summary>
    public static Shader FaceMasked(bool fullbright)
    {
        if (!s_faceMasked.TryGetValue(fullbright, out var s)) s_faceMasked[fullbright] = s = FaceShader.CreateMasked(fullbright);
        return s;
    }

    private static readonly Dictionary<bool, Shader> s_body = new();

    /// <summary>The system body's shader (<see cref="FaceShader.CreateBody"/>): the lit face shader with the alpha cut, opaque or (eyelashes) blended.</summary>
    public static Shader Body(bool blend)
    {
        if (!s_body.TryGetValue(blend, out var s)) s_body[blend] = s = FaceShader.CreateBody(blend);
        return s;
    }

    /// <summary>The opaque face shader reading its tint from the vertex colour (<see cref="FaceShader.CreateTinted"/>), for merged cells.</summary>
    public static Shader FaceTinted(bool fullbright)
    {
        if (!s_faceTinted.TryGetValue(fullbright, out var s)) s_faceTinted[fullbright] = s = FaceShader.CreateTinted(fullbright);
        return s;
    }

    /// <summary>The PBR face shader (<see cref="FaceShader.CreatePbr"/>): single- or double-sided, in its alpha mode.</summary>
    public static Shader Pbr(bool doubleSided, FaceAlpha alpha = FaceAlpha.Opaque)
    {
        if (!s_pbr.TryGetValue((doubleSided, alpha), out var s)) s_pbr[(doubleSided, alpha)] = s = FaceShader.CreatePbr(doubleSided, alpha);
        return s;
    }

    /// <summary>The legacy material face shader (<see cref="FaceShader.CreateLegacy"/>); a fullbright emissive face uses
    /// the fullbright opaque variant.</summary>
    public static Shader Legacy(bool fullbright, FaceAlpha alpha)
    {
        if (fullbright && alpha == FaceAlpha.Emissive) alpha = FaceAlpha.Opaque;
        if (!s_legacy.TryGetValue((fullbright, alpha), out var s)) s_legacy[(fullbright, alpha)] = s = FaceShader.CreateLegacy(fullbright, alpha);
        return s;
    }

    /// <summary>The face shader skinning a worn rigged mesh (<see cref="FaceShader.CreateSkinned"/>).</summary>
    public static Shader FaceSkinned(bool fullbright, bool blend)
    {
        if (!s_faceSkinned.TryGetValue((fullbright, blend), out var s)) s_faceSkinned[(fullbright, blend)] = s = FaceShader.CreateSkinned(fullbright, blend);
        return s;
    }

    /// <summary>The legacy material shader skinning a worn rigged mesh (<see cref="FaceShader.CreateLegacySkinned"/>).</summary>
    public static Shader LegacySkinned(bool fullbright, FaceAlpha alpha)
    {
        if (fullbright && alpha == FaceAlpha.Emissive) alpha = FaceAlpha.Opaque;
        if (!s_legacySkinned.TryGetValue((fullbright, alpha), out var s)) s_legacySkinned[(fullbright, alpha)] = s = FaceShader.CreateLegacySkinned(fullbright, alpha);
        return s;
    }

    /// <summary>The composed terrain shader (<see cref="ChunkArrays.TerrainMaterial"/>).</summary>
    public static Shader Terrain => s_terrain ??= new Shader { Code = ChunkArrays.TerrainCode };

    private static ImageTexture? s_terrainRamp;
    /// <summary>The terrain blend ramp picture (<see cref="Viewer.Core.World.TerrainRamp"/>) as one eight-bit texture, made once and never freed.</summary>
    public static ImageTexture TerrainRamp => s_terrainRamp ??= ImageTexture.CreateFromImage(Image.CreateFromData(Viewer.Core.World.TerrainRamp.Size, Viewer.Core.World.TerrainRamp.Size, false, Image.Format.R8, Viewer.Core.World.TerrainRamp.BottomFirst));

    /// <summary>The water shader (<see cref="EnvironmentLayer"/>).</summary>
    public static Shader Water => s_water ??= new Shader { Code = EnvironmentLayer.WaterCode };

    /// <summary>The haze of the view from below the water (<see cref="EnvironmentLayer.WaterHazeCode"/>).</summary>
    public static Shader WaterHaze => s_waterHaze ??= new Shader { Code = EnvironmentLayer.WaterHazeCode };

    /// <summary>Whether this process has made the underwater haze shader already (a test asks, to say whether a dive it times is a first one).</summary>
    internal static bool WaterHazeMade => s_waterHaze is not null;

    /// <summary>The sky shader (<see cref="EnvironmentLayer.SkyCode"/>).</summary>
    public static Shader Sky => s_sky ??= new Shader { Code = EnvironmentLayer.SkyCode };

    /// <summary>The skip shader (<see cref="FaceShader.SkipCode"/>) and the one material every layer draws skipped surfaces with.</summary>
    public static Shader Skip => s_skip ??= new Shader { Code = FaceShader.SkipCode };
    public static ShaderMaterial SkipMaterial => s_skipMaterial ??= new ShaderMaterial { Shader = Skip };

    /// <summary>Every shader object kept (the face variants, the PBR variants, terrain, water, skip) and the materials holding theirs.</summary>
    public static int Count => s_body.Count + s_face.Count + s_faceMasked.Count + s_faceTinted.Count + s_pbr.Count + s_legacy.Count + s_faceSkinned.Count + s_legacySkinned.Count + (s_terrain is null ? 0 : 1) + (s_water is null ? 0 : 1) + (s_waterHaze is null ? 0 : 1) + (s_sky is null ? 0 : 1) + (s_skip is null ? 0 : 1)
        + s_standard.Count + (s_skipMaterial is null ? 0 : 1);

    /// <summary>Makes every kept shader, and one standard material for each feature set the viewer's standard materials use
    /// (the height-coloured terrain; a prim face's colour, opaque and alpha), so their shaders stay made. Idempotent.</summary>
    public static void PrepareAtStartup()
    {
        foreach (bool fullbright in new[] { false, true })
            foreach (bool blend in new[] { false, true })
                Face(fullbright, blend);
        FaceMasked(false);
        FaceMasked(true);
        Body(false);
        Body(true);
        FaceTinted(false);
        FaceTinted(true);
        foreach (bool doubleSided in new[] { false, true })
            foreach (var a in PbrAlphas)
                Pbr(doubleSided, a);
        foreach (var (fullbright, a) in LegacyVariants())
            Legacy(fullbright, a);
        foreach (bool fullbright in new[] { false, true })
            foreach (bool blend in new[] { false, true })
                FaceSkinned(fullbright, blend);
        foreach (var (fullbright, a) in LegacyVariants())
            LegacySkinned(fullbright, a);
        _ = Terrain;
        _ = Water;
        _ = Sky;
        GlowPass.PrepareShaders();
        _ = SkipMaterial.GetRid();
        if (s_standard.Count == 0)
        {
            s_standard.Add(ChunkArrays.Material());
            s_standard.Add(PrimLayer.ColourMaterial(new Rgba(1f, 1f, 1f, 1f)));
            s_standard.Add(PrimLayer.ColourMaterial(new Rgba(1f, 1f, 1f, 0.5f)));
        }
        // a standard material makes its shader when its RID is first asked for (measured: without this a
        // kept material holds no shader, and every new material with its features compiles its pipelines again)
        foreach (var m in s_standard) _ = m.GetRid();
    }
}

/// <summary>
/// The pipeline warm-up. At start-up, on the main thread, in its own hidden viewport (drawn off
/// screen, never shown), every material variant the viewer makes is drawn through the viewer's own material and surface
/// paths (the kept shaders, the prepared handover), so the engine compiles their shaders and pipelines here rather than in
/// a frame of the world. The variants: the face shader's four (lit or fullbright, opaque or blended), the PBR shader's six
/// (single- and double-sided, each opaque, blended and masked, drawn with the normal map on), the legacy material shader's
/// seven (lit in each alpha mode, fullbright opaque, blended and masked, drawn with both maps on) and a face's colour
/// material (opaque, alpha) on a face's surface (prims, sculpts and meshes share it: positions, normals, UVs), the composed
/// terrain, the height-coloured terrain (and a composed chunk drawn height-coloured while its detail textures load), the
/// water and the sky. It takes two drawn frames: the first with the plain
/// stage (as a region before its environment), the second with the region's environment applied (sky, fog, water, through
/// <see cref="EnvironmentLayer"/>); a standard material's shader is made in the frame after the material (the engine's
/// deferred update), so the first frame also makes those. Everything it makes is freed before it completes; a variant it
/// did not cover is compiled on first use, as before, and counted by the frame log. In the viewer it runs under the login
/// screen's first frames.
/// </summary>
public static class PipelineWarmup
{
    public sealed record Result(double Ms, IReadOnlyDictionary<string, long> Pipelines, int Frames, int Variants);

    private static readonly RenderingServer.RenderingInfo[] Sources =
    {
        RenderingServer.RenderingInfo.PipelineCompilationsCanvas, RenderingServer.RenderingInfo.PipelineCompilationsMesh,
        RenderingServer.RenderingInfo.PipelineCompilationsSurface, RenderingServer.RenderingInfo.PipelineCompilationsDraw,
        RenderingServer.RenderingInfo.PipelineCompilationsSpecialization,
    };
    private static readonly string[] SourceNames = { "canvas", "mesh", "surface", "draw", "specialization" };

    /// <summary>Test only (TestRender --tr-warmup=leak): the warm-up keeps one material it should free, the
    /// negative control of the warm-up check's end state. Never set by the viewer.</summary>
    internal static bool LeakOneForTest;

    /// <summary>Test only (TestRender --tr-warmup=nolights): the warm-up leaves out its lights, the negative control of the compile gate
    /// (a scene with local lights then compiles variants after the warm-up). Never set by the viewer.</summary>
    internal static bool WithoutLightsForTest;
    internal static Material? LeakedForTest;

    /// <summary>The engine's pipeline compilations so far, by source.</summary>
    public static long[] Counters() => Sources.Select(s => (long)RenderingServer.GetRenderingInfo(s)).ToArray();

    public static Dictionary<string, long> Named(long[] d) =>
        SourceNames.Zip(d).Where(p => p.Second != 0).ToDictionary(p => p.First, p => p.Second);

    /// <summary>Runs the warm-up under <paramref name="parent"/> (the viewer's root) with the multisampling of the viewport
    /// the world will be drawn in; <paramref name="nextFrame"/> completes once the next frame has been drawn. Everything it
    /// made is freed when it completes. The kept shaders are made first (<see cref="KeptShaders.PrepareAtStartup"/>).</summary>
    public static async System.Threading.Tasks.Task<Result> RunAsync(Node parent, Viewport.Msaa msaa, Func<System.Threading.Tasks.Task> nextFrame)
    {
        long t0 = Stopwatch.GetTimestamp();
        var before = Counters();
        KeptShaders.PrepareAtStartup();
        var vp = new SubViewport
        {
            Size = new Vector2I(64, 64),
            OwnWorld3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            Msaa3D = msaa,
            ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled,
            UseTaa = false,
        };
        parent.AddChild(vp);
        var env = Stage.Environment();
        var sun = Stage.Sun();
        var cam = new Camera3D { Current = true, Position = new Vector3(0, 2, 6) };
        vp.AddChild(env);
        vp.AddChild(sun);
        vp.AddChild(cam);
        cam.LookAt(Vector3.Zero, Vector3.Up);
        var world = vp.FindWorld3D();
        var scenario = world.Scenario;

        var meshes = new List<Rid>();
        var instances = new List<Rid>();
        var materials = new List<Material>();
        int variants = 0;
        void Draw(PreparedSurface surface, Material material)
        {
            var mesh = Rids.MeshCreate("pipeline warm-up mesh");
            PrimLayer.HandOver(mesh, surface);   // the viewer's one surface path
            RenderingServer.MeshSurfaceSetMaterial(mesh, 0, material.GetRid());
            var inst = Rids.InstanceCreate2(mesh, scenario, "pipeline warm-up instance");
            RenderingServer.InstanceSetLayerMask(inst, 1u | GlowPass.InGlowPicture);   // the glow pass's warm-up draws them too
            RenderingServer.InstanceSetTransform(inst, new Transform3D(Basis.Identity, new Vector3(0, 0.5f, 0)));
            meshes.Add(mesh);
            instances.Add(inst);
            variants++;
        }
        // a face as the viewer makes one (SL axes and winding, with UVs: every prim, sculpt and mesh face has them)
        float[] pos = [-1, 0, -1, 1, 0, -1, 1, 0, 1, -1, 0, 1];
        float[] nrm = [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1];
        int[] idx = [0, 1, 2, 0, 2, 3];
        var face = MeshSurfaceData.Encode(new MeshFace(pos, nrm, [0, 0, 1, 0, 1, 1, 0, 1], idx));
        foreach (bool fullbright in new[] { false, true })
            foreach (bool blend in new[] { false, true })
            {
                var m = new ShaderMaterial { Shader = KeptShaders.Face(fullbright, blend) };
                materials.Add(m);
                Draw(face, m);
            }
        foreach (bool fullbright in new[] { false, true })
        {
            var m = new ShaderMaterial { Shader = KeptShaders.FaceMasked(fullbright) };
            materials.Add(m);
            Draw(face, m);
        }
        // a merged cell's surface: the same face with a vertex colour, which is a vertex format of its own
        var taggedFace = MeshSurfaceData.Encode(pos, nrm, idx, [0, 0, 1, 0, 1, 1, 0, 1], [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1], slAxes: true, reverseWinding: true);
        foreach (bool fullbright in new[] { false, true })
        {
            var m = new ShaderMaterial { Shader = KeptShaders.FaceTinted(fullbright) };
            materials.Add(m);
            Draw(taggedFace, m);
        }
        foreach (bool doubleSided in new[] { false, true })
            foreach (var a in KeptShaders.PbrAlphas)
            {
                var m = new ShaderMaterial { Shader = KeptShaders.Pbr(doubleSided, a) };
                m.SetShaderParameter(EngineNames.UseNormal, true);   // a uniform's value adds no variant; drawn with the normal path on
                materials.Add(m);
                Draw(face, m);
            }
        foreach (var (fullbright, a) in KeptShaders.LegacyVariants())
        {
            var m = new ShaderMaterial { Shader = KeptShaders.Legacy(fullbright, a) };
            m.SetShaderParameter(EngineNames.UseNormal, true);   // drawn with the normal and specular paths on
            m.SetShaderParameter(EngineNames.UseSpec, true);
            materials.Add(m);
            Draw(face, m);
        }
        // a worn rigged mesh's face: the same shaders skinning from the joint textures, on a surface carrying joint indices and weights
        var riggedFace = SkinnedSurfaceData.Encode(new MeshFace(pos, nrm, [0, 0, 1, 0, 1, 1, 0, 1], idx),
            new FaceInfluences(4, new byte[16], [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0], 0));
        foreach (bool fullbright in new[] { false, true })
            foreach (bool blend in new[] { false, true })
            {
                var m = new ShaderMaterial { Shader = KeptShaders.FaceSkinned(fullbright, blend) };
                materials.Add(m);
                Draw(riggedFace, m);
            }
        foreach (var (fullbright, a) in KeptShaders.LegacyVariants())
        {
            var m = new ShaderMaterial { Shader = KeptShaders.LegacySkinned(fullbright, a) };
            m.SetShaderParameter(EngineNames.UseNormal, true);
            m.SetShaderParameter(EngineNames.UseSpec, true);
            materials.Add(m);
            Draw(riggedFace, m);
        }
        // the skip material (surfaces drawn by a face instance of their own; the kept material, not freed here)
        Draw(face, KeptShaders.SkipMaterial);
        // an avatar part as the avatar layer draws one: skinned (two bones a vertex) on a skeleton, with the body shader
        // opaque and blended (the eyelashes)
        var skeleton = Rids.SkeletonCreate("pipeline warm-up skeleton");
        RenderingServer.SkeletonAllocateData(skeleton, 2);
        RenderingServer.SkeletonBoneSetTransform(skeleton, 0, Transform3D.Identity);
        RenderingServer.SkeletonBoneSetTransform(skeleton, 1, Transform3D.Identity);
        var skinned = AvatarSurfaceData.Encode(new MeshFace(pos, nrm, [0, 0, 1, 0, 1, 1, 0, 1], idx), [0, 0, 1, 1], [1, 1, 1, 1], [0f, 0.5f, 0f, 0f], 2);
        foreach (bool blend in new[] { false, true })
        {
            var m = new ShaderMaterial { Shader = KeptShaders.Body(blend) };
            materials.Add(m);
            var mesh = Rids.MeshCreate("pipeline warm-up mesh");
            AvatarLayer.HandOver(mesh, skinned);
            RenderingServer.MeshSurfaceSetMaterial(mesh, 0, m.GetRid());
            var inst = Rids.InstanceCreate2(mesh, scenario, "pipeline warm-up instance");
            RenderingServer.InstanceSetLayerMask(inst, 1u | GlowPass.InGlowPicture);   // the glow pass's warm-up draws them too
            RenderingServer.InstanceAttachSkeleton(inst, skeleton);
            RenderingServer.InstanceSetCustomAabb(inst, new Aabb(new Vector3(-2, -2, -2), new Vector3(4, 4, 4)));
            meshes.Add(mesh);
            instances.Add(inst);
            variants++;
        }
        foreach (float alpha in new[] { 1f, 0.5f })
        {
            var m = PrimLayer.ColourMaterial(new Rgba(1f, 1f, 1f, alpha));
            materials.Add(m);
            Draw(face, m);
        }
        // terrain: a chunk composed (detail textures) and one height-coloured, through the terrain encoder
        float[] tpos = [-1, 0, -1, 1, 0, -1, 1, 0, 1, -1, 0, 1];
        float[] tnrm = [0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0];
        float[] tcol = [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1];
        int[] tidx = [0, 2, 1, 0, 3, 2];
        var detail = Image.CreateEmpty(4, 4, true, Image.Format.Rgba8);
        detail.Fill(new Color(0.5f, 0.5f, 0.5f));
        detail.GenerateMipmaps();
        var detailTex = ImageTexture.CreateFromImage(detail);
        detail.Dispose();
        var composed = ChunkArrays.TerrainMaterial([detailTex, detailTex, detailTex, detailTex], Vector2.Zero);
        materials.Add(composed);
        Draw(TerrainSurface.Prepare(new TerrainChunk(0, 0, tpos, tnrm, tcol, tidx, [0f, 1f, 2f, 3f])), composed);
        var height = ChunkArrays.Material();
        materials.Add(height);
        Draw(TerrainSurface.Prepare(new TerrainChunk(0, 0, tpos, tnrm, tcol, tidx)), height);
        // a composed chunk drawn height-coloured while the region's detail textures are still loading (TerrainView: the
        // chunk's surface carries its composition in UV, the material is the height-coloured one)
        Draw(TerrainSurface.Prepare(new TerrainChunk(0, 0, tpos, tnrm, tcol, tidx, [0f, 1f, 2f, 3f])), height);

        // frame 1: the plain stage; frame 2: the region's environment (the default one; the water at y = 0 under the faces)
        await nextFrame();
        var environment = new EnvironmentLayer(scenario, env, sun);
        var region = RegionEnvironment.Default;
        environment.Apply(region, region.SkyAt(0.3), 0f, 256, 256);
        await nextFrame();
        // The engine specializes every shader it draws for what the scene holds (a point light, a spot light, shadows), compiling
        // the variant the first time a frame has that mix. A region's prims bring lights in any mix, so the lights are made here
        // through the viewer's own light code, in each mix: both kinds, the projector alone, the point light alone.
        var lights = new ProjectorLights(scenario);
        var white = new System.Numerics.Vector3(1f, 1f, 1f);
        var omniLight = new EngineLight(white, 1f, 12f, 1f, false, 0f, null);
        var spotLight = new EngineLight(white, 1f, 12f, 1f, true, 0.6f, new Beam(new System.Numerics.Vector3(0f, 0f, 0.2f), 0.2f, 12f, 1.2f, 1f, 1f, 0f));
        var picture = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
        picture.Fill(new Color(1f, 1f, 1f));
        var pictureTex = ImageTexture.CreateFromImage(picture);
        picture.Dispose();
        void Light(uint local, EngineLight? light) { if (!WithoutLightsForTest) lights.Set(local, light, new Vector3(0f, 3f, 0f), Basis.Identity, pictureTex.GetRid(), 12f); }
        Light(1, omniLight);
        Light(2, spotLight);
        if (!WithoutLightsForTest) lights.Cull(cam.Position, Vector3.Forward);
        // frames 3 and 4: the glow pass drawn over the same faces (its second render of the scene, its blurs and its sum), with both lights
        var glow = new GlowPass(null, world, env);
        vp.AddChild(glow);
        glow.WarmUp();
        await nextFrame();
        await nextFrame();
        // frame 5: the projector alone; frame 6: the point light alone
        Light(1, null);
        if (!WithoutLightsForTest) lights.Cull(cam.Position, Vector3.Forward);
        await nextFrame();
        Light(2, null);
        Light(1, omniLight);
        if (!WithoutLightsForTest) lights.Cull(cam.Position, Vector3.Forward);
        await nextFrame();
        vp.RemoveChild(glow);
        glow.Free();
        lights.Clear();
        pictureTex.Dispose();

        foreach (var i in instances) Rids.Free(i);
        foreach (var m in meshes) Rids.Free(m);
        Rids.Free(skeleton);
        if (LeakOneForTest) { LeakedForTest = materials[0]; materials.RemoveAt(0); }   // a face material made above, never shared
        foreach (var m in materials) m.Dispose();
        detailTex.Dispose();
        environment.Clear();
        parent.RemoveChild(vp);
        vp.Free();   // the viewport, its stage and its render buffers
        VariantDisposal.DrainAll("warm-up");   // the textures the sky and water materials were given are held by queued variants until drained: let them go now, so what the warm-up made is free when it completes
        var after = Counters();
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        CompileWatch.Active?.Arm();   // test renders: what the engine compiles from here on is counted
        return new Result(ms, Named(after.Zip(before, (a, b) => a - b).ToArray()), 6, variants + 2);   // + water and sky
    }
}
