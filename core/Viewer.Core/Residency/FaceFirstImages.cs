// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Residency;

/// <summary>One face's first image: when it was first in view and when it first showed its own image in view.</summary>
public readonly record struct FaceFirstImage(uint Object, int Face, Guid Texture, double InViewMs, double? ImageMs);

/// <summary>The faces' first images over a path: how many faces were in view, how many showed their image, the median
/// and p90 of the image times, the time until 90 % of the faces in view showed theirs (null when fewer ever did), and
/// every face's times.</summary>
public sealed record FaceFirstImageSummary(int InView, int Shown, double? P50, double? P90, double? At90PctOfInView, IReadOnlyList<FaceFirstImage> Faces)
{
    /// <summary>Faces whose first image was a fallback (a PBR face's legacy texture): the texture shown, and when the
    /// face first showed its own texture's image in view (null if it never did).</summary>
    public IReadOnlyList<FaceFallbackImage> Fallbacks { get; init; } = [];
}

/// <summary>A face whose first image was another texture than its own (a PBR face showing its legacy texture until its
/// base colour is on screen): that texture, and when the face first showed its own image in view.</summary>
public readonly record struct FaceFallbackImage(uint Object, int Face, Guid Shown, double? OwnImageMs);

/// <summary>
/// Each face's first image as the user sees it (measurement): for every face in view during a path, the time from the
/// path's start until it first shows its own image in view, whichever texture that is (the texture it draws when the
/// image arrives). A face whose texture is the null id has no image to wait for and is left out. A face is in view
/// when its residency user is (the user's bounds in the residency camera's view).
/// </summary>
public sealed class FaceFirstImages
{
    private sealed class Face
    {
        public double InViewMs;
        public double? ImageMs;
        public Guid Texture;
        public Guid Fallback;        // the texture shown as its first image when that was not its own
        public double? OwnImageMs;   // when it first showed its own image, after a fallback
    }

    private readonly Dictionary<TextureUser, Face> _faces = new();

    /// <summary>Faces seen in view so far.</summary>
    public int InView => _faces.Count;

    /// <summary>One face user in view at <paramref name="ms"/> from the path's start, drawing <paramref name="texture"/>;
    /// <paramref name="shown"/> when that texture has an image on screen. <paramref name="fallback"/>: another texture
    /// whose image the face shows instead while its own has none (a PBR face's legacy texture), or the null id.</summary>
    public void Observe(TextureUser user, Guid texture, bool shown, double ms, Guid fallback = default)
    {
        if (texture == Guid.Empty) return;
        if (!_faces.TryGetValue(user, out var f)) _faces[user] = f = new Face { InViewMs = ms, Texture = texture };
        if (f.ImageMs is not null)
        {
            if (shown && f.Fallback != Guid.Empty && f.OwnImageMs is null) f.OwnImageMs = ms;
            return;
        }
        f.Texture = texture;
        if (shown) f.ImageMs = ms;
        else if (fallback != Guid.Empty)
        {
            f.ImageMs = ms;
            f.Fallback = fallback;
        }
    }

    public FaceFirstImageSummary Summary()
    {
        var faces = _faces.OrderBy(kv => kv.Key.Id).ThenBy(kv => kv.Key.Face)
            .Select(kv => new FaceFirstImage(kv.Key.Id, kv.Key.Face, kv.Value.Texture, kv.Value.InViewMs, kv.Value.ImageMs)).ToList();
        var v = faces.Where(x => x.ImageMs.HasValue).Select(x => x.ImageMs!.Value).OrderBy(x => x).ToArray();
        int need = (int)Math.Ceiling(0.9 * faces.Count);
        double? Rank(double p) => v.Length == 0 ? null : v[Math.Clamp((int)Math.Ceiling(p * v.Length) - 1, 0, v.Length - 1)];
        var fallbacks = _faces.Where(kv => kv.Value.Fallback != Guid.Empty).OrderBy(kv => kv.Key.Id).ThenBy(kv => kv.Key.Face)
            .Select(kv => new FaceFallbackImage(kv.Key.Id, kv.Key.Face, kv.Value.Fallback, kv.Value.OwnImageMs)).ToList();
        return new FaceFirstImageSummary(faces.Count, v.Length, Rank(0.5), Rank(0.9), need > 0 && v.Length >= need ? v[need - 1] : null, faces) { Fallbacks = fallbacks };
    }
}
