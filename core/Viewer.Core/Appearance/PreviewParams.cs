// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Immutable;
using Viewer.Core.World;

namespace Viewer.Core.Appearance;

/// <summary>Turns edited weights into the visual parameter set the avatar is drawn from while an edit is open. Local only: nothing here is sent.</summary>
public static class PreviewParams
{
    /// <summary>The wire byte for a weight in a parameter's range (the inverse of the quantised read).</summary>
    public static byte ByteOf(float value, float min, float max)
    {
        if (!float.IsFinite(value) || !(max > min))
            return 0;
        float t = Math.Clamp((value - min) / (max - min), 0f, 1f);
        return (byte)MathF.Round(t * 255f, MidpointRounding.AwayFromZero);
    }

    /// <summary>The avatar's set with the edited weights written in. A weight whose parameter the set does not carry is ignored; the
    /// order and count never change.</summary>
    public static VisualParamSet Apply(VisualParamSet current, EditParamCatalog catalog, IReadOnlyDictionary<int, float> weights)
    {
        var raw = current.Raw.ToBuilder();
        bool changed = false;
        for (int i = 0; i < raw.Count && i < current.WireOrder.Length; i++)
        {
            if (!weights.TryGetValue(current.WireOrder[i], out float w) || !catalog.TryGet(current.WireOrder[i], out var p))
                continue;
            byte b = ByteOf(w, p.Min, p.Max);
            if (b != raw[i])
            {
                raw[i] = b;
                changed = true;
            }
        }
        return changed ? new VisualParamSet(raw.ToImmutable(), current.WireOrder) : current;
    }
}
