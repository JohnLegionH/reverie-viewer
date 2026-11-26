// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace Viewer.Shell;

/// <summary>A camera view: where the camera stands and what it looks at, both from the avatar, x forward.</summary>
public sealed record CameraPreset(string Name, Vector3 Offset, Vector3 Focus);

/// <summary>
/// The camera views: the official viewer's front, side and rear (the values of its camera settings files) and the ones a person saves
/// under a name.
/// </summary>
public sealed class CameraPresets
{
    public static readonly CameraPreset Rear = new("rear", new Vector3(-3f, 0f, 0.75f), new Vector3(1f, 0f, 1f));
    public static readonly CameraPreset Front = new("front", new Vector3(2.2f, 0f, 0f), Vector3.Zero);
    public static readonly CameraPreset Side = new("side", new Vector3(-1f, 0.7f, 0.5f), new Vector3(1.5f, 0.7f, 1f));
    public static IReadOnlyList<CameraPreset> BuiltIn { get; } = [Front, Side, Rear];
    public const int NameLimit = 63;

    private readonly List<CameraPreset> _saved = [];

    /// <summary>The saved views, in the order of their names.</summary>
    public IReadOnlyList<CameraPreset> Saved => _saved;

    /// <summary>Saves a view under a name, replacing the one of that name. False for no name, a built-in view's name, or a view that
    /// is not one (no distance from the avatar, or a number that is not finite).</summary>
    public bool Save(string name, Vector3 offset, Vector3 focus)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0 || name.Length > NameLimit || BuiltIn.Any(b => Same(b.Name, name)))
            return false;
        if (!Finite(offset) || !Finite(focus) || offset.Length() < 0.01f)
            return false;
        _saved.RemoveAll(p => Same(p.Name, name));
        _saved.Add(new CameraPreset(name, offset, focus));
        _saved.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    public bool Remove(string name) => _saved.RemoveAll(p => Same(p.Name, name)) > 0;

    public CameraPreset? Find(string name) => _saved.FirstOrDefault(p => Same(p.Name, name));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private sealed record Kept(string? Name, float[]? Offset, float[]? Focus);

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string ToText() =>
        JsonSerializer.Serialize(_saved.Select(p => new Kept(p.Name, [p.Offset.X, p.Offset.Y, p.Offset.Z], [p.Focus.X, p.Focus.Y, p.Focus.Z])), Options);

    /// <summary>The saved views in <paramref name="text"/>; none when it cannot be read.</summary>
    public static CameraPresets FromText(string? text)
    {
        var list = new CameraPresets();
        if (string.IsNullOrWhiteSpace(text))
            return list;
        try
        {
            foreach (var k in JsonSerializer.Deserialize<List<Kept>>(text, Options) ?? [])
                if (k is { Name: { } name, Offset: [var x, var y, var z], Focus: [var a, var b, var c] })
                    list.Save(name, new Vector3(x, y, z), new Vector3(a, b, c));
        }
        catch (JsonException) { }
        return list;
    }
}
