// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Avatars;

using Viewer.Core.Inventory;

namespace Viewer.Core.Appearance;

/// <summary>
/// One wearable being edited: the saved asset, the edited copy, and the rules for changing it (values stay in range, a driver
/// passes its value to the parameters it drives, an empty texture slot is only allowed where the official editor allows it).
/// Nothing is sent; <see cref="ToBytes"/> gives the file to save. Not thread-safe: the interface thread owns it.
/// </summary>
public sealed class WearableEditor
{
    private const float Same = 1e-6f;
    private WearableAsset _saved;
    private readonly EditParamCatalog _catalog;
    private WearableAsset _cur;

    public WearableEditor(WearableAsset saved, EditParamCatalog catalog, bool canModify)
    {
        _saved = saved.Clone();
        _cur = saved.Clone();
        _catalog = catalog;
        CanModify = canModify;
    }

    /// <summary>Raised once after each edit that changed something, and after a revert.</summary>
    public event Action? Changed;

    public WearableType Type => _cur.Type;

    /// <summary>False when the agent may not modify the item: every edit is refused.</summary>
    public bool CanModify { get; }

    public string Name
    {
        get => _cur.Name;
        set
        {
            if (!CanModify || value == _cur.Name)
                return;
            _cur.Name = value;
            Changed?.Invoke();
        }
    }

    public bool IsDirty =>
        _cur.Name != _saved.Name
        || _cur.Params.Count != _saved.Params.Count
        || _cur.Params.Any(kv => !_saved.Params.TryGetValue(kv.Key, out var w) || MathF.Abs(w - kv.Value) > Same)
        || _cur.Textures.Count != _saved.Textures.Count
        || _cur.Textures.Any(kv => !_saved.Textures.TryGetValue(kv.Key, out var g) || g != kv.Value);

    /// <summary>The current weight of a parameter (its default when the file does not carry it; 0 for an id the definition lacks).</summary>
    public float Value(int paramId) =>
        _cur.Params.TryGetValue(paramId, out float w) ? w : _catalog.TryGet(paramId, out var p) ? p.Default : 0f;

    public void SetValue(int paramId, float value)
    {
        if (!CanModify || !float.IsFinite(value) || !_catalog.TryGet(paramId, out var p) || p.Wearable != Type)
            return;
        if (Set(p, value, 0))
            Changed?.Invoke();
    }

    private bool Set(EditParam p, float value, int depth)
    {
        value = Math.Clamp(value, p.Min, p.Max);
        bool changed = !_cur.Params.TryGetValue(p.Id, out float old) || MathF.Abs(old - value) > Same;
        _cur.Params[p.Id] = value;
        if (depth < 8)   // a cycle in the definition is cut
            foreach (var d in p.Driven)
                if (_catalog.TryGet(d.Id, out var driven) && driven.Wearable == Type)
                    changed |= Set(driven, AvatarShape.DrivenWeight(p.AsShapeParam(), d, driven.AsShapeParam(), value), depth + 1);
        return changed;
    }

    public Guid Texture(int slot) => _cur.Textures.TryGetValue(slot, out var g) ? g : Guid.Empty;

    /// <summary>Whether a slot holds its type's default texture (the official picker's Default button is on while it does not).</summary>
    public bool IsDefaultTexture(int slot) => WearableKinds.DefaultTexture(Type, slot) is var d && d != Guid.Empty && Texture(slot) == d;

    /// <summary>Puts a slot back to its type's default texture; false when it is already there, the item is read-only or the slot is not the type's.</summary>
    public bool ResetTexture(int slot)
    {
        var d = WearableKinds.DefaultTexture(Type, slot);
        if (!CanModify || d == Guid.Empty || IsDefaultTexture(slot))
            return false;
        SetTexture(slot, d);
        return true;
    }

    public void SetTexture(int slot, Guid texture)
    {
        if (!CanModify || WearableKinds.Slots(Type).FirstOrDefault(s => s.Index == slot) is not { } s)
            return;
        if (texture == Guid.Empty)
        {
            if (!s.AllowNone || !_cur.Textures.Remove(slot))
                return;
        }
        else
        {
            if (_cur.Textures.TryGetValue(slot, out var old) && old == texture)
                return;
            _cur.Textures[slot] = texture;
        }
        Changed?.Invoke();
    }

    public (float R, float G, float B) Colour(ColourSwatch swatch) => (Value(swatch.Red), Value(swatch.Green), Value(swatch.Blue));

    public void SetColour(ColourSwatch swatch, float r, float g, float b)
    {
        if (!CanModify || !WearableKinds.Swatches(Type).Contains(swatch) || !float.IsFinite(r) || !float.IsFinite(g) || !float.IsFinite(b))
            return;
        bool changed = false;
        foreach (var (id, v) in new[] { (swatch.Red, r), (swatch.Green, g), (swatch.Blue, b) })
        {
            float w = Math.Clamp(v, 0f, 1f);
            changed |= !_cur.Params.TryGetValue(id, out float old) || MathF.Abs(old - w) > Same;
            _cur.Params[id] = w;
        }
        if (changed)
            Changed?.Invoke();
    }

    /// <summary>Takes the edited wearable as the saved one (after a save went through): the editor is clean again and Revert returns here.</summary>
    public void Commit()
    {
        _saved = _cur.Clone();
    }

    /// <summary>Puts back every value, texture and the name as saved.</summary>
    public void Revert()
    {
        if (!IsDirty)
            return;
        _cur = _saved.Clone();
        Changed?.Invoke();
    }

    /// <summary>Every parameter weight now (the file's, with edits).</summary>
    public IReadOnlyDictionary<int, float> Values => new Dictionary<int, float>(_cur.Params);

    /// <summary>The wearable as edited. Read only: change it through the editor.</summary>
    public WearableAsset Current => _cur;

    public byte[] ToBytes() => _cur.ToBytes();
}
