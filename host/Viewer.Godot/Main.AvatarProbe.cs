// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Viewer.Core.Avatars;
using Viewer.Core.Meshing;
using Viewer.Core.World;

namespace Viewer.Godot;

public partial class Main
{
    /// <summary>A development aid for the live look at another avatar's outfit: for each avatar the region holds other than the user's own, its bakes
    /// (by slot), the body slots drawn without a part, and each worn piece with its shape kind, mesh asset, face texture ids (as sent and as drawn after
    /// bake on mesh), face colours and how the layer presents it. Ids are shortened to eight characters. With <paramref name="saveSlot"/> (a bake slot such as "head") the picture the
    /// engine holds for that slot is also written as a PNG under the log folder the environment names, and its size is reported.</summary>
    public object? AvatarProbe(string? nameContains, string? saveSlot = null)
    {
        var world = Model?.Session?.World;
        var prims = _currentStack?.Prims ?? _presenter?.Prims;
        var avatars = OwnAvatars;
        if (world is null || prims is null) return null;
        static string Short(Guid g) => g.ToString("N")[..8];
        var snapshot = world.Snapshot();
        var list = new List<object>();
        foreach (var av in snapshot.Avatars.Values)
        {
            if (av.AgentId == _presented?.AgentId) continue;
            if (!string.IsNullOrEmpty(nameContains) && !av.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase)) continue;
            var appearance = world.Appearance(av.AgentId);
            var bakes = appearance?.Bakes ?? new Dictionary<string, Guid>();
            var worn = new List<string>();
            prims.WornReport(av.AgentId, worn);
            var pieces = new List<object>();
            foreach (var root in world.AttachmentRoots(av.Id.LocalId))
                foreach (var part in world.Linkset(root.Id).Prepend(root))
                {
                    var faces = new List<object>();
                    if (part.Textures is { } t)
                    {
                        int count = Math.Max(t.Overrides.Count, 1);
                        for (int i = 0; i < Math.Min(count, 16); i++)
                        {
                            var ft = t[i];
                            var r = BakeOnMesh.Resolve(ft.TextureId, bakes);
                            faces.Add(new
                            {
                                face = i, texture = Short(ft.TextureId), bake_slot = r.Slot, kind = r.Kind.ToString(), drawn_texture = Short(r.Id),
                                alpha = part.Colors is { } c ? Math.Round(c[i].A, 2) : 1.0,
                                glow = Math.Round(ft.Glow, 2), fullbright = ft.Fullbright,
                                material = MaterialOf(world, part.Materials is { } mats ? mats[i] : default),
                            });
                        }
                    }
                    pieces.Add(new
                    {
                        local = part.Id.LocalId, root = root.Id.LocalId, point = root.AttachmentPoint,
                        kind = part.Shape is null ? "none" : part.Shape.SculptType == PrimShape.SculptTypeMesh ? "mesh" : part.Shape.IsSculpt ? "sculpt" : "prim",
                        asset = part.Shape is { } s && (s.IsSculpt || s.SculptType == PrimShape.SculptTypeMesh) ? Short(s.SculptId) : "",
                        scale = new[] { part.Scale.X, part.Scale.Y, part.Scale.Z },
                        // the level of detail a mesh piece is drawn at, the level wanted now, and the distance and radius it is chosen from
                        mesh = prims.MeshReport(part.Id.LocalId) is { } mr ? new { lod = mr.Lod, available = mr.Available, wanted = mr.Wanted, distance = Math.Round(mr.Distance, 2), radius = Math.Round(mr.Radius, 3) } : null,
                        faces,
                    });
                }
            var entry = avatars?.Scene.Entries.FirstOrDefault(e => e.AgentId == av.AgentId);
            list.Add(new
            {
                agent = Short(av.AgentId), local = av.Id.LocalId, name = av.Name, position = new[] { av.Position.X, av.Position.Y, av.Position.Z },
                appearance_version = appearance?.AppearanceVersion ?? -1, yaw_deg = Math.Round(Math.Atan2(2 * (av.Rotation.W * av.Rotation.Z + av.Rotation.X * av.Rotation.Y), 1 - 2 * (av.Rotation.Y * av.Rotation.Y + av.Rotation.Z * av.Rotation.Z)) * 180 / Math.PI, 1),
                bakes = bakes.OrderBy(b => b.Key).Select(b => new { slot = b.Key, id = b.Value == Guid.Empty ? "" : Short(b.Value) }).ToArray(),
                hidden_body_slots = entry is null || avatars is null ? Array.Empty<string>() : avatars.HiddenSlots(entry),
                body_drawn = avatars?.AgentDrawn(av.AgentId) ?? false,
                presented = worn,
                saved = saveSlot is null ? null : SaveBakePicture(avatars, prims, av.AgentId, bakes.TryGetValue(saveSlot, out var bakeId) ? bakeId : Guid.Empty, saveSlot),
                presenter = avatars?.ProbeOf(av.AgentId),
                appearances = world.AppearanceReceipts(av.AgentId).Select(r => new
                {
                    at = r.Utc.ToString("HH:mm:ss.fff"), version = r.AppearanceVersion, cof = r.CofVersion,
                    bakes = r.RawBakes.OrderBy(b => b.Key).Select(b => new { slot = b.Key, id = b.Value == Guid.Empty ? "" : Short(b.Value) }).ToArray(),
                }).ToArray(),
                pieces,
            });
        }
        // the sky frame the view is lit with now: sun direction (region axes), its elevation, the sun and ambient colours and the probe ambiance
        var sky = _presenter?.Environment?.Frame is { } f ? new
        {
            sun = new[] { f.SunDirection.X, f.SunDirection.Y, f.SunDirection.Z },
            sun_elevation_deg = Elevation(f.SunDirection),
            sun_colour = new[] { f.SunColor.X, f.SunColor.Y, f.SunColor.Z },
            ambient = new[] { f.Ambient.X, f.Ambient.Y, f.Ambient.Z },
            probe_ambiance = f.ProbeAmbiance,
            key = f.Key,
            source = _presenter?.EnvironmentSource,
            cycle = _presenter?.CurrentEnvironment is { } env ? new
            {
                day_length = env.DayLength, day_offset = env.DayOffset,
                fraction_now = Math.Round(env.DayFraction(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0), 4),
                frames = (env.Sky.IsDefault ? [] : env.Sky).Select(k => new
                {
                    key = k.Key, sun = new[] { k.SunDirection.X, k.SunDirection.Y, k.SunDirection.Z },
                    sun_elevation_deg = Elevation(k.SunDirection),
                }).ToArray(),
            } : null,
        } : null;
        return new { avatars = list, sky };
    }

    /// <summary>A sun direction's elevation in degrees, rounded; 0 for a direction of no length (so the readout never carries a value JSON cannot hold).</summary>
    private static double Elevation(System.Numerics.Vector3 d)
    {
        float len = d.Length();
        return len > 1e-6f && float.IsFinite(len) ? Math.Round(Math.Asin(Math.Clamp(d.Z / len, -1f, 1f)) * 180 / Math.PI, 1) : 0;
    }

    /// <summary>Writes the picture of an avatar's bake in a slot, as the engine holds it, to the log folder (never anywhere else). Null when it has none or no folder is named.</summary>
    private object? SaveBakePicture(Viewer.Godot.Presentation.AvatarPresenter? avatars, Viewer.Godot.Presentation.PrimLayer prims, Guid agent, Guid bakeId, string slot)
    {
        string? logDir = Viewer.Core.Diagnostics.FrameBurstTarget.Resolve(System.Environment.GetEnvironmentVariable("NV_LOG_DIR"),
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), out _);
        if (logDir is not null) logDir = Viewer.Core.Diagnostics.FrameBurstTarget.Resolve(logDir, global::Godot.OS.GetUserDataDir(), out _);
        // the picture a worn mesh body's bake-on-mesh face draws is the layer's texture for the bake's id; the system body's own is the avatar's (small when that body is hidden): the larger one is kept
        var fromLayer = bakeId == Guid.Empty ? null : prims.TexturePicture(bakeId);
        var fromBody = avatars?.BakePicture(agent, slot);
        var image = fromLayer is not null && (fromBody is null || fromLayer.GetWidth() >= fromBody.GetWidth()) ? fromLayer : fromBody;
        if (logDir is null || image is null) return null;
        var folder = System.IO.Path.Combine(logDir, "bake-pictures");
        System.IO.Directory.CreateDirectory(folder);
        var file = System.IO.Path.Combine(folder, $"server-{slot}-{agent.ToString("N")[..8]}-{image.GetWidth()}.png");
        image.SavePng(file);
        return new { file, width = image.GetWidth(), height = image.GetHeight() };
    }

    /// <summary>A face's material references and, where the region has sent them, the legacy material's values as the grid sent them (0-255 for the byte fields).</summary>
    private static object MaterialOf(Viewer.Core.World.WorldState world, FaceMaterial m)
    {
        static string Short(Guid g) => g == Guid.Empty ? "" : g.ToString("N")[..8];
        var legacy = m.MaterialId == Guid.Empty ? null : world.LegacyMaterial(m.MaterialId);
        return new
        {
            bump = m.Bump, shiny = m.Shiny, legacy_id = Short(m.MaterialId), pbr_id = Short(m.RenderMaterialId),
            legacy = legacy is null ? null : new
            {
                normal_map = Short(legacy.Normal.TextureId), specular_map = Short(legacy.Specular.TextureId),
                specular_colour = new[] { legacy.SpecularColor.R, legacy.SpecularColor.G, legacy.SpecularColor.B, legacy.SpecularColor.A },
                specular_exponent = legacy.SpecularExponent, environment_intensity = legacy.EnvironmentIntensity,
                alpha_mode = legacy.DiffuseAlphaMode.ToString(), alpha_cutoff = legacy.AlphaMaskCutoff,
            },
        };
    }
}
