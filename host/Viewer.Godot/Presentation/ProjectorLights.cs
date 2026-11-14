// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Core.World;

namespace Viewer.Godot.Presentation;

/// <summary>
/// The engine lights of the prims that carry a light: an omni light for a plain point light, a spot light (with the
/// projector picture when its texture has arrived) for a projector. The numbers come from <see cref="LightRules"/>
/// (Docs/PROJECTOR-SPEC.md). Projectors cast shadows as the official viewer's default does: the two with the largest area on screen
/// (<see cref="LightRules.ShadowCasters"/>), and none when <see cref="Shadows"/> is off. A plain point light never casts one.
/// Every light sits on the view's layer and on the glow picture's, because the glow camera takes only the lights on the
/// layers it sees: a glowing face lit by a local light must be as lit there as in the view.
/// </summary>
internal sealed class ProjectorLights(Rid scenario)
{
    /// <summary>The layers of a light's instance: the view's and the glow picture's.</summary>
    public const uint LightLayers = 1u | GlowPass.InGlowPicture;

    /// <summary>The layer a projector's own prim is on instead of the view's: a projector's apex sits behind its prim's face, so the prim stands between
    /// the light and everything it should light. The official viewer leaves the light's own prim out of its shadow map (pipeline.cpp, RenderSpotLight);
    /// here a projector's shadow takes only the objects on <see cref="ShadowCasterLayers"/>, which excludes every projector prim (so a projector's prim
    /// casts no shadow into another projector's beam either). The camera sees all of the layers below the glow picture's.</summary>
    public const uint LampLayer = 1u << 18;

    /// <summary>The layers whose objects cast into a projector's shadow: the view's, which an object has while it is drawn in the glow picture too.</summary>
    public const uint ShadowCasterLayers = 1u;

    /// <summary>Negative control for the glow-light check (--tr-alter=nolightglow): the lights stay on the view's layer only.</summary>
    public static bool TestViewLayerOnly;

    /// <summary>The setting that turns projector shadows off (the official viewer's shadow detail below its default of 2). On by default.</summary>
    public static bool Shadows = true;

    private const float SpotEdgeExponent = 32f;
    private const float ShadowBias = 0.1f, ShadowNormalBias = 2f;

    // Godot's own rotation: a spot light shines along its local -Z, a prim's light along the prim's -Z, which is the engine's local -Y here
    private static readonly Basis ShineDown = new(new Vector3(1f, 0f, 0f), -Mathf.Pi / 2f);

    private sealed record Entry(Rid Light, Rid Instance, bool Spot)
    {
        public bool Casting;   // the light's shadow is on (its opacity is 1)
        public System.Numerics.Vector3 Shine;   // SL axes
        public float Radius;
    }
    private readonly Dictionary<uint, Entry> _lights = new();
    private readonly Dictionary<uint, System.Numerics.Vector3> _origins = new();   // SL axes, for choosing the nearest lights
    private readonly HashSet<uint> _hidden = new();

    public int Count => _lights.Count;

    /// <summary>The engine lights now held (omni, spot).</summary>
    public (int Omni, int Spot) Kinds()
    {
        int spot = 0;
        foreach (var e in _lights.Values) if (e.Spot) spot++;
        return (_lights.Count - spot, spot);
    }

    /// <summary>Makes or updates the light of object <paramref name="local"/>: <paramref name="e"/> null removes it.
    /// <paramref name="rotation"/> is the prim's orientation without its scale, <paramref name="projector"/> the picture's
    /// texture (invalid: none yet).</summary>
    public void Set(uint local, EngineLight? e, Vector3 origin, Basis rotation, Rid projector, float radius)
    {
        if (e is not { } light) { Remove(local); return; }
        if (_lights.TryGetValue(local, out var have) && have.Spot != light.IsSpot) Remove(local);
        if (!_lights.TryGetValue(local, out var entry))
        {
            var rid = Rids.LightCreate(light.IsSpot, "projector light");
            var inst = Rids.InstanceCreate2(rid, scenario, "projector light instance");
            RenderingServer.InstanceSetLayerMask(inst, TestViewLayerOnly ? 1u : LightLayers);
            // Without a shadow slot the engine does not set up a spot light's projection, and the picture lights nothing (verified on
            // Vulkan: a white picture on a spot light without shadows lit nothing, with them it lit the cone). So every projector keeps its
            // shadow slot, and its opacity (0 or 1) says whether it casts: see Cull. A plain point light has no shadow.
            RenderingServer.LightSetShadow(rid, light.IsSpot);
            if (light.IsSpot)
            {
                RenderingServer.LightSetParam(rid, RenderingServer.LightParam.ShadowOpacity, 0f);
                RenderingServer.LightSetShadowCasterMask(rid, 0u);   // no caster until CastShadows ranks this projector: a light at opacity 0 still renders its shadow map
                // the engine node's own defaults, set outright: the server's defaults are smaller, and a thin face then shadows itself (a glowing
                // panel in a beam came out 12 % darker in the view than in the glow picture)
                RenderingServer.LightSetParam(rid, RenderingServer.LightParam.ShadowBias, ShadowBias);
                RenderingServer.LightSetParam(rid, RenderingServer.LightParam.ShadowNormalBias, ShadowNormalBias);
                RenderingServer.LightSetParam(rid, RenderingServer.LightParam.ShadowBlur, 1f);
            }
            _lights[local] = entry = new Entry(rid, inst, light.IsSpot);
        }
        var l = entry.Light;
        entry.Radius = radius;
        RenderingServer.LightSetColor(l, new Color(light.ColourSrgb.X, light.ColourSrgb.Y, light.ColourSrgb.Z));
        RenderingServer.LightSetParam(l, RenderingServer.LightParam.Energy, light.Energy);
        RenderingServer.LightSetParam(l, RenderingServer.LightParam.Range, light.Range);
        RenderingServer.LightSetParam(l, RenderingServer.LightParam.Attenuation, light.Attenuation);
        Transform3D xf;
        if (light.IsSpot)
        {
            RenderingServer.LightSetParam(l, RenderingServer.LightParam.SpotAngle, Mathf.RadToDeg(light.SpotHalfAngle));
            // the engine fades a spot light by 1 - rim^exponent (rim: 0 on the axis, 1 at the cone's edge): a large exponent keeps the whole
            // cone lit and the fade to its very edge (an exponent of 0 would put the whole light out)
            RenderingServer.LightSetParam(l, RenderingServer.LightParam.SpotAttenuation, SpotEdgeExponent);
            RenderingServer.LightSetProjector(l, projector);
            float apex = light.Beam!.Value.Apex.Z;   // along the prim's Z, which is the engine's Y
            xf = new Transform3D(rotation * ShineDown, origin + rotation * new Vector3(0f, apex, 0f));
        }
        else xf = new Transform3D(rotation, origin);
        RenderingServer.InstanceSetTransform(entry.Instance, xf);
        RenderingServer.InstanceSetVisible(entry.Instance, !_hidden.Contains(local));
        _origins[local] = new System.Numerics.Vector3(origin.X, -origin.Z, origin.Y);
        if (light.IsSpot)
        {
            var d = rotation * new Vector3(0f, -1f, 0f);   // the prim's -Z in engine axes
            entry.Shine = new System.Numerics.Vector3(d.X, -d.Z, d.Y);
        }
    }

    /// <summary>Draws only the <see cref="LightRules.MaxLocalLights"/> lights nearest the camera (engine axes), as the official viewer does. Called once a frame.</summary>
    public void Cull(Vector3 camera, Vector3 cameraAt)
    {
        if (_lights.Count == 0 && _hidden.Count == 0) return;
        var eye = new System.Numerics.Vector3(camera.X, -camera.Z, camera.Y);
        var keep = LightRules.Nearest(_origins.Select(o => (o.Key, o.Value)).ToList(), eye, LightRules.MaxLocalLights);
        foreach (var (local, e) in _lights)
        {
            bool shown = keep.Contains(local);
            if (shown == !_hidden.Contains(local)) continue;
            if (shown) _hidden.Remove(local); else _hidden.Add(local);
            RenderingServer.InstanceSetVisible(e.Instance, shown);
        }
        CastShadows(eye, new System.Numerics.Vector3(cameraAt.X, -cameraAt.Z, cameraAt.Y));
    }

    /// <summary>Gives the shadow to the <see cref="LightRules.MaxShadowedProjectors"/> shown projectors the official viewer would rank highest (or to
    /// none when <see cref="Shadows"/> is off); every other projector keeps its slot at opacity 0 and an empty caster mask.</summary>
    private void CastShadows(System.Numerics.Vector3 eye, System.Numerics.Vector3 at)
    {
        var casters = Shadows
            ? LightRules.ShadowCasters(_lights.Where(kv => kv.Value.Spot && !_hidden.Contains(kv.Key)).Select(kv => (kv.Key, _origins[kv.Key], kv.Value.Shine, kv.Value.Radius)).ToList(),
                eye, at, LightRules.MaxShadowedProjectors)
            : [];
        foreach (var (local, e) in _lights)
        {
            if (!e.Spot) continue;
            bool cast = casters.Contains(local);
            if (cast == e.Casting) continue;
            e.Casting = cast;
            RenderingServer.LightSetParam(e.Light, RenderingServer.LightParam.ShadowOpacity, cast ? 1f : 0f);
            // the engine renders the shadow map of every light that has a shadow slot, whatever its opacity, so a projector that is not a caster
            // also takes no object into its map: its pass then has nothing to draw
            RenderingServer.LightSetShadowCasterMask(e.Light, cast ? ShadowCasterLayers : 0u);
        }
    }

    public void Remove(uint local)
    {
        _origins.Remove(local);
        _hidden.Remove(local);
        if (!_lights.Remove(local, out var e)) return;
        Rids.Free(e.Instance);
        Rids.Free(e.Light);
    }

    public void Clear()
    {
        foreach (var e in _lights.Values)
        {
            Rids.Free(e.Instance);
            Rids.Free(e.Light);
        }
        _lights.Clear();
        _origins.Clear();
        _hidden.Clear();
    }
}
